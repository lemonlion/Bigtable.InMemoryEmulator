using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for RowSet composition: keys, ranges, and combinations.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class RowSetCompositionTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "rsc-test";
    private const string CF = "cf";

    public RowSetCompositionTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        // Create rows: a through z (26 rows)
        for (char c = 'a'; c <= 'z'; c++)
            await Client.MutateRowAsync(TN, c.ToString(),
                Mutations.SetCell(CF, "c", $"val-{c}", new BigtableVersion(1000)));
        // Create numeric rows
        for (int i = 0; i < 20; i++)
            await Client.MutateRowAsync(TN, $"num-{i:D3}",
                Mutations.SetCell(CF, "c", $"n{i}", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null, long? limit = null)
    {
        var list = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: rows, filter: filter, rowsLimit: limit))
            list.Add(row);
        return list;
    }

    #region FromRowKeys

    [Fact]
    public async Task FromRowKeys_single()
    {
        var rows = await ReadAll(rows: RowSet.FromRowKeys("a"));
        rows.Should().ContainSingle().Which.Key.ToStringUtf8().Should().Be("a");
    }

    [Fact]
    public async Task FromRowKeys_multiple()
    {
        var rows = await ReadAll(rows: RowSet.FromRowKeys("a", "m", "z"));
        rows.Should().HaveCount(3);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeEquivalentTo(new[] { "a", "m", "z" });
    }

    [Fact]
    public async Task FromRowKeys_nonexistent()
    {
        var rows = await ReadAll(rows: RowSet.FromRowKeys("zzz-nonexistent"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task FromRowKeys_mixed_existing_nonexisting()
    {
        var rows = await ReadAll(rows: RowSet.FromRowKeys("a", "nonexistent", "z"));
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task FromRowKeys_duplicate_keys()
    {
        var rows = await ReadAll(rows: RowSet.FromRowKeys("a", "a", "a"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task FromRowKeys_out_of_order()
    {
        var rows = await ReadAll(rows: RowSet.FromRowKeys("z", "a", "m"));
        // Results should be in lexicographic order
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeEquivalentTo(new[] { "a", "m", "z" });
    }

    #endregion

    #region RowRange types

    [Fact]
    public async Task RowRange_closed_open()
    {
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("a", "d"));
        var rows = await ReadAll(rows: rowSet);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeEquivalentTo(new[] { "a", "b", "c" });
    }

    [Fact]
    public async Task RowRange_closed()
    {
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.Closed("a", "c"));
        var rows = await ReadAll(rows: rowSet);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeEquivalentTo(new[] { "a", "b", "c" });
    }

    [Fact]
    public async Task RowRange_open()
    {
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.Open("a", "d"));
        var rows = await ReadAll(rows: rowSet);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeEquivalentTo(new[] { "b", "c" });
    }

    [Fact]
    public async Task RowRange_open_closed()
    {
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.OpenClosed("a", "d"));
        var rows = await ReadAll(rows: rowSet);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeEquivalentTo(new[] { "b", "c", "d" });
    }

    #endregion

    #region Unbounded ranges

    [Fact]
    public async Task RowRange_from_start_to_key()
    {
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen(null, "c"));
        var rows = await ReadAll(rows: rowSet);
        // Should include 'a', 'b' (everything < 'c')
        rows.Select(r => r.Key.ToStringUtf8()).Should().Contain("a");
        rows.Select(r => r.Key.ToStringUtf8()).Should().Contain("b");
        rows.Select(r => r.Key.ToStringUtf8()).Should().NotContain("c");
    }

    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task RowRange_from_key_to_end()
    {
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.Closed("x", null));
        var rows = await ReadAll(rows: rowSet);
        rows.Select(r => r.Key.ToStringUtf8()).Should().Contain("x");
        rows.Select(r => r.Key.ToStringUtf8()).Should().Contain("y");
        rows.Select(r => r.Key.ToStringUtf8()).Should().Contain("z");
    }

    #endregion

    #region Multiple ranges

    [Fact]
    public async Task Multiple_non_overlapping_ranges()
    {
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("a", "c"));  // a, b
        rowSet.RowRanges.Add(RowRange.ClosedOpen("x", "z~")); // x, y, z
        var rows = await ReadAll(rows: rowSet);
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task Multiple_overlapping_ranges()
    {
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("a", "e")); // a, b, c, d
        rowSet.RowRanges.Add(RowRange.ClosedOpen("c", "g")); // c, d, e, f
        var rows = await ReadAll(rows: rowSet);
        // Should be de-duplicated: a, b, c, d, e, f
        rows.Should().HaveCount(6);
    }

    [Fact]
    public async Task Three_ranges()
    {
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.Closed("a", "a"));
        rowSet.RowRanges.Add(RowRange.Closed("m", "m"));
        rowSet.RowRanges.Add(RowRange.Closed("z", "z"));
        var rows = await ReadAll(rows: rowSet);
        rows.Should().HaveCount(3);
    }

    #endregion

    #region Keys + Ranges combined

    [Fact]
    public async Task Keys_and_range_combined()
    {
        var rowSet = RowSet.FromRowKeys("a");
        rowSet.RowRanges.Add(RowRange.ClosedOpen("x", "z~"));
        var rows = await ReadAll(rows: rowSet);
        rows.Should().HaveCount(4); // a + x, y, z
    }

    [Fact]
    public async Task Keys_overlapping_with_range()
    {
        var rowSet = RowSet.FromRowKeys("b", "c");
        rowSet.RowRanges.Add(RowRange.ClosedOpen("a", "d"));
        var rows = await ReadAll(rows: rowSet);
        rows.Should().HaveCount(3); // a, b, c (de-duplicated)
    }

    #endregion

    #region With limit

    [Fact]
    public async Task Range_with_limit()
    {
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("a", "z~"));
        var rows = await ReadAll(rows: rowSet, limit: 5);
        rows.Should().HaveCount(5);
        rows[0].Key.ToStringUtf8().Should().Be("a");
    }

    [Fact]
    public async Task Keys_with_limit()
    {
        var keys = Enumerable.Range(0, 10).Select(i => ((char)('a' + i)).ToString()).ToArray();
        var rowSet = RowSet.FromRowKeys(keys.Select(k => (BigtableByteString)k).ToArray());
        var rows = await ReadAll(rows: rowSet, limit: 3);
        rows.Should().HaveCount(3);
    }

    #endregion

    #region With filter

    [Fact]
    public async Task Range_with_filter()
    {
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("num-000", "num-999"));
        var rows = await ReadAll(rows: rowSet, filter: RowFilters.ValueRegex("n[0-4]"));
        rows.Should().HaveCount(5); // n0, n1, n2, n3, n4
    }

    [Fact]
    public async Task Keys_with_strip_value()
    {
        var rows = await ReadAll(
            rows: RowSet.FromRowKeys("a", "b"),
            filter: RowFilters.StripValueTransformer());
        rows.Should().HaveCount(2);
        foreach (var row in rows)
            row.Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    #endregion

    #region Empty RowSet

    [Fact]
    public async Task Empty_rowset_returns_all()
    {
        var rows = await ReadAll(rows: new RowSet());
        rows.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Null_rowset_returns_all()
    {
        var rows = await ReadAll(rows: null);
        rows.Count.Should().Be(46); // 26 letters + 20 nums
    }

    #endregion

    #region Numeric key ranges

    [Fact]
    public async Task Numeric_key_range()
    {
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("num-005", "num-010"));
        var rows = await ReadAll(rows: rowSet);
        rows.Should().HaveCount(5); // 005, 006, 007, 008, 009
    }

    [Fact]
    public async Task Numeric_specific_keys()
    {
        var rows = await ReadAll(rows: RowSet.FromRowKeys("num-000", "num-019"));
        rows.Should().HaveCount(2);
    }

    #endregion
}

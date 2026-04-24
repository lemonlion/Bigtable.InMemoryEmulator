using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for reading rows with various row key and range combinations.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
///   "The row keys and/or ranges to read sequentially."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsKeyPatternTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "rd-key";
    private const string CF = "cf";

    public ReadRowsKeyPatternTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        // Seed rows with various key patterns
        var entries = new List<MutateRowsRequest.Types.Entry>();
        // Alphabetical keys
        foreach (var key in new[] { "alpha", "beta", "gamma", "delta", "epsilon" })
            entries.Add(Mutations.CreateEntry(key, Mutations.SetCell(CF, "c", key, new BigtableVersion(1000))));
        // Numeric prefix keys
        for (int i = 0; i < 20; i++)
            entries.Add(Mutations.CreateEntry($"num-{i:D3}", Mutations.SetCell(CF, "c", $"n{i}", new BigtableVersion(1000))));
        // Hierarchical keys
        foreach (var ns in new[] { "us", "eu" })
            for (int i = 0; i < 5; i++)
                entries.Add(Mutations.CreateEntry($"region#{ns}#item-{i}", Mutations.SetCell(CF, "c", $"{ns}-{i}", new BigtableVersion(1000))));
        await Client.MutateRowsAsync(TN, entries.ToArray());
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

    #region Specific row keys

    [Fact]
    public async Task Read_single_key()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("alpha"));
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("alpha");
    }

    [Fact]
    public async Task Read_multiple_specific_keys()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("alpha", "gamma", "epsilon"));
        rows.Should().HaveCount(3);
        rows.Select(r => r.Key.ToStringUtf8()).Should()
            .BeEquivalentTo(new[] { "alpha", "epsilon", "gamma" }); // sorted order
    }

    [Fact]
    public async Task Read_nonexistent_key_returns_empty()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("nonexistent"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Read_mix_of_existing_and_nonexistent_keys()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("alpha", "nonexistent", "beta"));
        rows.Should().HaveCount(2);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeEquivalentTo(new[] { "alpha", "beta" });
    }

    [Fact]
    public async Task Read_duplicate_keys_returns_once()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("alpha", "alpha", "alpha"));
        rows.Should().ContainSingle();
    }

    #endregion

    #region Row ranges

    [Fact]
    public async Task ClosedOpen_range()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("num-005", "num-010")));
        rows.Should().HaveCount(5);
        rows.First().Key.ToStringUtf8().Should().Be("num-005");
        rows.Last().Key.ToStringUtf8().Should().Be("num-009");
    }

    [Fact]
    public async Task Closed_range()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.Closed("num-005", "num-010")));
        rows.Should().HaveCount(6);
        rows.First().Key.ToStringUtf8().Should().Be("num-005");
        rows.Last().Key.ToStringUtf8().Should().Be("num-010");
    }

    [Fact]
    public async Task Open_range()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.Open("num-005", "num-010")));
        rows.Should().HaveCount(4);
        rows.First().Key.ToStringUtf8().Should().Be("num-006");
        rows.Last().Key.ToStringUtf8().Should().Be("num-009");
    }

    [Fact]
    public async Task OpenClosed_range()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.OpenClosed("num-005", "num-010")));
        rows.Should().HaveCount(5);
        rows.First().Key.ToStringUtf8().Should().Be("num-006");
        rows.Last().Key.ToStringUtf8().Should().Be("num-010");
    }

    [Fact]
    public async Task Range_with_no_match()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("zzz", "zzzz")));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Range_covering_all_numeric()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("num-", "num~")));
        rows.Should().HaveCount(20);
    }

    #endregion

    #region Multiple ranges

    [Fact]
    public async Task Two_disjoint_ranges()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(
            RowRange.ClosedOpen("num-000", "num-003"),
            RowRange.ClosedOpen("num-017", "num-020")));
        rows.Should().HaveCount(6); // 000,001,002 + 017,018,019
    }

    [Fact]
    public async Task Three_ranges()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(
            RowRange.ClosedOpen("num-000", "num-002"),
            RowRange.ClosedOpen("num-005", "num-007"),
            RowRange.ClosedOpen("num-018", "num-020")));
        rows.Should().HaveCount(6); // 2+2+2
    }

    [Fact]
    public async Task Overlapping_ranges()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(
            RowRange.ClosedOpen("num-003", "num-008"),
            RowRange.ClosedOpen("num-005", "num-010")));
        // Union: num-003 through num-009 = 7 distinct rows
        rows.Should().HaveCount(7);
    }

    #endregion

    #region Keys + ranges combined

    [Fact]
    public async Task Specific_keys_and_range()
    {
        var rowSet = new RowSet();
        rowSet.RowKeys.Add(ByteString.CopyFromUtf8("alpha"));
        rowSet.RowKeys.Add(ByteString.CopyFromUtf8("beta"));
        rowSet.RowRanges.Add(RowRange.ClosedOpen("num-000", "num-003"));
        var rows = await ReadAll(rowSet);
        rows.Should().HaveCount(5); // alpha, beta, num-000, num-001, num-002
    }

    [Fact]
    public async Task Key_within_range_not_duplicated()
    {
        var rowSet = new RowSet();
        rowSet.RowKeys.Add(ByteString.CopyFromUtf8("num-001"));
        rowSet.RowRanges.Add(RowRange.ClosedOpen("num-000", "num-003"));
        var rows = await ReadAll(rowSet);
        // num-001 is in both the key set and the range — should not be duplicated
        rows.Should().HaveCount(3);
    }

    #endregion

    #region Hierarchical key patterns

    [Fact]
    public async Task Range_prefix_for_region()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("region#eu#", "region#eu~")));
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task Range_prefix_all_regions()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("region#", "region~")));
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task Specific_hierarchical_key()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("region#us#item-3"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("us-3");
    }

    #endregion

    #region Limit interactions

    [Fact]
    public async Task Limit_with_specific_keys()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("alpha", "beta", "gamma", "delta"), limit: 2);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Limit_with_range()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("num-", "num~")), limit: 5);
        rows.Should().HaveCount(5);
        rows.First().Key.ToStringUtf8().Should().Be("num-000");
    }

    [Fact]
    public async Task Limit_0_returns_all()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("num-", "num~")), limit: 0);
        rows.Should().HaveCount(20);
    }

    [Fact]
    public async Task Limit_greater_than_result_count()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("alpha", "beta"), limit: 100);
        rows.Should().HaveCount(2);
    }

    #endregion

    #region Full table scans

    [Fact]
    public async Task Read_all_rows_returns_all()
    {
        var rows = await ReadAll();
        // alpha, beta, gamma, delta, epsilon + 20 nums + 10 regions = 35
        rows.Should().HaveCount(35);
    }

    [Fact]
    public async Task Full_scan_in_lexicographic_order()
    {
        var rows = await ReadAll();
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Full_scan_with_limit()
    {
        var rows = await ReadAll(limit: 10);
        rows.Should().HaveCount(10);
    }

    #endregion

    #region Row key regex filter

    [Fact]
    public async Task RowKeyRegex_prefix()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("num-.*"));
        rows.Should().HaveCount(20);
    }

    [Fact]
    public async Task RowKeyRegex_exact()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("alpha"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task RowKeyRegex_alternation()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("alpha|beta|gamma"));
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task RowKeyRegex_no_match()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("zzz.*"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task RowKeyRegex_with_range()
    {
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("num-", "num~")),
            RowFilters.RowKeyRegex("num-00[0-4]"));
        rows.Should().HaveCount(5);
    }

    #endregion

    #region Empty and edge cases

    [Fact]
    public async Task Empty_rowset_returns_all()
    {
        var rows = await ReadAll(new RowSet());
        rows.Should().HaveCount(35);
    }

    [Fact]
    public async Task Range_start_equals_end_closed()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.Closed("alpha", "alpha")));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Range_start_equals_end_open()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.Open("alpha", "alpha")));
        rows.Should().BeEmpty();
    }

    #endregion
}

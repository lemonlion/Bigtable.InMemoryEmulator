using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Advanced ReadRows integration tests — multiple ranges, prefix scans, pagination,
/// request stats, and edge cases around RowSet construction.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsAdvancedIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "readrows-adv-tests";
    private const string CF = "cf";

    public ReadRowsAdvancedIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        var c = Client;
        var tn = TN;
        // Seed rows: a, b, c, d, e, f, g, h, i, j
        foreach (var key in new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i", "j" })
            await c.MutateRowAsync(tn, new BigtableByteString(key),
                Mutations.SetCell(CF, "col", "val-" + key, new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null, long? rowsLimit = null)
    {
        var list = new List<Row>();
        var stream = Client.ReadRows(TN, rows: rows, filter: filter, rowsLimit: rowsLimit);
        var e = stream.GetAsyncEnumerator(default);
        while (await e.MoveNextAsync()) list.Add(e.Current);
        return list;
    }

    #region Multiple ranges

    [Fact]
    public async Task ReadRows_multiple_ranges()
    {
        // Ref: RowSet can contain multiple RowRanges
        var ranges = RowSet.FromRowRanges(
            RowRange.ClosedOpen("a", "c"),   // a, b
            RowRange.ClosedOpen("f", "h"));  // f, g
        var rows = await ReadAll(ranges);
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("a", "b", "f", "g");
    }

    [Fact]
    public async Task ReadRows_mixed_keys_and_ranges()
    {
        // Both specific keys and ranges in the same RowSet
        var rowSet = new RowSet();
        rowSet.RowKeys.Add(ByteString.CopyFromUtf8("a"));
        rowSet.RowKeys.Add(ByteString.CopyFromUtf8("j"));
        rowSet.RowRanges.Add(new Google.Cloud.Bigtable.V2.RowRange
        {
            StartKeyClosed = ByteString.CopyFromUtf8("d"),
            EndKeyOpen = ByteString.CopyFromUtf8("f"),
        });
        var rows = await ReadAll(rowSet);
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("a", "d", "e", "j");
    }

    [Fact]
    public async Task ReadRows_overlapping_ranges_no_duplicates()
    {
        var ranges = RowSet.FromRowRanges(
            RowRange.ClosedOpen("a", "d"),  // a, b, c
            RowRange.ClosedOpen("c", "f")); // c, d, e — overlaps at c
        var rows = await ReadAll(ranges);
        // Should not duplicate row "c"
        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().OnlyHaveUniqueItems();
        keys.Should().Contain("a");
        keys.Should().Contain("c");
        keys.Should().Contain("e");
    }

    #endregion

    #region Range boundary types

    [Fact]
    public async Task ReadRows_closed_closed_range()
    {
        var range = new Google.Cloud.Bigtable.V2.RowRange
        {
            StartKeyClosed = ByteString.CopyFromUtf8("c"),
            EndKeyClosed = ByteString.CopyFromUtf8("e"),
        };
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.Closed("c", "e")));
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("c", "d", "e");
    }

    [Fact]
    public async Task ReadRows_open_open_range()
    {
        var range = new Google.Cloud.Bigtable.V2.RowRange
        {
            StartKeyOpen = ByteString.CopyFromUtf8("c"),
            EndKeyOpen = ByteString.CopyFromUtf8("f"),
        };
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.Open("c", "f")));
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("d", "e");
    }

    [Fact]
    public async Task ReadRows_open_closed_range()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.OpenClosed("b", "d")));
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("c", "d");
    }

    [Fact]
    public async Task ReadRows_unbounded_start_range()
    {
        // No start key → from the beginning
        var range = new Google.Cloud.Bigtable.V2.RowRange
        {
            EndKeyOpen = ByteString.CopyFromUtf8("c"),
        };
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(range);
        var rows = await ReadAll(rowSet);
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("a", "b");
    }

    [Fact]
    public async Task ReadRows_unbounded_end_range()
    {
        // No end key → to the end
        var range = new Google.Cloud.Bigtable.V2.RowRange
        {
            StartKeyClosed = ByteString.CopyFromUtf8("i"),
        };
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(range);
        var rows = await ReadAll(rowSet);
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("i", "j");
    }

    #endregion

    #region Prefix scan

    [Fact]
    public async Task ReadRows_prefix_scan()
    {
        // Add rows with common prefix
        await Client.MutateRowAsync(TN, new BigtableByteString("user#1"),
            Mutations.SetCell(CF, "col", "u1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, new BigtableByteString("user#2"),
            Mutations.SetCell(CF, "col", "u2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, new BigtableByteString("user#3"),
            Mutations.SetCell(CF, "col", "u3", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, new BigtableByteString("zzz"),
            Mutations.SetCell(CF, "col", "other", new BigtableVersion(1000)));

        // Prefix "user#" → all rows starting with "user#"
        // Prefix range: ["user#", "user$") — $ is # + 1 in ASCII
        var prefix = "user#";
        var prefixEnd = prefix[..^1] + (char)(prefix[^1] + 1); // "user$"
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen(prefix, prefixEnd)));
        rows.Should().HaveCount(3);
        rows.Select(r => r.Key.ToStringUtf8()).Should().OnlyContain(k => k.StartsWith("user#"));
    }

    #endregion

    #region RowsLimit with ranges

    [Fact]
    public async Task ReadRows_limit_applied_across_ranges()
    {
        var ranges = RowSet.FromRowRanges(
            RowRange.ClosedOpen("a", "f"),   // a-e
            RowRange.ClosedOpen("g", "j"));  // g-i
        var rows = await ReadAll(ranges, rowsLimit: 3);
        rows.Should().HaveCount(3);
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task ReadRows_limit_1_returns_single_row()
    {
        var rows = await ReadAll(rowsLimit: 1);
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("a");
    }

    [Fact]
    public async Task ReadRows_limit_exceeds_row_count_returns_all()
    {
        var rows = await ReadAll(rowsLimit: 1000);
        rows.Should().HaveCount(10);
    }

    #endregion

    #region Empty and null RowSet

    [Fact]
    public async Task ReadRows_null_rowset_returns_all_rows()
    {
        var rows = await ReadAll();
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task ReadRows_empty_rowset_returns_all_rows()
    {
        var rows = await ReadAll(new RowSet());
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task ReadRows_nonexistent_keys_returns_empty()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("zzz", "yyy"));
        rows.Should().BeEmpty();
    }

    // Go emulator divergence: throws InvalidArgument for start_key == end_key range instead of returning empty.
    // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.RowRange
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task ReadRows_empty_range_returns_empty()
    {
        // Range where start > end
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("z", "a")));
        rows.Should().BeEmpty();
    }

    #endregion

    #region Filter + range combinations

    [Fact]
    public async Task ReadRows_range_with_value_filter()
    {
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("a", "f")),
            RowFilters.ValueExact("val-c"));
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("c");
    }

    [Fact]
    public async Task ReadRows_specific_keys_with_filter()
    {
        var rows = await ReadAll(
            RowSet.FromRowKeys("a", "b", "c"),
            RowFilters.ValueRegex("val-[ab]"));
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReadRows_limit_with_filter()
    {
        // Limit applies after filtering
        var rows = await ReadAll(filter: RowFilters.ValueRegex("val-[a-e]"), rowsLimit: 2);
        rows.Should().HaveCount(2);
    }

    #endregion

    #region Multi-family reads

    [Fact]
    public async Task ReadRows_row_with_multiple_families()
    {
        await _fixture.CreateTableAsync("multi-fam", new[] { "f1", "f2", "f3" });
        var tn = _fixture.GetTableName("multi-fam");
        var rk = new BigtableByteString("mf1");
        await Client.MutateRowAsync(tn, rk,
            Mutations.SetCell("f1", "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("f2", "b", "v2", new BigtableVersion(1000)),
            Mutations.SetCell("f3", "c", "v3", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(tn, rk);
        row.Should().NotBeNull();
        row!.Families.Should().HaveCount(3);
        row.Families.Select(f => f.Name).Should().BeInAscendingOrder();
    }

    #endregion
}

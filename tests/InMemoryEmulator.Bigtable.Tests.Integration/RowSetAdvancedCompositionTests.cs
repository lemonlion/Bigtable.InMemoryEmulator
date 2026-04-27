using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for ReadRows with complex RowSet compositions: multiple ranges,
/// ranges with keys, overlapping ranges, adjacent ranges, and edge cases.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
///   "rows: The row keys and/or ranges to read sequentially."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class RowSetAdvancedCompositionTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "rowset-adv";
    private const int RowCount = 100;

    public RowSetAdvancedCompositionTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        var entries = Enumerable.Range(0, RowCount).Select(i =>
            Mutations.CreateEntry($"rs-{i:D4}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))).ToArray();
        await _fixture.Client.MutateRowsAsync(TN, entries);
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<string>> ReadAllKeys(RowSet? rowSet = null, RowFilter? filter = null)
    {
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(TN, rowSet, filter: filter))
            keys.Add(row.Key.ToStringUtf8());
        return keys;
    }

    #region Multiple disjoint ranges

    [Fact]
    public async Task Three_disjoint_ranges()
    {
        var rowSet = RowSet.FromRowRanges(
            RowRange.Closed("rs-0000", "rs-0004"),
            RowRange.Closed("rs-0020", "rs-0024"),
            RowRange.Closed("rs-0050", "rs-0054"));
        var keys = await ReadAllKeys(rowSet);
        keys.Should().HaveCount(15);
    }

    [Fact]
    public async Task Five_single_row_ranges()
    {
        var rowSet = RowSet.FromRowRanges(
            RowRange.Closed("rs-0010", "rs-0010"),
            RowRange.Closed("rs-0020", "rs-0020"),
            RowRange.Closed("rs-0030", "rs-0030"),
            RowRange.Closed("rs-0040", "rs-0040"),
            RowRange.Closed("rs-0050", "rs-0050"));
        var keys = await ReadAllKeys(rowSet);
        keys.Should().HaveCount(5);
    }

    #endregion

    #region Overlapping ranges

    [Fact]
    public async Task Overlapping_ranges_no_duplicates()
    {
        // Ref: Bigtable deduplicates overlapping ranges
        var rowSet = RowSet.FromRowRanges(
            RowRange.Closed("rs-0010", "rs-0020"),
            RowRange.Closed("rs-0015", "rs-0025"));
        var keys = await ReadAllKeys(rowSet);
        keys.Should().HaveCount(16); // 0010..0025
        keys.Distinct().Should().HaveCount(keys.Count);
    }

    [Fact]
    public async Task Contained_range_no_duplicates()
    {
        var rowSet = RowSet.FromRowRanges(
            RowRange.Closed("rs-0010", "rs-0030"),
            RowRange.Closed("rs-0015", "rs-0020"));
        var keys = await ReadAllKeys(rowSet);
        keys.Should().HaveCount(21); // 0010..0030
        keys.Distinct().Should().HaveCount(keys.Count);
    }

    #endregion

    #region Adjacent ranges

    [Fact]
    public async Task Adjacent_closed_open_ranges()
    {
        var rowSet = RowSet.FromRowRanges(
            RowRange.ClosedOpen("rs-0010", "rs-0020"),
            RowRange.ClosedOpen("rs-0020", "rs-0030"));
        var keys = await ReadAllKeys(rowSet);
        keys.Should().HaveCount(20);
    }

    #endregion

    #region Keys and ranges combined

    [Fact]
    public async Task Keys_only()
    {
        var rowSet = RowSet.FromRowKeys("rs-0005", "rs-0010", "rs-0099");
        var keys = await ReadAllKeys(rowSet);
        keys.Should().HaveCount(3);
        keys.Should().BeEquivalentTo(new[] { "rs-0005", "rs-0010", "rs-0099" });
    }

    [Fact]
    public async Task Keys_and_range_combined()
    {
        var rowSet = new RowSet();
        rowSet.RowKeys.Add(ByteString.CopyFromUtf8("rs-0050"));
        rowSet.RowKeys.Add(ByteString.CopyFromUtf8("rs-0090"));
        rowSet.RowRanges.Add(RowRange.Closed("rs-0001", "rs-0003"));
        var keys = await ReadAllKeys(rowSet);
        keys.Should().HaveCount(5);
        keys.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Key_within_range_no_duplicate()
    {
        var rowSet = new RowSet();
        rowSet.RowKeys.Add(ByteString.CopyFromUtf8("rs-0015"));
        rowSet.RowRanges.Add(RowRange.Closed("rs-0010", "rs-0020"));
        var keys = await ReadAllKeys(rowSet);
        keys.Should().HaveCount(11); // range covers 0010..0020, key 0015 is within
        keys.Distinct().Should().HaveCount(keys.Count);
    }

    [Fact]
    public async Task Nonexistent_keys_in_set()
    {
        var rowSet = RowSet.FromRowKeys("rs-9997", "rs-9998", "rs-9999");
        var keys = await ReadAllKeys(rowSet);
        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task Mix_of_existing_and_nonexistent_keys()
    {
        var rowSet = RowSet.FromRowKeys("rs-0001", "no-exist", "rs-0050", "also-no");
        var keys = await ReadAllKeys(rowSet);
        keys.Should().HaveCount(2);
    }

    #endregion

    #region Empty and null RowSet

    [Fact]
    public async Task Empty_rowset_returns_all_rows()
    {
        // Ref: Empty RowSet means "read all rows"
        var keys = await ReadAllKeys(new RowSet());
        keys.Should().HaveCount(RowCount);
    }

    [Fact]
    public async Task Null_rowset_returns_all()
    {
        var keys = await ReadAllKeys(null);
        keys.Should().HaveCount(RowCount);
    }

    [Fact]
    public async Task Empty_range_returns_nothing()
    {
        var rowSet = RowSet.FromRowRanges(RowRange.Open("rs-0010", "rs-0010"));
        var keys = await ReadAllKeys(rowSet);
        keys.Should().BeEmpty();
    }

    #endregion

    #region Row order

    [Fact]
    public async Task Results_always_in_ascending_key_order()
    {
        // Even with ranges specified in reverse order
        var rowSet = RowSet.FromRowRanges(
            RowRange.Closed("rs-0050", "rs-0060"),
            RowRange.Closed("rs-0010", "rs-0020"));
        var keys = await ReadAllKeys(rowSet);
        keys.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Keys_returned_in_order_regardless_of_input_order()
    {
        var rowSet = RowSet.FromRowKeys("rs-0099", "rs-0001", "rs-0050", "rs-0025");
        var keys = await ReadAllKeys(rowSet);
        keys.Should().BeInAscendingOrder();
    }

    #endregion

    #region Prefix ranges

    [Fact]
    public async Task Row_range_prefix_pattern()
    {
        // Common pattern: read all rows with prefix "rs-001"
        var rowSet = RowSet.FromRowRanges(RowRange.ClosedOpen("rs-001", "rs-002"));
        var keys = await ReadAllKeys(rowSet);
        keys.Should().HaveCount(10); // rs-0010 through rs-0019
    }

    [Fact]
    public async Task Row_range_prefix_all_rows()
    {
        var rowSet = RowSet.FromRowRanges(RowRange.ClosedOpen("rs-", "rs."));
        var keys = await ReadAllKeys(rowSet);
        keys.Should().HaveCount(RowCount);
    }

    #endregion

    #region RowSet with filters

    [Fact]
    public async Task RowSet_with_value_filter()
    {
        // Read a range but only rows whose value matches
        var rowSet = RowSet.FromRowRanges(RowRange.Closed("rs-0010", "rs-0015"));
        var keys = await ReadAllKeys(rowSet, RowFilters.ValueRegex("v1[0-2]"));
        keys.Should().HaveCount(3); // v10, v11, v12
    }

    [Fact]
    public async Task RowSet_with_cells_per_row_limit()
    {
        var rowSet = RowSet.FromRowRanges(RowRange.Closed("rs-0000", "rs-0005"));
        var keys = await ReadAllKeys(rowSet, RowFilters.CellsPerRowLimit(1));
        keys.Should().HaveCount(6);
    }

    #endregion
}

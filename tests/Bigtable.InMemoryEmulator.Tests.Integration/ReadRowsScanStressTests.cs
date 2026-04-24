using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Stress tests for ReadRows with various scan patterns and edge cases.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsScanStressTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "read-scan";
    private const string CF = "cf";

    public ReadRowsScanStressTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        var client = _fixture.Client;
        var tn = _fixture.GetTableName(Table);

        // Seed 100 rows: rs-000 through rs-099
        var entries = Enumerable.Range(0, 100).Select(i =>
            Mutations.CreateEntry($"rs-{i:D3}",
                Mutations.SetCell(CF, "val", $"data-{i}", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "idx", $"{i}", new BigtableVersion(1000)))
        ).ToArray();
        await client.MutateRowsAsync(tn, entries);
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

    #region Full table scan

    [Fact]
    public async Task Full_scan_returns_all_100_rows()
    {
        var rows = await ReadAll();
        rows.Count.Should().BeGreaterThanOrEqualTo(100);
    }

    [Fact]
    public async Task Full_scan_rows_in_ascending_order()
    {
        var rows = await ReadAll();
        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().BeInAscendingOrder();
    }

    #endregion

    #region Limit

    [Fact]
    public async Task Limit_1_returns_first_row()
    {
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("rs-", "rs~")),
            limit: 1);
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("rs-000");
    }

    [Fact]
    public async Task Limit_10_returns_first_10()
    {
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("rs-", "rs~")),
            limit: 10);
        rows.Should().HaveCount(10);
        rows.First().Key.ToStringUtf8().Should().Be("rs-000");
        rows.Last().Key.ToStringUtf8().Should().Be("rs-009");
    }

    [Fact]
    public async Task Limit_50()
    {
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("rs-", "rs~")),
            limit: 50);
        rows.Should().HaveCount(50);
    }

    [Fact]
    public async Task Limit_exceeds_total_rows()
    {
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("rs-", "rs~")),
            limit: 500);
        rows.Should().HaveCount(100);
    }

    #endregion

    #region Specific row keys

    [Fact]
    public async Task Single_specific_key()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("rs-050"));
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("rs-050");
    }

    [Fact]
    public async Task Three_specific_keys()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("rs-010", "rs-050", "rs-090"));
        rows.Should().HaveCount(3);
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("rs-010", "rs-050", "rs-090");
    }

    [Fact]
    public async Task Nonexistent_key_returns_empty()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("rs-999"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Mix_existing_and_nonexistent_keys()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("rs-000", "rs-999", "rs-050"));
        rows.Should().HaveCount(2);
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("rs-000", "rs-050");
    }

    [Fact]
    public async Task Duplicate_keys_return_once()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("rs-005", "rs-005", "rs-005"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Ten_specific_keys()
    {
        var keys = Enumerable.Range(0, 10).Select(i => new BigtableByteString($"rs-{i * 10:D3}")).ToArray();
        var rows = await ReadAll(RowSet.FromRowKeys(keys));
        rows.Should().HaveCount(10);
    }

    #endregion

    #region Row ranges

    [Fact]
    public async Task ClosedOpen_range()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("rs-010", "rs-020")));
        rows.Should().HaveCount(10);
        rows.First().Key.ToStringUtf8().Should().Be("rs-010");
        rows.Last().Key.ToStringUtf8().Should().Be("rs-019");
    }

    [Fact]
    public async Task Closed_range()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.Closed("rs-010", "rs-015")));
        rows.Should().HaveCount(6);
    }

    [Fact]
    public async Task Open_range()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.Open("rs-010", "rs-015")));
        rows.Should().HaveCount(4); // 011, 012, 013, 014
    }

    [Fact]
    public async Task OpenClosed_range()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.OpenClosed("rs-010", "rs-015")));
        rows.Should().HaveCount(5); // 011, 012, 013, 014, 015
    }

    [Fact]
    public async Task Range_past_all_data()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("zz-000", "zz-999")));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Range_before_all_data()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("aa-000", "aa-999")));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Range_single_row()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.Closed("rs-050", "rs-050")));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Multiple_non_overlapping_ranges()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(
            RowRange.ClosedOpen("rs-000", "rs-005"),
            RowRange.ClosedOpen("rs-050", "rs-055")));
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task Multiple_adjacent_ranges()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(
            RowRange.ClosedOpen("rs-000", "rs-010"),
            RowRange.ClosedOpen("rs-010", "rs-020")));
        rows.Should().HaveCount(20);
    }

    [Fact]
    public async Task Five_ranges()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(
            RowRange.ClosedOpen("rs-000", "rs-005"),
            RowRange.ClosedOpen("rs-020", "rs-025"),
            RowRange.ClosedOpen("rs-040", "rs-045"),
            RowRange.ClosedOpen("rs-060", "rs-065"),
            RowRange.ClosedOpen("rs-080", "rs-085")));
        rows.Should().HaveCount(25);
    }

    #endregion

    #region Range with limit

    [Fact]
    public async Task Range_with_limit_5()
    {
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("rs-", "rs~")),
            limit: 5);
        rows.Should().HaveCount(5);
        rows.First().Key.ToStringUtf8().Should().Be("rs-000");
    }

    [Fact]
    public async Task Multiple_ranges_with_limit()
    {
        var rows = await ReadAll(
            RowSet.FromRowRanges(
                RowRange.ClosedOpen("rs-000", "rs-050"),
                RowRange.ClosedOpen("rs-050", "rs-100")),
            limit: 20);
        rows.Should().HaveCount(20);
    }

    #endregion

    #region Range with filter

    [Fact]
    public async Task Range_with_column_filter()
    {
        var filter = RowFilters.ColumnQualifierExact("val");
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("rs-000", "rs-010")),
            filter);
        rows.Should().HaveCount(10);
        foreach (var row in rows)
            row.Families[0].Columns.Should().ContainSingle()
                .Which.Qualifier.ToStringUtf8().Should().Be("val");
    }

    [Fact]
    public async Task Range_with_value_regex_filter()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("val"),
            RowFilters.ValueRegex("data-5.*"));
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("rs-", "rs~")),
            filter);
        // data-5, data-50..59
        rows.Count.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Range_with_strip_value()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("val"),
            RowFilters.StripValueTransformer());
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("rs-000", "rs-005")), filter);
        rows.Should().HaveCount(5);
        foreach (var row in rows)
            row.Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    #endregion

    #region Keys and ranges combined

    [Fact]
    public async Task Specific_keys_and_range_combined()
    {
        var rowSet = new RowSet();
        rowSet.RowKeys.Add(ByteString.CopyFromUtf8("rs-000"));
        rowSet.RowKeys.Add(ByteString.CopyFromUtf8("rs-099"));
        rowSet.RowRanges.Add(RowRange.ClosedOpen("rs-050", "rs-055"));
        var rows = await ReadAll(rowSet);
        rows.Should().HaveCount(7); // 2 specific + 5 range
    }

    #endregion

    #region Empty results

    [Fact]
    public async Task Value_filter_no_match()
    {
        var filter = RowFilters.ValueExact("nonexistent-value-xyz");
        var rows = await ReadAll(RowSet.FromRowKeys("rs-000"), filter);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Family_filter_no_match()
    {
        var filter = RowFilters.FamilyNameExact("nonexistent-family");
        var rows = await ReadAll(RowSet.FromRowKeys("rs-000"), filter);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Column_filter_no_match()
    {
        var filter = RowFilters.ColumnQualifierExact("nonexistent-col");
        var rows = await ReadAll(RowSet.FromRowKeys("rs-000"), filter);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Row_key_regex_no_match()
    {
        var filter = RowFilters.RowKeyRegex("zzz.*");
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("rs-", "rs~")), filter);
        rows.Should().BeEmpty();
    }

    #endregion

    #region Ordering guarantees

    [Fact]
    public async Task Rows_always_in_lexicographic_order()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("rs-", "rs~")));
        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Columns_in_lexicographic_order()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("rs-000"));
        var quals = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Specific_keys_returned_in_sorted_order()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("rs-099", "rs-000", "rs-050"));
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("rs-000", "rs-050", "rs-099");
    }

    #endregion
}

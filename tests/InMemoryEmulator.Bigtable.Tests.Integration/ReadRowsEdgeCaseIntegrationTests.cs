using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// ReadRows edge case integration tests — range boundary types, combined keys and ranges,
/// limits with filters, empty results, binary key ranges, and pagination scenarios.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.ReadRowsRequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsEdgeCaseIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "rr-edge-tests";
    private const string CF = "cf";

    public ReadRowsEdgeCaseIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
        // Seed 10 rows: row-00 through row-09
        var client = _fixture.Client;
        var tn = _fixture.GetTableName(Table);
        for (int i = 0; i < 10; i++)
        {
            await client.MutateRowAsync(tn, new BigtableByteString($"row-{i:D2}"),
                Mutations.SetCell(CF, "c", $"val-{i}", new BigtableVersion(1000)));
        }
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null, long? rowsLimit = null)
    {
        var list = new List<Row>();
        var stream = Client.ReadRows(TN, rows: rows, filter: filter, rowsLimit: rowsLimit);
        await foreach (var row in stream) list.Add(row);
        return list;
    }

    #region Range boundary types

    [Fact]
    public async Task ClosedOpen_range_includes_start_excludes_end()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.RowRange
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("row-02", "row-05")));
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("row-02", "row-03", "row-04");
    }

    [Fact]
    public async Task ClosedClosed_range_includes_both_ends()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.Closed("row-02", "row-05")));
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("row-02", "row-03", "row-04", "row-05");
    }

    [Fact]
    public async Task OpenOpen_range_excludes_both_ends()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.Open("row-02", "row-05")));
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("row-03", "row-04");
    }

    [Fact]
    public async Task OpenClosed_range_excludes_start_includes_end()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.OpenClosed("row-02", "row-05")));
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("row-03", "row-04", "row-05");
    }

    #endregion

    #region Unbounded ranges

    [Fact]
    public async Task Unbounded_start_closed_end_returns_from_beginning()
    {
        // RowRange with only end_key_closed set
        var range = new RowRange { EndKeyClosed = ByteString.CopyFromUtf8("row-02") };
        var rows = await ReadAll(RowSet.FromRowRanges(range));
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("row-00", "row-01", "row-02");
    }

    [Fact]
    public async Task Closed_start_unbounded_end_returns_to_end()
    {
        var range = new RowRange { StartKeyClosed = ByteString.CopyFromUtf8("row-07") };
        var rows = await ReadAll(RowSet.FromRowRanges(range));
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("row-07", "row-08", "row-09");
    }

    [Fact]
    public async Task Open_start_unbounded_end_returns_after_start()
    {
        var range = new RowRange { StartKeyOpen = ByteString.CopyFromUtf8("row-07") };
        var rows = await ReadAll(RowSet.FromRowRanges(range));
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("row-08", "row-09");
    }

    [Fact]
    public async Task Unbounded_start_open_end_returns_up_to_end()
    {
        var range = new RowRange { EndKeyOpen = ByteString.CopyFromUtf8("row-03") };
        var rows = await ReadAll(RowSet.FromRowRanges(range));
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("row-00", "row-01", "row-02");
    }

    [Fact]
    public async Task Fully_unbounded_range_returns_all_rows()
    {
        // An empty RowRange with no start/end returns all rows
        var range = new RowRange();
        var rows = await ReadAll(RowSet.FromRowRanges(range));
        rows.Should().HaveCount(10);
    }

    #endregion

    #region Multiple ranges

    [Fact]
    public async Task Two_non_overlapping_ranges()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(
            RowRange.ClosedOpen("row-01", "row-03"),
            RowRange.ClosedOpen("row-06", "row-08")));
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("row-01", "row-02", "row-06", "row-07");
    }

    [Fact]
    public async Task Three_non_overlapping_ranges()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(
            RowRange.Closed("row-00", "row-01"),
            RowRange.Closed("row-04", "row-05"),
            RowRange.Closed("row-08", "row-09")));
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal(
            "row-00", "row-01", "row-04", "row-05", "row-08", "row-09");
    }

    [Fact]
    public async Task Overlapping_ranges_do_not_produce_duplicates()
    {
        // row-02..04 and row-03..06 overlap at row-03 and row-04
        var rows = await ReadAll(RowSet.FromRowRanges(
            RowRange.Closed("row-02", "row-04"),
            RowRange.Closed("row-03", "row-06")));
        // Should be deduplicated
        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().Equal("row-02", "row-03", "row-04", "row-05", "row-06");
    }

    [Fact]
    public async Task Adjacent_ranges_cover_all_in_between()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(
            RowRange.ClosedOpen("row-00", "row-05"),
            RowRange.ClosedOpen("row-05", "row-10")));
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task Range_with_no_matching_rows_returns_empty()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(
            RowRange.Closed("zzz-01", "zzz-99")));
        rows.Should().BeEmpty();
    }

    #endregion

    #region Keys and ranges combined

    [Fact]
    public async Task Keys_and_ranges_combined()
    {
        var rowSet = new RowSet();
        rowSet.RowKeys.Add(ByteString.CopyFromUtf8("row-00"));
        rowSet.RowKeys.Add(ByteString.CopyFromUtf8("row-09"));
        rowSet.RowRanges.Add(RowRange.Closed("row-04", "row-06"));
        var rows = await ReadAll(rowSet);
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal(
            "row-00", "row-04", "row-05", "row-06", "row-09");
    }

    [Fact]
    public async Task Keys_overlapping_with_range_no_duplicates()
    {
        var rowSet = new RowSet();
        rowSet.RowKeys.Add(ByteString.CopyFromUtf8("row-03")); // also in range below
        rowSet.RowRanges.Add(RowRange.Closed("row-02", "row-04"));
        var rows = await ReadAll(rowSet);
        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().Equal("row-02", "row-03", "row-04");
    }

    [Fact]
    public async Task Keys_for_nonexistent_rows_are_skipped()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("row-01", "nonexistent", "row-05"));
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("row-01", "row-05");
    }

    [Fact]
    public async Task Single_key_that_exists()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("row-05"));
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("row-05");
    }

    [Fact]
    public async Task Single_key_that_does_not_exist()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("nonexistent"));
        rows.Should().BeEmpty();
    }

    #endregion

    #region Limits

    [Fact]
    public async Task Limit_0_returns_all_rows()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.ReadRowsRequest
        //   "rows_limit: 0 means no limit."
        var rows = await ReadAll(rowsLimit: 0);
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task Limit_1_returns_first_row()
    {
        var rows = await ReadAll(rowsLimit: 1);
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("row-00");
    }

    [Fact]
    public async Task Limit_5_returns_first_5()
    {
        var rows = await ReadAll(rowsLimit: 5);
        rows.Should().HaveCount(5);
        rows.Last().Key.ToStringUtf8().Should().Be("row-04");
    }

    [Fact]
    public async Task Limit_exceeds_total_rows()
    {
        var rows = await ReadAll(rowsLimit: 100);
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task Limit_with_range()
    {
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.Closed("row-03", "row-09")),
            rowsLimit: 3);
        rows.Should().HaveCount(3);
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("row-03", "row-04", "row-05");
    }

    [Fact]
    public async Task Limit_with_specific_keys()
    {
        var rows = await ReadAll(
            RowSet.FromRowKeys("row-00", "row-03", "row-06", "row-09"),
            rowsLimit: 2);
        rows.Should().HaveCount(2);
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("row-00", "row-03");
    }

    [Fact]
    public async Task Limit_with_filter()
    {
        // Write extra column to some rows
        await Client.MutateRowAsync(TN, "row-02",
            Mutations.SetCell(CF, "extra", "x", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "row-05",
            Mutations.SetCell(CF, "extra", "x", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "row-08",
            Mutations.SetCell(CF, "extra", "x", new BigtableVersion(1000)));

        var rows = await ReadAll(
            filter: RowFilters.ColumnQualifierExact("extra"),
            rowsLimit: 2);
        rows.Should().HaveCount(2);
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("row-02", "row-05");
    }

    #endregion

    #region Multiple versions in read results

    [Fact]
    public async Task ReadRows_returns_all_versions_per_cell()
    {
        var rk = "rr-multi-ver";
        for (int i = 1; i <= 3; i++)
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));

        var rows = await ReadAll(RowSet.FromRowKeys(rk));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task ReadRows_versions_filtered_by_CellsPerColumnLimit()
    {
        var rk = "rr-ver-limit";
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));

        var rows = await ReadAll(RowSet.FromRowKeys(rk), RowFilters.CellsPerColumnLimit(2));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(2);
        // Should be newest 2
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v5");
        rows[0].Families[0].Columns[0].Cells[1].Value.ToStringUtf8().Should().Be("v4");
    }

    #endregion

    #region Multi-family reads

    [Fact]
    public async Task ReadRows_returns_cells_from_all_families()
    {
        await Client.MutateRowAsync(TN, "rr-multifam",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "v2", new BigtableVersion(1000)));

        var rows = await ReadAll(RowSet.FromRowKeys("rr-multifam"));
        rows.Should().ContainSingle();
        rows[0].Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReadRows_family_filter_restricts_to_one_family()
    {
        await Client.MutateRowAsync(TN, "rr-famfilt",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "v2", new BigtableVersion(1000)));

        var rows = await ReadAll(
            RowSet.FromRowKeys("rr-famfilt"),
            RowFilters.FamilyNameExact("cf2"));
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be("cf2");
    }

    #endregion

    #region Empty result scenarios

    [Fact]
    public async Task ReadRows_with_filter_matching_nothing_returns_empty()
    {
        var rows = await ReadAll(filter: RowFilters.ValueRegex("NONEXISTENT_VALUE_PATTERN"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadRows_range_beyond_all_data()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.Closed("zzz-00", "zzz-99")));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadRows_range_before_all_data()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.Closed("aaa-00", "aaa-99")));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadRows_empty_table()
    {
        await _fixture.CreateTableAsync("rr-empty-table", new[] { CF });
        var emptyTn = _fixture.GetTableName("rr-empty-table");
        var list = new List<Row>();
        await foreach (var row in Client.ReadRows(emptyTn)) list.Add(row);
        list.Should().BeEmpty();
    }

    #endregion

    #region ReadRows with multiple data shapes

    [Fact]
    public async Task ReadRows_row_with_many_columns()
    {
        var rk = "rr-many-cols";
        var mutations = Enumerable.Range(0, 20)
            .Select(i => Mutations.SetCell(CF, $"col-{i:D2}", $"val-{i}", new BigtableVersion(1000)))
            .ToArray();
        await Client.MutateRowAsync(TN, rk, mutations);

        var rows = await ReadAll(RowSet.FromRowKeys(rk));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns.Should().HaveCount(20);
        // Columns should be in lexicographic order
        var quals = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task ReadRows_rows_with_varying_column_counts()
    {
        await Client.MutateRowAsync(TN, "rr-vary-1",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "rr-vary-2",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var rows = await ReadAll(RowSet.FromRowKeys("rr-vary-1", "rr-vary-2"));
        rows.Should().HaveCount(2);
        rows[0].Families[0].Columns.Should().HaveCount(1);
        rows[1].Families[0].Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task ReadRows_binary_value_roundtrip()
    {
        var binaryValue = new byte[] { 0x00, 0x01, 0xFF, 0xFE, 0x80, 0x7F };
        await Client.MutateRowAsync(TN, "rr-binary-val",
            Mutations.SetCell(CF, "c", ByteString.CopyFrom(binaryValue), new BigtableVersion(1000)));

        var rows = await ReadAll(RowSet.FromRowKeys("rr-binary-val"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells[0].Value.ToByteArray().Should().Equal(binaryValue);
    }

    [Fact]
    public async Task ReadRows_empty_value_roundtrip()
    {
        await Client.MutateRowAsync(TN, "rr-empty-val",
            Mutations.SetCell(CF, "c", ByteString.Empty, new BigtableVersion(1000)));

        var rows = await ReadAll(RowSet.FromRowKeys("rr-empty-val"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    #endregion
}

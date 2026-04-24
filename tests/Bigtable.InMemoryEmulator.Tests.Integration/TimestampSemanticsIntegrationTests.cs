using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Timestamp semantics integration tests — server-assigned timestamps, precision,
/// ordering, range boundaries, interactions with mutations and reads.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.Mutation.SetCell
///   "timestamp_micros: … The timestamp must be set to -1 for server-assigned timestamps."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class TimestampSemanticsIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "ts-sem-tests";
    private const string CF = "cf";

    public TimestampSemanticsIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private BigtableServiceApiClient ApiClient => _fixture.ServiceApiClient;

    /// <summary>
    /// Create a raw MutateRowRequest with an explicit timestamp.
    /// Used for timestamp values (like 0) that the high-level BigtableClient doesn't expose directly.
    /// </summary>
    private MutateRowRequest RawMutateRequest(string rowKey, string family, string col, string value, long timestampMicros) =>
        new()
        {
            TableName = TN.ToString(),
            RowKey = ByteString.CopyFromUtf8(rowKey),
            Mutations =
            {
                new Mutation
                {
                    SetCell = new Mutation.Types.SetCell
                    {
                        FamilyName = family,
                        ColumnQualifier = ByteString.CopyFromUtf8(col),
                        Value = ByteString.CopyFromUtf8(value),
                        TimestampMicros = timestampMicros,
                    }
                }
            }
        };

    private static RowFilter TsRange(long startMicros = 0, long endMicros = 0)
    {
        var filter = new RowFilter { TimestampRangeFilter = new TimestampRange() };
        if (startMicros > 0) filter.TimestampRangeFilter.StartTimestampMicros = startMicros;
        if (endMicros > 0) filter.TimestampRangeFilter.EndTimestampMicros = endMicros;
        return filter;
    }

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null, long? rowsLimit = null)
    {
        var list = new List<Row>();
        var stream = Client.ReadRows(TN, rows: rows, filter: filter, rowsLimit: rowsLimit);
        await foreach (var row in stream) list.Add(row);
        return list;
    }

    #region Server-assigned timestamps

    [Fact]
    public async Task ServerAssigned_timestamp_is_positive()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.Mutation.SetCell
        //   "timestamp_micros: … -1 triggers server-assigned timestamp."
        // Note: BigtableClient converts -1 to a client-side timestamp before sending.
        await Client.MutateRowAsync(TN, "ts-srv-1",
            Mutations.SetCell(CF, "c", "val"));
        var row = await Client.ReadRowAsync(TN, "ts-srv-1");
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ServerAssigned_timestamp_is_millisecond_aligned()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.Mutation.SetCell
        //   "The server will round the value to the nearest 1000 microseconds."
        await Client.MutateRowAsync(TN, "ts-srv-align",
            Mutations.SetCell(CF, "c", "val"));
        var row = await Client.ReadRowAsync(TN, "ts-srv-align");
        var ts = row!.Families[0].Columns[0].Cells[0].TimestampMicros;
        (ts % 1000).Should().Be(0, "timestamps should be ms-aligned");
    }

    [Fact]
    public async Task ServerAssigned_two_writes_produce_different_or_equal_timestamps()
    {
        // Two consecutive writes without explicit timestamps should have monotonically non-decreasing timestamps
        await Client.MutateRowAsync(TN, "ts-srv-mono",
            Mutations.SetCell(CF, "c", "v1"));
        await Client.MutateRowAsync(TN, "ts-srv-mono",
            Mutations.SetCell(CF, "c2", "v2"));
        var row = await Client.ReadRowAsync(TN, "ts-srv-mono");
        var cells = row!.Families[0].Columns.SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(2);
        cells.All(c => c.TimestampMicros > 0).Should().BeTrue();
    }

    [Fact]
    public async Task ServerAssigned_multiple_columns_same_request()
    {
        // Multiple mutations in a single request all get timestamps assigned
        await Client.MutateRowAsync(TN, "ts-srv-multi",
            Mutations.SetCell(CF, "a", "v1"),
            Mutations.SetCell(CF, "b", "v2"));
        var row = await Client.ReadRowAsync(TN, "ts-srv-multi");
        row!.Families[0].Columns.Should().HaveCount(2);
        var timestamps = row.Families[0].Columns.SelectMany(c => c.Cells).Select(c => c.TimestampMicros).ToList();
        timestamps.All(ts => ts > 0).Should().BeTrue();
    }

    [Fact]
    public async Task ServerAssigned_on_same_column_creates_version()
    {
        // Two writes to same cell with different explicit timestamps guarantee two versions
        await Client.MutateRowAsync(TN, "ts-srv-samecol",
            Mutations.SetCell(CF, "c", "v1"));
        await Client.MutateRowAsync(TN, "ts-srv-samecol",
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "ts-srv-samecol");
        row!.Families[0].Columns[0].Cells.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    #endregion

    #region Explicit timestamp zero

    [Fact]
    public async Task Timestamp_zero_is_valid_explicit_value()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.Mutation.SetCell
        //   "Only -1 triggers server-assigned timestamp. 0 is a valid explicit timestamp."
        await ApiClient.MutateRowAsync(RawMutateRequest("ts-zero-valid", CF, "c", "val", 0));
        var row = await Client.ReadRowAsync(TN, "ts-zero-valid");
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(0);
    }

    [Fact]
    public async Task Timestamp_zero_coexists_with_other_versions()
    {
        await ApiClient.MutateRowAsync(RawMutateRequest("ts-zero-coex", CF, "c", "v0", 0));
        await Client.MutateRowAsync(TN, "ts-zero-coex",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "ts-zero-coex");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
        // Newer timestamp first (descending order)
        row.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(1000 * 1000);
        row.Families[0].Columns[0].Cells[1].TimestampMicros.Should().Be(0);
    }

    [Fact]
    public async Task Timestamp_zero_can_be_overwritten()
    {
        await ApiClient.MutateRowAsync(RawMutateRequest("ts-zero-ow", CF, "c", "old", 0));
        await ApiClient.MutateRowAsync(RawMutateRequest("ts-zero-ow", CF, "c", "new", 0));
        var row = await Client.ReadRowAsync(TN, "ts-zero-ow");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    #endregion

    #region Timestamp precision and boundaries

    [Fact]
    public async Task Explicit_millisecond_timestamps_stored_as_microseconds()
    {
        // BigtableVersion(N) => N milliseconds => N*1000 microseconds on wire
        var rk = new BigtableByteString("ts-ms-us");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "val", new BigtableVersion(42)));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(42_000);
    }

    [Fact]
    public async Task Timestamps_1ms_apart_are_distinct_versions()
    {
        var rk = new BigtableByteString("ts-1ms-apart");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(1001)));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task Large_timestamp_preserved()
    {
        // Test a timestamp far in the future (year ~2100)
        var rk = new BigtableByteString("ts-large");
        long futureMs = 4102444800000; // Jan 1 2100 UTC in ms
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "future", new BigtableVersion(futureMs)));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(futureMs * 1000);
    }

    [Fact]
    public async Task Multiple_timestamps_spanning_large_range()
    {
        var rk = new BigtableByteString("ts-range");
        var timestamps = new long[] { 1000, 100_000, 1_000_000, 1_000_000_000, 1_700_000_000_000 };
        foreach (var ts in timestamps)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, "c", $"v{ts}", new BigtableVersion(ts)));
        }
        var row = await Client.ReadRowAsync(TN, rk);
        var cellTs = row!.Families[0].Columns[0].Cells.Select(c => c.TimestampMicros).ToList();
        cellTs.Should().HaveCount(5);
        cellTs.Should().BeInDescendingOrder();
    }

    #endregion

    #region TimestampRange filter

    [Fact]
    public async Task TimestampRange_inclusive_start_exclusive_end()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.TimestampRange
        //   "Range of timestamps. start_timestamp_micros is inclusive, end_timestamp_micros exclusive."
        var rk = new BigtableByteString("ts-range-ie");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));

        var filter = TsRange(1_000_000, 3_000_000);
        var rows = await ReadAll(RowSet.FromRowKeys(rk), filter);
        rows.Should().ContainSingle();
        var cells = rows[0].Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2);
        cells.Select(c => c.Value.ToStringUtf8()).Should().Equal("v2", "v1");
    }

    [Fact]
    public async Task TimestampRange_start_equals_cell_timestamp_includes_cell()
    {
        var rk = new BigtableByteString("ts-range-start-eq");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "match", new BigtableVersion(5000)));

        var filter = TsRange(5_000_000, 6_000_000);
        var rows = await ReadAll(RowSet.FromRowKeys(rk), filter);
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("match");
    }

    [Fact]
    public async Task TimestampRange_end_equals_cell_timestamp_excludes_cell()
    {
        var rk = new BigtableByteString("ts-range-end-eq");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "excluded", new BigtableVersion(5000)));

        var filter = TsRange(4_000_000, 5_000_000); // exactly the cell's timestamp → excluded
        var rows = await ReadAll(RowSet.FromRowKeys(rk), filter);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task TimestampRange_no_matching_cells_returns_empty()
    {
        var rk = new BigtableByteString("ts-range-nomatch");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "val", new BigtableVersion(1000)));

        var filter = TsRange(5_000_000, 6_000_000);
        var rows = await ReadAll(RowSet.FromRowKeys(rk), filter);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task TimestampRange_filters_across_columns()
    {
        var rk = new BigtableByteString("ts-range-cols");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "va-old", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "va-new", new BigtableVersion(3000)),
            Mutations.SetCell(CF, "b", "vb-old", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "vb-new", new BigtableVersion(3000)));

        var filter = TsRange(2_000_000, 4_000_000);
        var rows = await ReadAll(RowSet.FromRowKeys(rk), filter);
        rows.Should().ContainSingle();
        var cols = rows[0].Families[0].Columns;
        cols.Should().HaveCount(2);
        cols.All(c => c.Cells.All(cell => cell.Value.ToStringUtf8().EndsWith("new"))).Should().BeTrue();
    }

    [Fact]
    public async Task TimestampRange_filters_across_families()
    {
        var rk = new BigtableByteString("ts-range-fams");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "v-cf-old", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v-cf-new", new BigtableVersion(3000)),
            Mutations.SetCell("cf2", "c", "v-cf2-old", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "v-cf2-new", new BigtableVersion(3000)));

        var filter = TsRange(2_000_000, 4_000_000);
        var rows = await ReadAll(RowSet.FromRowKeys(rk), filter);
        rows.Should().ContainSingle();
        rows[0].Families.Should().HaveCount(2);
        foreach (var fam in rows[0].Families)
        {
            fam.Columns[0].Cells.Should().ContainSingle();
            fam.Columns[0].Cells[0].Value.ToStringUtf8().Should().EndWith("new");
        }
    }

    #endregion

    #region Timestamp and delete interactions

    [Fact]
    public async Task Delete_specific_timestamp_preserves_others()
    {
        var rk = new BigtableByteString("ts-del-specific");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));

        // Delete only the version at timestamp 2000
        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(2001))));

        var row = await Client.ReadRowAsync(TN, rk);
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2);
        cells.Select(c => c.Value.ToStringUtf8()).Should().Equal("v3", "v1");
    }

    [Fact]
    public async Task Delete_time_range_removes_matching_versions()
    {
        var rk = new BigtableByteString("ts-del-range");
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));

        // Delete versions in range [2000ms, 4000ms) — removes v2, v3
        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(4000))));

        var row = await Client.ReadRowAsync(TN, rk);
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(3);
        cells.Select(c => c.Value.ToStringUtf8()).Should().Equal("v5", "v4", "v1");
    }

    [Fact]
    public async Task Delete_all_then_write_with_timestamp_creates_new()
    {
        var rk = new BigtableByteString("ts-del-rewrite");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromRow());
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Delete_from_column_unbounded_end_removes_from_start()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.Mutation.DeleteFromColumn
        //   "time_range: Optional time range to which the delete is restricted."
        var rk = new BigtableByteString("ts-del-unbounded-end");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));

        // Delete from timestamp 2000ms onward (unbounded end)
        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(2000), null)));

        var row = await Client.ReadRowAsync(TN, rk);
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().ContainSingle();
        cells[0].Value.ToStringUtf8().Should().Be("v1");
    }

    [Fact]
    public async Task Delete_from_column_unbounded_start_removes_up_to_end()
    {
        var rk = new BigtableByteString("ts-del-unbounded-start");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));

        // Delete up to timestamp 2000ms exclusive (unbounded start)
        // Range [0, 2000ms) in microseconds → deletes v1 (1000ms), preserves v2 (2000ms) and v3
        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(null, new BigtableVersion(2000))));

        var row = await Client.ReadRowAsync(TN, rk);
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2);
        cells.Select(c => c.Value.ToStringUtf8()).Should().Equal("v3", "v2");
    }

    #endregion

    #region Timestamp ordering across multiple columns/families

    [Fact]
    public async Task Cells_in_same_column_always_descending_timestamp()
    {
        var rk = new BigtableByteString("ts-order-col");
        // Write in scrambled order
        var timestamps = new long[] { 3000, 1000, 5000, 2000, 4000 };
        foreach (var ts in timestamps)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, "c", $"v{ts}", new BigtableVersion(ts)));
        }
        var row = await Client.ReadRowAsync(TN, rk);
        var cellTs = row!.Families[0].Columns[0].Cells.Select(c => c.TimestampMicros).ToList();
        cellTs.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Columns_sorted_lexicographically_regardless_of_timestamp()
    {
        var rk = new BigtableByteString("ts-order-cols");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "zzz", "val", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "aaa", "val", new BigtableVersion(5000)),
            Mutations.SetCell(CF, "mmm", "val", new BigtableVersion(3000)));
        var row = await Client.ReadRowAsync(TN, rk);
        var quals = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().Equal("aaa", "mmm", "zzz");
    }

    [Fact]
    public async Task Families_sorted_lexicographically_regardless_of_timestamp()
    {
        var rk = new BigtableByteString("ts-order-fams");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("cf2", "c", "val", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "val", new BigtableVersion(5000)));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.Select(f => f.Name).Should().Equal(CF, "cf2");
    }

    #endregion

    #region Timestamp with mutations batch (MutateRows)

    [Fact]
    public async Task MutateRows_batch_preserves_explicit_timestamps()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("ts-batch-1",
                Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000))),
            Mutations.CreateEntry("ts-batch-2",
                Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000))),
            Mutations.CreateEntry("ts-batch-3",
                Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000))),
        };
        await Client.MutateRowsAsync(TN, entries);

        var row1 = await Client.ReadRowAsync(TN, "ts-batch-1");
        var row2 = await Client.ReadRowAsync(TN, "ts-batch-2");
        var row3 = await Client.ReadRowAsync(TN, "ts-batch-3");
        row1!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(1_000_000);
        row2!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(2_000_000);
        row3!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(3_000_000);
    }

    [Fact]
    public async Task MutateRows_batch_same_row_different_timestamps_creates_versions()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("ts-batch-ver",
                Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000))),
            Mutations.CreateEntry("ts-batch-ver",
                Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000))),
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "ts-batch-ver");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
        row.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(2_000_000);
        row.Families[0].Columns[0].Cells[1].TimestampMicros.Should().Be(1_000_000);
    }

    #endregion

    #region Timestamp with ReadModifyWriteRow

    [Fact]
    public async Task ReadModifyWrite_append_gets_server_assigned_timestamp()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.ReadModifyWriteRule
        //   "The resulting cell's timestamp is server-assigned."
        var result = await Client.ReadModifyWriteRowAsync(TN, "ts-rmw-append",
            ReadModifyWriteRules.Append(CF, "c", "hello"));
        var cell = result.Row.Families[0].Columns[0].Cells[0];
        cell.TimestampMicros.Should().BeGreaterThan(0);
        (cell.TimestampMicros % 1000).Should().Be(0, "should be ms-aligned");
    }

    [Fact]
    public async Task ReadModifyWrite_increment_gets_server_assigned_timestamp()
    {
        var result = await Client.ReadModifyWriteRowAsync(TN, "ts-rmw-incr",
            ReadModifyWriteRules.Increment(CF, "c", 42));
        var cell = result.Row.Families[0].Columns[0].Cells[0];
        cell.TimestampMicros.Should().BeGreaterThan(0);
    }

    #endregion

    #region Timestamp with CheckAndMutateRow

    [Fact]
    public async Task CheckAndMutate_mutation_timestamps_are_preserved()
    {
        var rk = new BigtableByteString("ts-cam-ts");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "flag", "on", new BigtableVersion(1000)));
        await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "result", "done", new BigtableVersion(5000)) });
        var row = await Client.ReadRowAsync(TN, rk);
        var resultCol = row!.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "result");
        resultCol.Cells[0].TimestampMicros.Should().Be(5_000_000);
    }

    #endregion
}

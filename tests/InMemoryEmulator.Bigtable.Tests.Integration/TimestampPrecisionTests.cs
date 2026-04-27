using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for timestamp precision, server-assigned timestamps, and timestamp
/// boundary conditions.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#cell
///   "timestamp_micros: Timestamp that describes when the value was last modified."
///   "Must be at millisecond granularity, i.e., the number of microseconds must be a multiple of 1000."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class TimestampPrecisionTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "ts-prec";

    public TimestampPrecisionTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    #region Server-assigned timestamps

    [Fact]
    public async Task Server_assigned_timestamp_is_nonzero()
    {
        // BigtableVersion(-1) means server-assigned
        await Client.MutateRowAsync(TN, "ts-srv1",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(-1)));
        var row = await Client.ReadRowAsync(TN, "ts-srv1");
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Server_assigned_timestamps_are_nondecreasing()
    {
        for (int i = 0; i < 5; i++)
            await Client.MutateRowAsync(TN, $"ts-srv-u{i}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(-1)));

        var timestamps = new List<long>();
        for (int i = 0; i < 5; i++)
        {
            var row = await Client.ReadRowAsync(TN, $"ts-srv-u{i}");
            timestamps.Add(row!.Families[0].Columns[0].Cells[0].TimestampMicros);
        }
        // Server-assigned timestamps should be non-decreasing (same ms is OK)
        for (int i = 1; i < timestamps.Count; i++)
            timestamps[i].Should().BeGreaterThanOrEqualTo(timestamps[i - 1]);
    }

    [Fact]
    public async Task Server_assigned_timestamp_is_millisecond_granularity()
    {
        await Client.MutateRowAsync(TN, "ts-srv-ms",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(-1)));
        var row = await Client.ReadRowAsync(TN, "ts-srv-ms");
        var ts = row!.Families[0].Columns[0].Cells[0].TimestampMicros;
        (ts % 1000).Should().Be(0, "server timestamps should be at millisecond granularity");
    }

    #endregion

    #region Explicit timestamp values

    [Fact]
    public async Task Explicit_timestamp_preserved()
    {
        await Client.MutateRowAsync(TN, "ts-exp1",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(42000)));
        var row = await Client.ReadRowAsync(TN, "ts-exp1");
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(42_000_000);
    }

    [Fact]
    public async Task Timestamp_zero_stored_as_zero()
    {
        // Ref: timestamp 0 is valid — it means "unset" but the cell exists
        await Client.MutateRowAsync(TN, "ts-zero",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(0)));
        var row = await Client.ReadRowAsync(TN, "ts-zero");
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(0);
    }

    [Fact]
    public async Task Multiple_versions_ordered_descending()
    {
        await Client.MutateRowAsync(TN, "ts-order",
            Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "second", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "third", new BigtableVersion(3000)));
        var row = await Client.ReadRowAsync(TN, "ts-order");
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(3);
        cells[0].TimestampMicros.Should().Be(3_000_000); // newest first
        cells[1].TimestampMicros.Should().Be(2_000_000);
        cells[2].TimestampMicros.Should().Be(1_000_000);
    }

    [Fact]
    public async Task Same_timestamp_overwrites_value()
    {
        await Client.MutateRowAsync(TN, "ts-overwrite",
            Mutations.SetCell(CF, "c", "original", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ts-overwrite",
            Mutations.SetCell(CF, "c", "updated", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "ts-overwrite");
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().ContainSingle();
        cells[0].Value.ToStringUtf8().Should().Be("updated");
    }

    #endregion

    #region Timestamp boundary values

    [Fact]
    public async Task Very_small_timestamp()
    {
        await Client.MutateRowAsync(TN, "ts-small",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1)));
        var row = await Client.ReadRowAsync(TN, "ts-small");
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(1000);
    }

    [Fact]
    public async Task Large_timestamp_value()
    {
        // BigtableVersion takes milliseconds, max valid: 9223372036854775 (long.MaxValue / 1000)
        var largeTs = new BigtableVersion(1_000_000_000);
        await Client.MutateRowAsync(TN, "ts-large",
            Mutations.SetCell(CF, "c", "v", largeTs));
        var row = await Client.ReadRowAsync(TN, "ts-large");
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(1_000_000_000_000);
    }

    #endregion

    #region Timestamp filter interactions

    [Fact]
    public async Task TimestampRange_exact_match()
    {
        await Client.MutateRowAsync(TN, "ts-range1",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        // Range [2000ms, 3000ms) in micros = [2000000, 3000000)
        var filter = new RowFilter { TimestampRangeFilter = new TimestampRange { StartTimestampMicros = 2_000_000, EndTimestampMicros = 3_000_000 } };
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("ts-range1"), filter))
        {
            var cells = row.Families[0].Columns[0].Cells;
            cells.Should().ContainSingle();
            cells[0].Value.ToStringUtf8().Should().Be("v2");
        }
    }

    [Fact]
    public async Task TimestampRange_includes_start_excludes_end()
    {
        await Client.MutateRowAsync(TN, "ts-range2",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        var filter2 = new RowFilter { TimestampRangeFilter = new TimestampRange { StartTimestampMicros = 1_000_000, EndTimestampMicros = 3_000_000 } };
        var cells = new List<string>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("ts-range2"), filter2))
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    foreach (var cell in col.Cells)
                        cells.Add(cell.Value.ToStringUtf8());
        cells.Should().HaveCount(2);
        cells.Should().Contain("v1").And.Contain("v2");
    }

    [Fact]
    public async Task Server_timestamp_within_range()
    {
        await Client.MutateRowAsync(TN, "ts-srvrange",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(-1)));
        var row = await Client.ReadRowAsync(TN, "ts-srvrange");
        var ts = row!.Families[0].Columns[0].Cells[0].TimestampMicros;
        // Read with a wide range that should include the server timestamp
        var found = false;
        var tsFilter = new RowFilter { TimestampRangeFilter = new TimestampRange { StartTimestampMicros = ts - 1_000_000_000, EndTimestampMicros = ts + 1_000_000_000 } };
        await foreach (var r in Client.ReadRows(TN, RowSet.FromRowKeys("ts-srvrange"), tsFilter))
            found = true;
        found.Should().BeTrue();
    }

    #endregion

    #region Delete with timestamp ranges

    [Fact]
    public async Task Delete_specific_timestamp_version()
    {
        await Client.MutateRowAsync(TN, "ts-del1",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        // Delete timestamp range [2000ms, 3000ms)
        await Client.MutateRowAsync(TN, "ts-del1",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(3000))));
        var row = await Client.ReadRowAsync(TN, "ts-del1");
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2);
        cells.Select(c => c.Value.ToStringUtf8()).Should().BeEquivalentTo("v3", "v1");
    }

    [Fact]
    public async Task Delete_all_versions_of_column()
    {
        await Client.MutateRowAsync(TN, "ts-del2",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, "ts-del2",
            Mutations.DeleteFromColumn(CF, "c"));
        var row = await Client.ReadRowAsync(TN, "ts-del2");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Delete_from_row_removes_all_families()
    {
        await Client.MutateRowAsync(TN, "ts-del3",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ts-del3", Mutations.DeleteFromRow());
        var row = await Client.ReadRowAsync(TN, "ts-del3");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Delete_from_family_removes_all_columns()
    {
        await Client.MutateRowAsync(TN, "ts-del4",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ts-del4",
            Mutations.DeleteFromFamily(CF));
        var row = await Client.ReadRowAsync(TN, "ts-del4");
        row.Should().BeNull();
    }

    #endregion
}

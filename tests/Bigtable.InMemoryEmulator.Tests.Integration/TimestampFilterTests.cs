using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for timestamp-based filters including TimestampRange and versioning.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
///   "timestamp_range_filter: Matches only cells with timestamps within the given range."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class TimestampFilterTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "ts-filter";

    public TimestampFilterTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task TimestampRange_includes_start()
    {
        await Client.MutateRowAsync(TN, "ts-r1",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        var row = await Client.ReadRowAsync(TN, "ts-r1",
            new RowFilter
            {
                TimestampRangeFilter = new TimestampRange
                {
                    StartTimestampMicros = 2_000_000,
                    EndTimestampMicros = 4_000_000
                }
            });
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task TimestampRange_excludes_end()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#timestamprange
        //   "end_timestamp_micros: Exclusive upper bound."
        await Client.MutateRowAsync(TN, "ts-r2",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "ts-r2",
            new RowFilter
            {
                TimestampRangeFilter = new TimestampRange
                {
                    StartTimestampMicros = 1_000_000,
                    EndTimestampMicros = 2_000_000
                }
            });
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(1_000_000);
    }

    [Fact]
    public async Task TimestampRange_start_only()
    {
        await Client.MutateRowAsync(TN, "ts-r3",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        var row = await Client.ReadRowAsync(TN, "ts-r3",
            new RowFilter
            {
                TimestampRangeFilter = new TimestampRange
                {
                    StartTimestampMicros = 2_000_000
                }
            });
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task TimestampRange_end_only()
    {
        await Client.MutateRowAsync(TN, "ts-r4",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        var row = await Client.ReadRowAsync(TN, "ts-r4",
            new RowFilter
            {
                TimestampRangeFilter = new TimestampRange
                {
                    EndTimestampMicros = 2_000_000
                }
            });
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(1_000_000);
    }

    [Fact]
    public async Task TimestampRange_no_match()
    {
        await Client.MutateRowAsync(TN, "ts-r5",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "ts-r5",
            new RowFilter
            {
                TimestampRangeFilter = new TimestampRange
                {
                    StartTimestampMicros = 5_000_000,
                    EndTimestampMicros = 10_000_000
                }
            });
        row.Should().BeNull();
    }

    [Fact]
    public async Task TimestampRange_across_columns()
    {
        await Client.MutateRowAsync(TN, "ts-r6",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v", new BigtableVersion(3000)));
        var row = await Client.ReadRowAsync(TN, "ts-r6",
            new RowFilter
            {
                TimestampRangeFilter = new TimestampRange
                {
                    StartTimestampMicros = 2_000_000
                }
            });
        row!.Families[0].Columns.Should().ContainSingle();
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("b");
    }

    [Fact]
    public async Task CellsPerColumnLimit_returns_latest()
    {
        await Client.MutateRowAsync(TN, "ts-r7",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "ts-r7",
            RowFilters.CellsPerColumnLimit(1));
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task CellsPerColumnLimit_2_returns_two_latest()
    {
        await Client.MutateRowAsync(TN, "ts-r8",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        var row = await Client.ReadRowAsync(TN, "ts-r8",
            RowFilters.CellsPerColumnLimit(2));
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v3");
        row.Families[0].Columns[0].Cells[1].Value.ToStringUtf8().Should().Be("v2");
    }

    [Fact]
    public async Task Cells_returned_in_descending_timestamp_order()
    {
        await Client.MutateRowAsync(TN, "ts-r9",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(3000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "ts-r9");
        var timestamps = row!.Families[0].Columns[0].Cells.Select(c => c.TimestampMicros).ToList();
        timestamps.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task TimestampRange_with_chain()
    {
        await Client.MutateRowAsync(TN, "ts-r10",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(5000)),
            Mutations.SetCell(CF, "b", "v", new BigtableVersion(3000)));
        var row = await Client.ReadRowAsync(TN, "ts-r10",
            RowFilters.Chain(
                new RowFilter
                {
                    TimestampRangeFilter = new TimestampRange
                    {
                        StartTimestampMicros = 2_000_000,
                        EndTimestampMicros = 6_000_000
                    }
                },
                RowFilters.CellsPerColumnLimit(1)));
        var totalCells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();
        totalCells.Should().Be(2);
    }
}

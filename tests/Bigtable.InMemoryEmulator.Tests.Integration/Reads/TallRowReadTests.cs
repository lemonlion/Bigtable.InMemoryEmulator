using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class TallRowReadTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "tall-row";
    private const string CF = "cf";

    public TallRowReadTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        // Create a tall row with 20 versions
        for (int i = 1; i <= 20; i++)
        {
            await Client.MutateRowAsync(TN, "tall",
                Mutations.SetCell(CF, "c", $"ver-{i:D2}", new BigtableVersion(i * 1000)));
        }
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Read_all_versions()
    {
        var row = await Client.ReadRowAsync(TN, "tall");
        row.Should().NotBeNull();
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(20);
    }

    [Fact]
    public async Task CellsPerColumn_limits_versions()
    {
        var row = await Client.ReadRowAsync(TN, "tall", RowFilters.CellsPerColumnLimit(5));
        row.Should().NotBeNull();
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(5);
    }

    [Fact]
    public async Task CellsPerColumn_returns_newest_first()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
        // CellsPerColumnLimit returns the most recent N cells
        var row = await Client.ReadRowAsync(TN, "tall", RowFilters.CellsPerColumnLimit(3));
        var cells = row!.Families[0].Columns[0].Cells;
        cells[0].Value.ToStringUtf8().Should().Be("ver-20");
        cells[1].Value.ToStringUtf8().Should().Be("ver-19");
        cells[2].Value.ToStringUtf8().Should().Be("ver-18");
    }

    [Fact]
    public async Task Timestamp_range_filters_versions()
    {
        // BigtableVersion(i*1000) = i*1000 ms = i*1_000_000 micros
        // Select versions 5-15 → 5_000_000 to 15_000_001 micros
        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                StartTimestampMicros = 5_000_000,
                EndTimestampMicros = 15_000_001
            }
        };
        var row = await Client.ReadRowAsync(TN, "tall", filter);
        row.Should().NotBeNull();
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(11); // 5,6,...,15
    }

    [Fact]
    public async Task Timestamp_range_exclusive_end()
    {
        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                StartTimestampMicros = 5_000_000,
                EndTimestampMicros = 6_000_000
            }
        };
        var row = await Client.ReadRowAsync(TN, "tall", filter);
        row.Should().NotBeNull();
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().ContainSingle(); // only version 5
    }

    [Fact]
    public async Task Version_ordering_descending()
    {
        var row = await Client.ReadRowAsync(TN, "tall");
        var timestamps = row!.Families[0].Columns[0].Cells
            .Select(c => c.TimestampMicros)
            .ToList();
        timestamps.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task CellsPerRow_on_tall_row()
    {
        var row = await Client.ReadRowAsync(TN, "tall", RowFilters.CellsPerRowLimit(7));
        var cells = row!.Families.SelectMany(f => f.Columns.SelectMany(c => c.Cells)).ToList();
        cells.Should().HaveCount(7);
    }

    [Fact]
    public async Task CellsPerRowOffset_on_tall_row()
    {
        var row = await Client.ReadRowAsync(TN, "tall", RowFilters.CellsPerRowOffset(18));
        var cells = row!.Families.SelectMany(f => f.Columns.SelectMany(c => c.Cells)).ToList();
        cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task Value_regex_on_tall_row()
    {
        var row = await Client.ReadRowAsync(TN, "tall", RowFilters.ValueRegex("ver-0."));
        row.Should().NotBeNull();
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(9); // ver-01 through ver-09
    }

    [Fact]
    public async Task Chain_timestamp_then_limit()
    {
        var filter = RowFilters.Chain(
            new RowFilter
            {
                TimestampRangeFilter = new TimestampRange
                {
                    StartTimestampMicros = 1_000_000,
                    EndTimestampMicros = 20_000_001
                }
            },
            RowFilters.CellsPerColumnLimit(3));
        var row = await Client.ReadRowAsync(TN, "tall", filter);
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(3);
        // Should be the newest 3 within the range
        cells[0].Value.ToStringUtf8().Should().Be("ver-20");
    }

    [Fact]
    public async Task Overwrite_version_keeps_count()
    {
        // Write same timestamp — should overwrite, not add
        await Client.MutateRowAsync(TN, "tall",
            Mutations.SetCell(CF, "c", "overwritten", new BigtableVersion(10000)));
        var row = await Client.ReadRowAsync(TN, "tall");
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(20); // still 20, not 21
    }
}

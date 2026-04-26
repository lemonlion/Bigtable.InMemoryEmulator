using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CellTimestampBehaviorTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "cell-ts";
    private const string CF = "cf";

    public CellTimestampBehaviorTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Server_assigns_timestamp()
    {
        await Client.MutateRowAsync(TN, "r1", Mutations.SetCell(CF, "c", "v"));
        var row = await Client.ReadRowAsync(TN, "r1");
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Explicit_timestamp_roundtrip()
    {
        await Client.MutateRowAsync(TN, "r2", Mutations.SetCell(CF, "c", "v", new BigtableVersion(5000)));
        var row = await Client.ReadRowAsync(TN, "r2");
        // BigtableVersion(5000) = 5000ms = 5_000_000 micros
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(5_000_000);
    }

    [Fact]
    public async Task Timestamp_range_inclusive_start()
    {
        await Client.MutateRowAsync(TN, "r3", Mutations.SetCell(CF, "c", "v", new BigtableVersion(3000)));
        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange { StartTimestampMicros = 3_000_000 }
        };
        var row = await Client.ReadRowAsync(TN, "r3", filter);
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Timestamp_range_exclusive_end()
    {
        await Client.MutateRowAsync(TN, "r4", Mutations.SetCell(CF, "c", "v", new BigtableVersion(3000)));
        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                StartTimestampMicros = 3_000_000,
                EndTimestampMicros = 3_000_000
            }
        };
        var row = await Client.ReadRowAsync(TN, "r4", filter);
        row.Should().BeNull(); // exclusive end = start means empty range
    }

    [Fact]
    public async Task Timestamp_range_end_includes_version()
    {
        await Client.MutateRowAsync(TN, "r5", Mutations.SetCell(CF, "c", "v", new BigtableVersion(3000)));
        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                StartTimestampMicros = 3_000_000,
                EndTimestampMicros = 3_000_001
            }
        };
        var row = await Client.ReadRowAsync(TN, "r5", filter);
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_column_with_version_range()
    {
        await Client.MutateRowAsync(TN, "r6",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        // Delete [1ms, 2001ms) = [1000000, 2001000) micros → deletes v1 and v2
        await Client.MutateRowAsync(TN, "r6",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(1000), new BigtableVersion(2001))));
        var row = await Client.ReadRowAsync(TN, "r6");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v3");
    }

    [Fact]
    public async Task Timestamps_descending_order()
    {
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "r7", Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
        var row = await Client.ReadRowAsync(TN, "r7");
        var ts = row!.Families[0].Columns[0].Cells.Select(c => c.TimestampMicros).ToList();
        ts.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Timestamp_zero_version()
    {
        // BigtableVersion(0) is a valid version representing timestamp 0
        await Client.MutateRowAsync(TN, "r8", Mutations.SetCell(CF, "c", "v", new BigtableVersion(0)));
        var row = await Client.ReadRowAsync(TN, "r8");
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(0);
    }

    [Fact]
    public async Task Timestamp_large_value()
    {
        var ts = new BigtableVersion(1_000_000_000); // 1 billion ms
        await Client.MutateRowAsync(TN, "r9", Mutations.SetCell(CF, "c", "v", ts));
        var row = await Client.ReadRowAsync(TN, "r9");
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(1_000_000_000_000);
    }

    [Fact]
    public async Task CellsPerColumn_respects_timestamp_order()
    {
        for (int i = 1; i <= 10; i++)
            await Client.MutateRowAsync(TN, "r10", Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
        var row = await Client.ReadRowAsync(TN, "r10", RowFilters.CellsPerColumnLimit(3));
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(3);
        cells[0].Value.ToStringUtf8().Should().Be("v10"); // newest
        cells[1].Value.ToStringUtf8().Should().Be("v9");
        cells[2].Value.ToStringUtf8().Should().Be("v8");
    }
}

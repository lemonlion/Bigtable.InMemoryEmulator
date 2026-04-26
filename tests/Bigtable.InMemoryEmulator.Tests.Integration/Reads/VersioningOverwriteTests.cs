using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class VersioningOverwriteTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "ver-ow";
    private const string CF = "cf";

    public VersioningOverwriteTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Same_timestamp_overwrites()
    {
        await Client.MutateRowAsync(TN, "r1", Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "r1", Mutations.SetCell(CF, "c", "second", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "r1");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task Different_timestamps_create_versions()
    {
        await Client.MutateRowAsync(TN, "r2", Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "r2", Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "r2");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task Newest_version_first()
    {
        await Client.MutateRowAsync(TN, "r3",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "r3");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
        row.Families[0].Columns[0].Cells[1].Value.ToStringUtf8().Should().Be("old");
    }

    [Fact]
    public async Task CellsPerColumn_1_returns_latest()
    {
        await Client.MutateRowAsync(TN, "r4",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        var row = await Client.ReadRowAsync(TN, "r4", RowFilters.CellsPerColumnLimit(1));
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v3");
    }

    [Fact]
    public async Task Delete_specific_version()
    {
        await Client.MutateRowAsync(TN, "r5",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        // Delete version at 2000ms = [2000000, 2000001) micros
        await Client.MutateRowAsync(TN, "r5",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(2001))));
        var row = await Client.ReadRowAsync(TN, "r5");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
        row.Families[0].Columns[0].Cells.Select(c => c.Value.ToStringUtf8())
            .Should().BeEquivalentTo(new[] { "v3", "v1" });
    }

    [Fact]
    public async Task Many_versions_in_batch()
    {
        var mutations = Enumerable.Range(1, 15)
            .Select(i => Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)))
            .ToArray();
        await Client.MutateRowAsync(TN, "r6", mutations);
        var row = await Client.ReadRowAsync(TN, "r6");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(15);
    }

    [Fact]
    public async Task Overwrite_then_read_consistent()
    {
        for (int i = 0; i < 5; i++)
        {
            await Client.MutateRowAsync(TN, "r7", Mutations.SetCell(CF, "c", $"iter-{i}", new BigtableVersion(1000)));
        }
        var row = await Client.ReadRowAsync(TN, "r7");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("iter-4");
    }

    [Fact]
    public async Task Server_timestamp_always_increases()
    {
        // Server-assigned timestamps (no explicit version)
        await Client.MutateRowAsync(TN, "r8", Mutations.SetCell(CF, "c", "a"));
        await Client.MutateRowAsync(TN, "r8", Mutations.SetCell(CF, "c", "b"));
        var row = await Client.ReadRowAsync(TN, "r8");
        var ts = row!.Families[0].Columns[0].Cells.Select(c => c.TimestampMicros).ToList();
        // With server timestamps, each write gets a new timestamp
        ts.Should().HaveCount(2);
        ts[0].Should().BeGreaterThanOrEqualTo(ts[1]); // newest first
    }

    [Fact]
    public async Task Timestamp_microsecond_precision()
    {
        // BigtableVersion(1) = 1ms = 1000 micros
        await Client.MutateRowAsync(TN, "r9", Mutations.SetCell(CF, "c", "v", new BigtableVersion(1)));
        var row = await Client.ReadRowAsync(TN, "r9");
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(1000);
    }

    [Fact]
    public async Task Mixed_server_and_explicit_timestamps()
    {
        await Client.MutateRowAsync(TN, "r10", Mutations.SetCell(CF, "c", "explicit", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "r10", Mutations.SetCell(CF, "c", "server"));
        var row = await Client.ReadRowAsync(TN, "r10");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
    }
}

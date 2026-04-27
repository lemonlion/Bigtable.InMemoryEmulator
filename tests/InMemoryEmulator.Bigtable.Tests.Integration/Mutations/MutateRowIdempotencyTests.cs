using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutateRowIdempotencyTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "mut-idem";
    private const string CF = "cf";

    public MutateRowIdempotencyTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Repeated_set_same_version_is_idempotent()
    {
        for (int i = 0; i < 5; i++)
            await Client.MutateRowAsync(TN, "r1", Mutations.SetCell(CF, "c", "same", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "r1");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
    }

    [Fact]
    public async Task Repeated_delete_is_idempotent()
    {
        await Client.MutateRowAsync(TN, "r2", Mutations.SetCell(CF, "c", "val"));
        for (int i = 0; i < 3; i++)
            await Client.MutateRowAsync(TN, "r2", Mutations.DeleteFromRow());
        (await Client.ReadRowAsync(TN, "r2")).Should().BeNull();
    }

    [Fact]
    public async Task Repeated_delete_column_is_idempotent()
    {
        await Client.MutateRowAsync(TN, "r3", Mutations.SetCell(CF, "c", "val"));
        for (int i = 0; i < 3; i++)
            await Client.MutateRowAsync(TN, "r3", Mutations.DeleteFromColumn(CF, "c"));
        (await Client.ReadRowAsync(TN, "r3")).Should().BeNull();
    }

    [Fact]
    public async Task Repeated_delete_family_is_idempotent()
    {
        await Client.MutateRowAsync(TN, "r4", Mutations.SetCell(CF, "c", "val"));
        for (int i = 0; i < 3; i++)
            await Client.MutateRowAsync(TN, "r4", Mutations.DeleteFromFamily(CF));
        (await Client.ReadRowAsync(TN, "r4")).Should().BeNull();
    }

    [Fact]
    public async Task Repeated_batch_is_idempotent()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("r5", Mutations.SetCell(CF, "c", "val", new BigtableVersion(1000)))
        };
        for (int i = 0; i < 3; i++)
            await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "r5");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
    }

    [Fact]
    public async Task Write_read_write_read_consistent()
    {
        await Client.MutateRowAsync(TN, "r6", Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        var row1 = await Client.ReadRowAsync(TN, "r6");
        row1!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v1");
        await Client.MutateRowAsync(TN, "r6", Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));
        var row2 = await Client.ReadRowAsync(TN, "r6");
        row2!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v2");
    }

    [Fact]
    public async Task Delete_then_recreate()
    {
        await Client.MutateRowAsync(TN, "r7", Mutations.SetCell(CF, "c", "v1"));
        await Client.MutateRowAsync(TN, "r7", Mutations.DeleteFromRow());
        await Client.MutateRowAsync(TN, "r7", Mutations.SetCell(CF, "c", "v2"));
        var row = await Client.ReadRowAsync(TN, "r7");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Multiple_delete_recreate_cycles()
    {
        for (int i = 0; i < 5; i++)
        {
            await Client.MutateRowAsync(TN, "r8", Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion((i + 1) * 1000)));
            await Client.MutateRowAsync(TN, "r8", Mutations.DeleteFromRow());
        }
        await Client.MutateRowAsync(TN, "r8", Mutations.SetCell(CF, "c", "final", new BigtableVersion(10000)));
        var row = await Client.ReadRowAsync(TN, "r8");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("final");
    }
}

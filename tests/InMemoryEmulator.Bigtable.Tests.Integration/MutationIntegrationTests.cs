using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutationIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "mutation-tests";
    private const string Family = "cf";

    public MutationIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { Family, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task MutateRow_stores_cell()
    {
        var rowKey = new BigtableByteString("mut-r1");
        await Client.MutateRowAsync(TN, rowKey, Mutations.SetCell(Family, "col", "value1", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, rowKey);
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("value1");
    }

    [Fact]
    public async Task MutateRow_empty_key_throws()
    {
        var act = () => Client.MutateRowAsync(TN, new BigtableByteString(""),
            Mutations.SetCell(Family, "col", "val", new BigtableVersion(1000)));
        // SDK validates empty key on the client side before making the gRPC call
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task MutateRow_overwrites_same_cell()
    {
        var rowKey = new BigtableByteString("mut-ow");
        await Client.MutateRowAsync(TN, rowKey, Mutations.SetCell(Family, "col", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rowKey, Mutations.SetCell(Family, "col", "v2", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, rowKey);
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v2");
    }

    [Fact]
    public async Task MutateRow_different_timestamps_create_versions()
    {
        var rowKey = new BigtableByteString("mut-ver");
        await Client.MutateRowAsync(TN, rowKey, Mutations.SetCell(Family, "col", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rowKey, Mutations.SetCell(Family, "col", "v2", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, rowKey);
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v2");
    }

    [Fact]
    public async Task DeleteFromRow_removes_entire_row()
    {
        var rowKey = new BigtableByteString("mut-dr");
        await Client.MutateRowAsync(TN, rowKey, Mutations.SetCell(Family, "a", "1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rowKey, Mutations.DeleteFromRow());
        var row = await Client.ReadRowAsync(TN, rowKey);
        row.Should().BeNull();
    }

    [Fact]
    public async Task MutateRows_batch_succeeds()
    {
        var entries = new[]
        {
            Mutations.CreateEntry(new BigtableByteString("mut-b1"),
                Mutations.SetCell(Family, "col", "v1", new BigtableVersion(1000))),
            Mutations.CreateEntry(new BigtableByteString("mut-b2"),
                Mutations.SetCell(Family, "col", "v2", new BigtableVersion(1000))),
        };
        await Client.MutateRowsAsync(TN, entries);
        (await Client.ReadRowAsync(TN, new BigtableByteString("mut-b1"))).Should().NotBeNull();
        (await Client.ReadRowAsync(TN, new BigtableByteString("mut-b2"))).Should().NotBeNull();
    }
}

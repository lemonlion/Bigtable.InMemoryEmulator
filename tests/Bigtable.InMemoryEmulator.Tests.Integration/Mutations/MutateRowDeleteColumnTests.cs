using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutateRowDeleteColumnTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "mut-del-col";
    private const string CF = "cf";

    public MutateRowDeleteColumnTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Delete_single_column_all_versions()
    {
        await Client.MutateRowAsync(TN, "r1",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "b", "v3", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "r1", Mutations.DeleteFromColumn(CF, "a"));
        var row = await Client.ReadRowAsync(TN, "r1");
        row.Should().NotBeNull();
        var cols = row!.Families.SelectMany(f => f.Columns).ToList();
        cols.Should().ContainSingle();
        cols[0].Qualifier.ToStringUtf8().Should().Be("b");
    }

    [Fact]
    public async Task Delete_column_with_time_range()
    {
        await Client.MutateRowAsync(TN, "r2",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "a", "v3", new BigtableVersion(3000)));
        // Delete versions in [1000, 2001) micros — should remove v1 and v2
        await Client.MutateRowAsync(TN, "r2",
            Mutations.DeleteFromColumn(CF, "a",
                new BigtableVersionRange(new BigtableVersion(1000), new BigtableVersion(2001))));
        var row = await Client.ReadRowAsync(TN, "r2");
        row.Should().NotBeNull();
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().ContainSingle();
        cells[0].Value.ToStringUtf8().Should().Be("v3");
    }

    [Fact]
    public async Task Delete_from_family()
    {
        await Client.MutateRowAsync(TN, "r3",
            Mutations.SetCell(CF, "a", "v1"),
            Mutations.SetCell(CF, "b", "v2"));
        await Client.MutateRowAsync(TN, "r3", Mutations.DeleteFromFamily(CF));
        var row = await Client.ReadRowAsync(TN, "r3");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Delete_from_row()
    {
        await Client.MutateRowAsync(TN, "r4",
            Mutations.SetCell(CF, "a", "v1"),
            Mutations.SetCell(CF, "b", "v2"));
        await Client.MutateRowAsync(TN, "r4", Mutations.DeleteFromRow());
        var row = await Client.ReadRowAsync(TN, "r4");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Delete_nonexistent_column_is_noop()
    {
        await Client.MutateRowAsync(TN, "r5", Mutations.SetCell(CF, "a", "v1"));
        await Client.MutateRowAsync(TN, "r5", Mutations.DeleteFromColumn(CF, "nonexistent"));
        var row = await Client.ReadRowAsync(TN, "r5");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_nonexistent_row_is_noop()
    {
        // Deleting from a row that doesn't exist should not throw
        await Client.MutateRowAsync(TN, "ghost", Mutations.DeleteFromRow());
        var row = await Client.ReadRowAsync(TN, "ghost");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Set_and_delete_in_same_mutation()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowrequest
        // Mutations are applied in order
        await Client.MutateRowAsync(TN, "r6",
            Mutations.SetCell(CF, "a", "v1"),
            Mutations.SetCell(CF, "b", "v2"),
            Mutations.DeleteFromColumn(CF, "a"));
        var row = await Client.ReadRowAsync(TN, "r6");
        row.Should().NotBeNull();
        var cols = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().ContainSingle().Which.Should().Be("b");
    }

    [Fact]
    public async Task Delete_all_columns_removes_row()
    {
        await Client.MutateRowAsync(TN, "r7", Mutations.SetCell(CF, "only", "val"));
        await Client.MutateRowAsync(TN, "r7", Mutations.DeleteFromColumn(CF, "only"));
        var row = await Client.ReadRowAsync(TN, "r7");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Delete_column_preserves_other_rows()
    {
        await Client.MutateRowAsync(TN, "r8a", Mutations.SetCell(CF, "c", "v1"));
        await Client.MutateRowAsync(TN, "r8b", Mutations.SetCell(CF, "c", "v2"));
        await Client.MutateRowAsync(TN, "r8a", Mutations.DeleteFromColumn(CF, "c"));
        var row = await Client.ReadRowAsync(TN, "r8b");
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v2");
    }

    [Fact]
    public async Task Delete_family_preserves_other_rows()
    {
        await Client.MutateRowAsync(TN, "r9a", Mutations.SetCell(CF, "c", "v1"));
        await Client.MutateRowAsync(TN, "r9b", Mutations.SetCell(CF, "c", "v2"));
        await Client.MutateRowAsync(TN, "r9a", Mutations.DeleteFromFamily(CF));
        var rowB = await Client.ReadRowAsync(TN, "r9b");
        rowB.Should().NotBeNull();
    }
}

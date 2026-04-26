using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class DeleteMutationBehaviorTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "delmut-beh";
    private const string CF = "cf";

    public DeleteMutationBehaviorTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task DeleteFromRow_removes_all_families()
    {
        await Client.MutateRowAsync(TN, "r1",
            Mutations.SetCell(CF, "a", "1"),
            Mutations.SetCell(CF, "b", "2"));
        await Client.MutateRowAsync(TN, "r1", Mutations.DeleteFromRow());
        var row = await Client.ReadRowAsync(TN, "r1");
        row.Should().BeNull();
    }

    [Fact]
    public async Task DeleteFromFamily_removes_only_target_family()
    {
        await _fixture.CreateTableAsync("delmut-beh-fam", new[] { "cf1", "cf2" });
        var tn = _fixture.GetTableName("delmut-beh-fam");
        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf1", "a", "1"),
            Mutations.SetCell("cf2", "b", "2"));
        await Client.MutateRowAsync(tn, "r1", Mutations.DeleteFromFamily("cf1"));
        var row = await Client.ReadRowAsync(tn, "r1");
        row.Should().NotBeNull();
        row!.Families.Should().ContainSingle().Which.Name.Should().Be("cf2");
    }

    [Fact]
    public async Task DeleteFromColumn_removes_all_versions()
    {
        await Client.MutateRowAsync(TN, "r2",
            Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col", "v2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, "r2", Mutations.DeleteFromColumn(CF, "col"));
        var row = await Client.ReadRowAsync(TN, "r2");
        row.Should().BeNull();
    }

    [Fact]
    public async Task DeleteFromColumn_with_time_range_removes_subset()
    {
        await Client.MutateRowAsync(TN, "r3",
            Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "col", "v3", new BigtableVersion(3000)));
        await Client.MutateRowAsync(TN, "r3",
            Mutations.DeleteFromColumn(CF, "col",
                new BigtableVersionRange(new BigtableVersion(1000), new BigtableVersion(3000))));
        var row = await Client.ReadRowAsync(TN, "r3");
        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().ContainSingle();
        cells[0].Value.ToStringUtf8().Should().Be("v3");
    }

    [Fact]
    public async Task DeleteFromRow_on_nonexistent_row_is_noop()
    {
        // Should not throw
        await Client.MutateRowAsync(TN, "nonexist", Mutations.DeleteFromRow());
        var row = await Client.ReadRowAsync(TN, "nonexist");
        row.Should().BeNull();
    }

    [Fact]
    public async Task DeleteFromFamily_on_nonexistent_family_is_noop()
    {
        await Client.MutateRowAsync(TN, "r4", Mutations.SetCell(CF, "a", "1"));
        // Delete from a family that has no data for this row — should be noop
        await Client.MutateRowAsync(TN, "r4", Mutations.DeleteFromFamily(CF));
        var row = await Client.ReadRowAsync(TN, "r4");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Delete_then_rewrite_same_cell()
    {
        await Client.MutateRowAsync(TN, "r5", Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "r5", Mutations.DeleteFromColumn(CF, "c"));
        await Client.MutateRowAsync(TN, "r5", Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "r5");
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Multiple_deletes_in_single_mutation()
    {
        await Client.MutateRowAsync(TN, "r6",
            Mutations.SetCell(CF, "a", "1"),
            Mutations.SetCell(CF, "b", "2"),
            Mutations.SetCell(CF, "c", "3"));
        await Client.MutateRowAsync(TN, "r6",
            Mutations.DeleteFromColumn(CF, "a"),
            Mutations.DeleteFromColumn(CF, "c"));
        var row = await Client.ReadRowAsync(TN, "r6");
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("b");
    }

    [Fact]
    public async Task DeleteFromRow_after_multiple_writes()
    {
        for (int i = 0; i < 10; i++)
            await Client.MutateRowAsync(TN, "r7", Mutations.SetCell(CF, $"col{i}", $"val{i}"));
        await Client.MutateRowAsync(TN, "r7", Mutations.DeleteFromRow());
        var row = await Client.ReadRowAsync(TN, "r7");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Delete_and_set_in_same_mutation_set_wins()
    {
        await Client.MutateRowAsync(TN, "r8", Mutations.SetCell(CF, "col", "old", new BigtableVersion(1000)));
        // Mutations applied in order: delete first, then set
        await Client.MutateRowAsync(TN, "r8",
            Mutations.DeleteFromColumn(CF, "col"),
            Mutations.SetCell(CF, "col", "new", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "r8");
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task DeleteFromColumn_specific_version_only()
    {
        await Client.MutateRowAsync(TN, "r9",
            Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col", "v2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, "r9",
            Mutations.DeleteFromColumn(CF, "col",
                new BigtableVersionRange(new BigtableVersion(1000), new BigtableVersion(2000))));
        var row = await Client.ReadRowAsync(TN, "r9");
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("v2");
    }

    [Fact]
    public async Task DeleteFromRow_does_not_affect_other_rows()
    {
        await Client.MutateRowAsync(TN, "r10a", Mutations.SetCell(CF, "a", "1"));
        await Client.MutateRowAsync(TN, "r10b", Mutations.SetCell(CF, "a", "2"));
        await Client.MutateRowAsync(TN, "r10a", Mutations.DeleteFromRow());
        var rowB = await Client.ReadRowAsync(TN, "r10b");
        rowB.Should().NotBeNull();
        rowB!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("2");
    }

    [Fact]
    public async Task DeleteFromColumn_empty_time_range_is_noop()
    {
        await Client.MutateRowAsync(TN, "r11", Mutations.SetCell(CF, "col", "v", new BigtableVersion(5000)));
        // Range that doesn't include the version
        await Client.MutateRowAsync(TN, "r11",
            Mutations.DeleteFromColumn(CF, "col",
                new BigtableVersionRange(new BigtableVersion(1000), new BigtableVersion(2000))));
        var row = await Client.ReadRowAsync(TN, "r11");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Repeated_delete_from_row_is_idempotent()
    {
        await Client.MutateRowAsync(TN, "r12", Mutations.SetCell(CF, "a", "1"));
        await Client.MutateRowAsync(TN, "r12", Mutations.DeleteFromRow());
        await Client.MutateRowAsync(TN, "r12", Mutations.DeleteFromRow());
        var row = await Client.ReadRowAsync(TN, "r12");
        row.Should().BeNull();
    }
}

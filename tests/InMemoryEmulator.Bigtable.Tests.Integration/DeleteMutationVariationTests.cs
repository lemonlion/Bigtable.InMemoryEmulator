using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for DeleteFromRow, DeleteFromFamily, and DeleteFromColumn mutation semantics.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
///   "delete_from_row: Deletes cells from the entire row."
///   "delete_from_family: Deletes cells from a column family."
///   "delete_from_column: Deletes cells from a column."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class DeleteMutationVariationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string CF2 = "cf2";
    private const string Table = "del-var";

    public DeleteMutationVariationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task DeleteFromRow_removes_all_data()
    {
        await Client.MutateRowAsync(TN, "dv-r1",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "3", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "dv-r1", Mutations.DeleteFromRow());
        var row = await Client.ReadRowAsync(TN, "dv-r1");
        row.Should().BeNull();
    }

    [Fact]
    public async Task DeleteFromFamily_removes_only_target_family()
    {
        await Client.MutateRowAsync(TN, "dv-r2",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "b", "2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "dv-r2", Mutations.DeleteFromFamily(CF));
        var row = await Client.ReadRowAsync(TN, "dv-r2");
        row!.Families.Should().ContainSingle();
        row.Families[0].Name.Should().Be(CF2);
    }

    [Fact]
    public async Task DeleteFromColumn_removes_all_versions()
    {
        await Client.MutateRowAsync(TN, "dv-r3",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        await Client.MutateRowAsync(TN, "dv-r3", Mutations.DeleteFromColumn(CF, "c"));
        var row = await Client.ReadRowAsync(TN, "dv-r3");
        row.Should().BeNull();
    }

    [Fact]
    public async Task DeleteFromColumn_preserves_other_columns()
    {
        await Client.MutateRowAsync(TN, "dv-r4",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "dv-r4", Mutations.DeleteFromColumn(CF, "a"));
        var row = await Client.ReadRowAsync(TN, "dv-r4");
        row!.Families[0].Columns.Should().ContainSingle();
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("b");
    }

    [Fact]
    public async Task DeleteFromColumn_with_version_range()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
        //   "time_range: Optional. The range of timestamps within which cells should be deleted."
        await Client.MutateRowAsync(TN, "dv-r5",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        await Client.MutateRowAsync(TN, "dv-r5",
            Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(1000, 3000)));
        var row = await Client.ReadRowAsync(TN, "dv-r5");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v3");
    }

    [Fact]
    public async Task DeleteFromColumn_version_range_start_only()
    {
        await Client.MutateRowAsync(TN, "dv-r6",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        await Client.MutateRowAsync(TN, "dv-r6",
            Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(2000, null)));
        var row = await Client.ReadRowAsync(TN, "dv-r6");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v1");
    }

    [Fact]
    public async Task DeleteFromColumn_version_range_end_only()
    {
        await Client.MutateRowAsync(TN, "dv-r7",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        await Client.MutateRowAsync(TN, "dv-r7",
            Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(null, 2000)));
        var row = await Client.ReadRowAsync(TN, "dv-r7");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task DeleteFromFamily_then_recreate()
    {
        await Client.MutateRowAsync(TN, "dv-r8",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "dv-r8", Mutations.DeleteFromFamily(CF));
        await Client.MutateRowAsync(TN, "dv-r8",
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "dv-r8");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task DeleteFromRow_then_recreate()
    {
        await Client.MutateRowAsync(TN, "dv-r9",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "dv-r9", Mutations.DeleteFromRow());
        await Client.MutateRowAsync(TN, "dv-r9",
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "dv-r9");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Delete_and_set_in_same_mutation()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowrequest
        //   "Mutations are applied in order, meaning that earlier mutations can be masked by later ones."
        await Client.MutateRowAsync(TN, "dv-r10",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "dv-r10",
            Mutations.DeleteFromRow(),
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "dv-r10");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Set_and_delete_in_same_mutation_removes_cell()
    {
        await Client.MutateRowAsync(TN, "dv-r11",
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(1000)),
            Mutations.DeleteFromRow());
        var row = await Client.ReadRowAsync(TN, "dv-r11");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Delete_nonexistent_column_is_noop()
    {
        await Client.MutateRowAsync(TN, "dv-r12",
            Mutations.SetCell(CF, "a", "val", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "dv-r12",
            Mutations.DeleteFromColumn(CF, "nonexistent"));
        var row = await Client.ReadRowAsync(TN, "dv-r12");
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("a");
    }

    [Fact]
    public async Task Delete_nonexistent_family_is_noop()
    {
        await Client.MutateRowAsync(TN, "dv-r13",
            Mutations.SetCell(CF, "c", "val", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "dv-r13",
            Mutations.DeleteFromFamily(CF2));
        var row = await Client.ReadRowAsync(TN, "dv-r13");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_from_row_on_nonexistent_row_is_noop()
    {
        // Should not throw
        await Client.MutateRowAsync(TN, "dv-r14-missing", Mutations.DeleteFromRow());
        var row = await Client.ReadRowAsync(TN, "dv-r14-missing");
        row.Should().BeNull();
    }
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for delete mutation semantics: DeleteFromRow, DeleteFromFamily,
/// DeleteFromColumn with version ranges, and interactions with reads.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class DeleteMutationSemanticTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string CF2 = "cf2";
    private const string Table = "del-sem";

    public DeleteMutationSemanticTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    #region DeleteFromRow

    [Fact]
    public async Task DeleteFromRow_removes_all_data()
    {
        await Client.MutateRowAsync(TN, "ds-dfr1",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "3", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ds-dfr1", Mutations.DeleteFromRow());
        var row = await Client.ReadRowAsync(TN, "ds-dfr1");
        row.Should().BeNull();
    }

    [Fact]
    public async Task DeleteFromRow_on_nonexistent_row_succeeds()
    {
        // Should not throw
        await Client.MutateRowAsync(TN, "ds-dfr-none", Mutations.DeleteFromRow());
    }

    [Fact]
    public async Task DeleteFromRow_then_write_creates_new_row()
    {
        await Client.MutateRowAsync(TN, "ds-dfr2",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ds-dfr2", Mutations.DeleteFromRow());
        await Client.MutateRowAsync(TN, "ds-dfr2",
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "ds-dfr2");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    #endregion

    #region DeleteFromFamily

    [Fact]
    public async Task DeleteFromFamily_removes_only_target_family()
    {
        await Client.MutateRowAsync(TN, "ds-dff1",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "b", "2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ds-dff1", Mutations.DeleteFromFamily(CF));
        var row = await Client.ReadRowAsync(TN, "ds-dff1");
        row!.Families.Should().ContainSingle().Which.Name.Should().Be(CF2);
    }

    [Fact]
    public async Task DeleteFromFamily_all_families_removes_row()
    {
        await Client.MutateRowAsync(TN, "ds-dff2",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "b", "2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ds-dff2",
            Mutations.DeleteFromFamily(CF), Mutations.DeleteFromFamily(CF2));
        var row = await Client.ReadRowAsync(TN, "ds-dff2");
        row.Should().BeNull();
    }

    [Fact]
    public async Task DeleteFromFamily_preserves_other_columns_in_other_family()
    {
        await Client.MutateRowAsync(TN, "ds-dff3",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "3", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "d", "4", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ds-dff3", Mutations.DeleteFromFamily(CF));
        var row = await Client.ReadRowAsync(TN, "ds-dff3");
        row!.Families.Should().ContainSingle().Which.Columns.Should().HaveCount(2);
    }

    #endregion

    #region DeleteFromColumn

    [Fact]
    public async Task DeleteFromColumn_removes_all_versions()
    {
        await Client.MutateRowAsync(TN, "ds-dfc1",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        await Client.MutateRowAsync(TN, "ds-dfc1",
            Mutations.DeleteFromColumn(CF, "c"));
        var row = await Client.ReadRowAsync(TN, "ds-dfc1");
        row.Should().BeNull();
    }

    [Fact]
    public async Task DeleteFromColumn_preserves_other_columns()
    {
        await Client.MutateRowAsync(TN, "ds-dfc2",
            Mutations.SetCell(CF, "keep", "yes", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "remove", "bye", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ds-dfc2",
            Mutations.DeleteFromColumn(CF, "remove"));
        var row = await Client.ReadRowAsync(TN, "ds-dfc2");
        row!.Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("keep");
    }

    [Fact]
    public async Task DeleteFromColumn_with_version_range()
    {
        await Client.MutateRowAsync(TN, "ds-dfc3",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)),
            Mutations.SetCell(CF, "c", "v4", new BigtableVersion(4000)),
            Mutations.SetCell(CF, "c", "v5", new BigtableVersion(5000)));
        // Delete versions in [2000ms, 4000ms)
        await Client.MutateRowAsync(TN, "ds-dfc3",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(4000))));
        var row = await Client.ReadRowAsync(TN, "ds-dfc3");
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(3);
        cells.Select(c => c.Value.ToStringUtf8()).Should().BeEquivalentTo("v5", "v4", "v1");
    }

    [Fact]
    public async Task DeleteFromColumn_range_start_only()
    {
        await Client.MutateRowAsync(TN, "ds-dfc4",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        // Delete from 2000ms onwards
        await Client.MutateRowAsync(TN, "ds-dfc4",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(2000), null)));
        var row = await Client.ReadRowAsync(TN, "ds-dfc4");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("v1");
    }

    [Fact]
    public async Task DeleteFromColumn_range_end_only()
    {
        await Client.MutateRowAsync(TN, "ds-dfc5",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        // Delete up to 2000ms (exclusive)
        await Client.MutateRowAsync(TN, "ds-dfc5",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(null, new BigtableVersion(2000))));
        var row = await Client.ReadRowAsync(TN, "ds-dfc5");
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2);
        cells.Select(c => c.Value.ToStringUtf8()).Should().BeEquivalentTo("v3", "v2");
    }

    #endregion

    #region Delete + write in same mutation

    [Fact]
    public async Task Delete_and_set_in_same_request()
    {
        await Client.MutateRowAsync(TN, "ds-combo1",
            Mutations.SetCell(CF, "old", "x", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ds-combo1",
            Mutations.DeleteFromColumn(CF, "old"),
            Mutations.SetCell(CF, "new", "y", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "ds-combo1");
        var cols = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("new");
        cols.Should().NotContain("old");
    }

    [Fact]
    public async Task DeleteFromRow_and_set_in_same_request()
    {
        await Client.MutateRowAsync(TN, "ds-combo2",
            Mutations.SetCell(CF, "old", "x", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ds-combo2",
            Mutations.DeleteFromRow(),
            Mutations.SetCell(CF, "new", "y", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "ds-combo2");
        row!.Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task DeleteFromFamily_and_set_other_family()
    {
        await Client.MutateRowAsync(TN, "ds-combo3",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ds-combo3",
            Mutations.DeleteFromFamily(CF),
            Mutations.SetCell(CF2, "c", "v2", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "ds-combo3");
        row!.Families.Should().ContainSingle().Which.Name.Should().Be(CF2);
    }

    #endregion

    #region Multiple delete operations

    [Fact]
    public async Task Delete_multiple_columns_in_one_request()
    {
        await Client.MutateRowAsync(TN, "ds-multdel1",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ds-multdel1",
            Mutations.DeleteFromColumn(CF, "a"),
            Mutations.DeleteFromColumn(CF, "c"));
        var row = await Client.ReadRowAsync(TN, "ds-multdel1");
        row!.Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("b");
    }

    [Fact]
    public async Task Delete_column_that_doesnt_exist_succeeds()
    {
        await Client.MutateRowAsync(TN, "ds-dne",
            Mutations.SetCell(CF, "exists", "v", new BigtableVersion(1000)));
        // Deleting a non-existent column should not throw
        await Client.MutateRowAsync(TN, "ds-dne",
            Mutations.DeleteFromColumn(CF, "nonexistent"));
        var row = await Client.ReadRowAsync(TN, "ds-dne");
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("exists");
    }

    [Fact]
    public async Task Delete_from_nonexistent_row_succeeds()
    {
        await Client.MutateRowAsync(TN, "ds-norow",
            Mutations.DeleteFromColumn(CF, "c"));
        var row = await Client.ReadRowAsync(TN, "ds-norow");
        row.Should().BeNull();
    }

    #endregion
}

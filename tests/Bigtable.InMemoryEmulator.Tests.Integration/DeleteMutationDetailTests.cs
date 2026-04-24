using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for row-level delete semantics: DeleteFromRow, DeleteFromFamily, DeleteFromColumn.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class DeleteMutationDetailTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF1 = "cf1";
    private const string CF2 = "cf2";

    public DeleteMutationDetailTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync("del-detail", new[] { CF1, CF2 });
    }
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("del-detail");

    private async Task<Row?> ReadRow(string key)
    {
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys(key)))
            return row;
        return null;
    }

    private async Task SeedRow(string key)
    {
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF1, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF1, "b", "v2", new BigtableVersion(1000)),
            Mutations.SetCell(CF1, "c", "v3", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "x", "v4", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "y", "v5", new BigtableVersion(1000)));
    }

    #region DeleteFromRow

    [Fact]
    public async Task DeleteFromRow_removes_all_data()
    {
        await SeedRow("del-row-1");
        await Client.MutateRowAsync(TN, "del-row-1", Mutations.DeleteFromRow());
        var row = await ReadRow("del-row-1");
        row.Should().BeNull();
    }

    [Fact]
    public async Task DeleteFromRow_leaves_other_rows_intact()
    {
        await SeedRow("del-row-a");
        await SeedRow("del-row-b");
        await Client.MutateRowAsync(TN, "del-row-a", Mutations.DeleteFromRow());
        var rowA = await ReadRow("del-row-a");
        var rowB = await ReadRow("del-row-b");
        rowA.Should().BeNull();
        rowB.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteFromRow_on_nonexistent_row_succeeds()
    {
        await Client.MutateRowAsync(TN, "del-noexist", Mutations.DeleteFromRow());
        var row = await ReadRow("del-noexist");
        row.Should().BeNull();
    }

    [Fact]
    public async Task DeleteFromRow_then_rewrite()
    {
        await SeedRow("del-rewrite");
        await Client.MutateRowAsync(TN, "del-rewrite", Mutations.DeleteFromRow());
        await Client.MutateRowAsync(TN, "del-rewrite",
            Mutations.SetCell(CF1, "new", "fresh", new BigtableVersion(2000)));
        var row = await ReadRow("del-rewrite");
        row.Should().NotBeNull();
        row!.Families.Should().ContainSingle();
        row.Families[0].Columns.Should().ContainSingle();
    }

    #endregion

    #region DeleteFromFamily

    [Fact]
    public async Task DeleteFromFamily_removes_only_that_family()
    {
        await SeedRow("del-fam-1");
        await Client.MutateRowAsync(TN, "del-fam-1", Mutations.DeleteFromFamily(CF1));
        var row = await ReadRow("del-fam-1");
        row.Should().NotBeNull();
        row!.Families.Should().ContainSingle();
        row.Families[0].Name.Should().Be(CF2);
    }

    [Fact]
    public async Task DeleteFromFamily_removes_second_family()
    {
        await SeedRow("del-fam-2");
        await Client.MutateRowAsync(TN, "del-fam-2", Mutations.DeleteFromFamily(CF2));
        var row = await ReadRow("del-fam-2");
        row.Should().NotBeNull();
        row!.Families.Should().ContainSingle();
        row.Families[0].Name.Should().Be(CF1);
    }

    [Fact]
    public async Task DeleteFromFamily_both_families_removes_row()
    {
        await SeedRow("del-fam-both");
        await Client.MutateRowAsync(TN, "del-fam-both",
            Mutations.DeleteFromFamily(CF1),
            Mutations.DeleteFromFamily(CF2));
        var row = await ReadRow("del-fam-both");
        row.Should().BeNull();
    }

    [Fact]
    public async Task DeleteFromFamily_preserves_other_columns()
    {
        await SeedRow("del-fam-cols");
        await Client.MutateRowAsync(TN, "del-fam-cols", Mutations.DeleteFromFamily(CF1));
        var row = await ReadRow("del-fam-cols");
        var cf2Cols = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cf2Cols.Should().BeEquivalentTo(new[] { "x", "y" });
    }

    #endregion

    #region DeleteFromColumn

    [Fact]
    public async Task DeleteFromColumn_single_column()
    {
        await SeedRow("del-col-1");
        await Client.MutateRowAsync(TN, "del-col-1", Mutations.DeleteFromColumn(CF1, "a"));
        var row = await ReadRow("del-col-1");
        var cf1Cols = row!.Families.First(f => f.Name == CF1).Columns
            .Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cf1Cols.Should().BeEquivalentTo(new[] { "b", "c" });
    }

    [Fact]
    public async Task DeleteFromColumn_all_columns_removes_family()
    {
        await Client.MutateRowAsync(TN, "del-col-all",
            Mutations.SetCell(CF1, "only", "val", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "keep", "val", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "del-col-all", Mutations.DeleteFromColumn(CF1, "only"));
        var row = await ReadRow("del-col-all");
        row.Should().NotBeNull();
        row!.Families.Should().ContainSingle();
        row.Families[0].Name.Should().Be(CF2);
    }

    [Fact]
    public async Task DeleteFromColumn_nonexistent_column_succeeds()
    {
        await SeedRow("del-col-noexist");
        await Client.MutateRowAsync(TN, "del-col-noexist", Mutations.DeleteFromColumn(CF1, "zzz"));
        var row = await ReadRow("del-col-noexist");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteFromColumn_with_versions()
    {
        // Write 3 versions
        for (int i = 1; i <= 3; i++)
            await Client.MutateRowAsync(TN, "del-col-ver",
                Mutations.SetCell(CF1, "data", $"v{i}", new BigtableVersion(i * 1000)));
        // Delete all versions
        await Client.MutateRowAsync(TN, "del-col-ver", Mutations.DeleteFromColumn(CF1, "data"));
        var row = await ReadRow("del-col-ver");
        row.Should().BeNull();
    }

    [Fact]
    public async Task DeleteFromColumn_version_range()
    {
        // Write 5 versions
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "del-col-vr",
                Mutations.SetCell(CF1, "data", $"v{i}", new BigtableVersion(i * 1000)));
        // Delete versions in range [2000, 4000) — deletes v2 and v3
        await Client.MutateRowAsync(TN, "del-col-vr",
            Mutations.DeleteFromColumn(CF1, "data",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(4000))));
        var row = await ReadRow("del-col-vr");
        row.Should().NotBeNull();
        var cells = row!.Families[0].Columns[0].Cells;
        var vals = cells.Select(c => c.Value.ToStringUtf8()).ToList();
        vals.Should().Contain("v1");
        vals.Should().Contain("v4");
        vals.Should().Contain("v5");
        vals.Should().NotContain("v2");
        vals.Should().NotContain("v3");
    }

    #endregion

    #region Mixed delete mutations in single request

    [Fact]
    public async Task Delete_column_and_set_cell_in_same_request()
    {
        await SeedRow("del-set-combo");
        await Client.MutateRowAsync(TN, "del-set-combo",
            Mutations.DeleteFromColumn(CF1, "a"),
            Mutations.SetCell(CF1, "new_col", "new_val", new BigtableVersion(2000)));
        var row = await ReadRow("del-set-combo");
        var cf1Cols = row!.Families.First(f => f.Name == CF1).Columns
            .Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cf1Cols.Should().NotContain("a");
        cf1Cols.Should().Contain("new_col");
    }

    [Fact]
    public async Task Delete_family_and_set_cell_in_other_family()
    {
        await SeedRow("del-fam-set");
        await Client.MutateRowAsync(TN, "del-fam-set",
            Mutations.DeleteFromFamily(CF1),
            Mutations.SetCell(CF2, "new", "val", new BigtableVersion(2000)));
        var row = await ReadRow("del-fam-set");
        row.Should().NotBeNull();
        row!.Families.Should().ContainSingle();
        row.Families[0].Name.Should().Be(CF2);
    }

    [Fact]
    public async Task Batch_deletes_across_multiple_rows()
    {
        for (int i = 0; i < 5; i++)
            await Client.MutateRowAsync(TN, $"batch-del-{i:D2}",
                Mutations.SetCell(CF1, "c", "v", new BigtableVersion(1000)));

        var entries = Enumerable.Range(0, 5).Select(i =>
            Mutations.CreateEntry($"batch-del-{i:D2}", Mutations.DeleteFromRow())).ToArray();
        await Client.MutateRowsAsync(TN, entries);

        for (int i = 0; i < 5; i++)
        {
            var row = await ReadRow($"batch-del-{i:D2}");
            row.Should().BeNull();
        }
    }

    #endregion
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for DeleteFromRow, DeleteFromFamily mutations and combined deletion patterns.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
///   "DeleteFromRow: deletes all cells from the containing row."
///   "DeleteFromFamily: deletes all cells from the specified column family."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class DeleteMutationComprehensiveTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public DeleteMutationComprehensiveTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync("del-comp", new[] { CF, "cf2", "cf3" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("del-comp");

    [Fact]
    public async Task DeleteFromRow_removes_all_families()
    {
        await Client.MutateRowAsync(TN, "del-all-fam",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "v2", new BigtableVersion(1000)),
            Mutations.SetCell("cf3", "c", "v3", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "del-all-fam", Mutations.DeleteFromRow());

        var row = await Client.ReadRowAsync(TN, "del-all-fam");
        row.Should().BeNull();
    }

    [Fact]
    public async Task DeleteFromFamily_preserves_other_families()
    {
        await Client.MutateRowAsync(TN, "del-one-fam",
            Mutations.SetCell(CF, "c", "keep", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "remove", new BigtableVersion(1000)),
            Mutations.SetCell("cf3", "c", "keep", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "del-one-fam", Mutations.DeleteFromFamily("cf2"));

        var row = await Client.ReadRowAsync(TN, "del-one-fam");
        row.Should().NotBeNull();
        row!.Families.Select(f => f.Name).Should().NotContain("cf2");
        row.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task DeleteFromFamily_all_families_removes_row()
    {
        await Client.MutateRowAsync(TN, "del-all-sep",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "v", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "del-all-sep",
            Mutations.DeleteFromFamily(CF),
            Mutations.DeleteFromFamily("cf2"));

        var row = await Client.ReadRowAsync(TN, "del-all-sep");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Delete_and_rewrite_row()
    {
        await Client.MutateRowAsync(TN, "del-rw",
            Mutations.SetCell(CF, "c", "original", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "del-rw", Mutations.DeleteFromRow());

        await Client.MutateRowAsync(TN, "del-rw",
            Mutations.SetCell(CF, "c", "rewritten", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, "del-rw");
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("rewritten");
        row.Families[0].Columns[0].Cells.Should().ContainSingle();
    }

    [Fact]
    public async Task Delete_nonexistent_row_is_noop()
    {
        // Deleting a row that doesn't exist should not throw
        await Client.MutateRowAsync(TN, "del-phantom", Mutations.DeleteFromRow());
    }

    [Fact]
    public async Task Delete_nonexistent_family_from_row_is_noop()
    {
        await Client.MutateRowAsync(TN, "del-no-fam",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        // cf3 exists in schema but row has no data in it
        await Client.MutateRowAsync(TN, "del-no-fam", Mutations.DeleteFromFamily("cf3"));

        var row = await Client.ReadRowAsync(TN, "del-no-fam");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_removes_all_versions()
    {
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "del-versions",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));

        await Client.MutateRowAsync(TN, "del-versions", Mutations.DeleteFromRow());

        var row = await Client.ReadRowAsync(TN, "del-versions");
        row.Should().BeNull();
    }

    [Fact]
    public async Task DeleteFromColumn_removes_all_versions_of_column()
    {
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "del-col-ver",
                Mutations.SetCell(CF, "target", $"v{i}", new BigtableVersion(i * 1000)),
                Mutations.SetCell(CF, "keep", $"k{i}", new BigtableVersion(i * 1000)));

        await Client.MutateRowAsync(TN, "del-col-ver", Mutations.DeleteFromColumn(CF, "target"));

        var row = await Client.ReadRowAsync(TN, "del-col-ver");
        row.Should().NotBeNull();
        var cols = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().NotContain("target");
        cols.Should().Contain("keep");
    }

    [Fact]
    public async Task Batch_delete_multiple_rows()
    {
        for (int i = 0; i < 10; i++)
            await Client.MutateRowAsync(TN, $"batch-del-{i:D2}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)));

        var entries = Enumerable.Range(0, 10).Select(i =>
            Mutations.CreateEntry($"batch-del-{i:D2}", Mutations.DeleteFromRow())).ToArray();
        await Client.MutateRowsAsync(TN, entries);

        for (int i = 0; i < 10; i++)
        {
            var row = await Client.ReadRowAsync(TN, $"batch-del-{i:D2}");
            row.Should().BeNull();
        }
    }

    [Fact]
    public async Task Delete_then_set_in_same_batch_entry()
    {
        await Client.MutateRowAsync(TN, "del-set-entry",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));

        // In the same entry: delete row then set new cell
        var entries = new[]
        {
            Mutations.CreateEntry("del-set-entry",
                Mutations.DeleteFromRow(),
                Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "del-set-entry");
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Delete_preserves_unrelated_rows()
    {
        await Client.MutateRowAsync(TN, "del-iso-keep",
            Mutations.SetCell(CF, "c", "keep", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "del-iso-remove",
            Mutations.SetCell(CF, "c", "remove", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "del-iso-remove", Mutations.DeleteFromRow());

        var kept = await Client.ReadRowAsync(TN, "del-iso-keep");
        kept.Should().NotBeNull();
        kept!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("keep");
    }

    [Fact]
    public async Task DeleteFromColumn_then_add_new_version()
    {
        await Client.MutateRowAsync(TN, "del-col-add",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));

        await Client.MutateRowAsync(TN, "del-col-add",
            Mutations.DeleteFromColumn(CF, "c"),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));

        var row = await Client.ReadRowAsync(TN, "del-col-add");
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v3");
    }
}

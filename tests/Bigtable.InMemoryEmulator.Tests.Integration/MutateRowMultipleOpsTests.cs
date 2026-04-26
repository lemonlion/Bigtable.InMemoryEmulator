using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for MutateRow with multiple mutations in a single request — verifying atomicity
/// and ordering semantics of applying multiple mutations together.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutateRowMultipleOpsTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "mrmo-tests";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public MutateRowMultipleOpsTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Multiple_set_cells_same_column_different_versions()
    {
        await Client.MutateRowAsync(TN, "mrmo-sv",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));

        var row = await Client.ReadRowAsync(TN, "mrmo-sv");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task Multiple_set_cells_different_columns()
    {
        await Client.MutateRowAsync(TN, "mrmo-dc",
            Mutations.SetCell(CF, "a", "va", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "vb", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "vc", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "mrmo-dc");
        row!.Families[0].Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task Multiple_set_cells_different_families()
    {
        await Client.MutateRowAsync(TN, "mrmo-df",
            Mutations.SetCell(CF, "c", "cf-val", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "cf2-val", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "mrmo-df");
        row!.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Set_cell_then_delete_column_in_same_request()
    {
        // Pre-populate
        await Client.MutateRowAsync(TN, "mrmo-sd",
            Mutations.SetCell(CF, "keep", "keep-val", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "remove", "remove-val", new BigtableVersion(1000)));

        // Set new + delete old in one request
        await Client.MutateRowAsync(TN, "mrmo-sd",
            Mutations.SetCell(CF, "new", "new-val", new BigtableVersion(2000)),
            Mutations.DeleteFromColumn(CF, "remove"));

        var row = await Client.ReadRowAsync(TN, "mrmo-sd");
        var cols = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("keep");
        cols.Should().Contain("new");
        cols.Should().NotContain("remove");
    }

    [Fact]
    public async Task Set_cell_and_delete_family_same_request()
    {
        // Pre-populate both families
        await Client.MutateRowAsync(TN, "mrmo-sdf",
            Mutations.SetCell(CF, "c", "cf-old", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "cf2-old", new BigtableVersion(1000)));

        // Add to cf2, delete cf
        await Client.MutateRowAsync(TN, "mrmo-sdf",
            Mutations.SetCell("cf2", "d", "cf2-new", new BigtableVersion(2000)),
            Mutations.DeleteFromFamily(CF));

        var row = await Client.ReadRowAsync(TN, "mrmo-sdf");
        row!.Families.Should().HaveCount(1);
        row.Families[0].Name.Should().Be("cf2");
    }

    [Fact]
    public async Task Delete_from_row_clears_everything()
    {
        await Client.MutateRowAsync(TN, "mrmo-delr",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "v3", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "mrmo-delr", Mutations.DeleteFromRow());

        var row = await Client.ReadRowAsync(TN, "mrmo-delr");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Delete_row_then_set_cell_in_same_request()
    {
        await Client.MutateRowAsync(TN, "mrmo-drs",
            Mutations.SetCell(CF, "old", "old-val", new BigtableVersion(1000)));

        // Delete row then add new data
        await Client.MutateRowAsync(TN, "mrmo-drs",
            Mutations.DeleteFromRow(),
            Mutations.SetCell(CF, "new", "new-val", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, "mrmo-drs");
        row.Should().NotBeNull();
        var cols = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().ContainSingle("new");
    }

    [Fact]
    public async Task Multiple_deletes_in_one_request()
    {
        await Client.MutateRowAsync(TN, "mrmo-mdel",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "mrmo-mdel",
            Mutations.DeleteFromColumn(CF, "a"),
            Mutations.DeleteFromColumn(CF, "c"));

        var row = await Client.ReadRowAsync(TN, "mrmo-mdel");
        row!.Families[0].Columns.Should().HaveCount(1);
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("b");
    }

    [Fact]
    public async Task Set_same_cell_twice_last_wins()
    {
        await Client.MutateRowAsync(TN, "mrmo-last",
            Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "second", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "mrmo-last");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task Write_many_columns_single_mutation()
    {
        var mutations = Enumerable.Range(0, 50)
            .Select(i => Mutations.SetCell(CF, $"col{i:D3}", $"val{i}", new BigtableVersion(1000)))
            .ToArray();

        await Client.MutateRowAsync(TN, "mrmo-50col", mutations);

        var row = await Client.ReadRowAsync(TN, "mrmo-50col");
        row!.Families[0].Columns.Should().HaveCount(50);
    }

    [Fact]
    public async Task Delete_version_range_then_write_new_version()
    {
        await Client.MutateRowAsync(TN, "mrmo-dvw",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));

        // Delete v2, then add v4
        await Client.MutateRowAsync(TN, "mrmo-dvw",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(3000))),
            Mutations.SetCell(CF, "c", "v4", new BigtableVersion(4000)));

        var row = await Client.ReadRowAsync(TN, "mrmo-dvw");
        var vals = row!.Families[0].Columns[0].Cells
            .Select(c => c.Value.ToStringUtf8()).ToList();
        vals.Should().Contain("v1");
        vals.Should().Contain("v3");
        vals.Should().Contain("v4");
        vals.Should().NotContain("v2");
    }

    [Fact]
    public async Task Set_cells_across_both_families_and_multiple_columns()
    {
        await Client.MutateRowAsync(TN, "mrmo-cross",
            Mutations.SetCell(CF, "a", "cf-a", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "cf-b", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "x", "cf2-x", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "y", "cf2-y", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "mrmo-cross");
        row!.Families.Should().HaveCount(2);
        var cfFam = row.Families.First(f => f.Name == CF);
        var cf2Fam = row.Families.First(f => f.Name == "cf2");
        cfFam.Columns.Should().HaveCount(2);
        cf2Fam.Columns.Should().HaveCount(2);
    }

    [Fact]
    public async Task Delete_family_then_re_add_to_same_family()
    {
        await Client.MutateRowAsync(TN, "mrmo-dfra",
            Mutations.SetCell(CF, "old", "old-val", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "mrmo-dfra",
            Mutations.DeleteFromFamily(CF),
            Mutations.SetCell(CF, "new", "new-val", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, "mrmo-dfra");
        row.Should().NotBeNull();
        var cols = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().ContainSingle("new");
    }

    [Fact]
    public async Task Empty_mutation_list_is_rejected()
    {
        // SDK should reject an empty mutation list
        var act = () => Client.MutateRowAsync(TN, "mrmo-empty");
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Mixed_set_delete_set_ordering()
    {
        await Client.MutateRowAsync(TN, "mrmo-mix",
            Mutations.SetCell(CF, "a", "first-a", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "first-b", new BigtableVersion(1000)));

        // Set a=v2, delete b, then set b=v2
        await Client.MutateRowAsync(TN, "mrmo-mix",
            Mutations.SetCell(CF, "a", "second-a", new BigtableVersion(2000)),
            Mutations.DeleteFromColumn(CF, "b"),
            Mutations.SetCell(CF, "b", "second-b", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, "mrmo-mix");
        var aVals = row!.Families[0].Columns
            .First(c => c.Qualifier.ToStringUtf8() == "a")
            .Cells.Select(c => c.Value.ToStringUtf8()).ToList();
        aVals.Should().HaveCount(2);

        var bVals = row.Families[0].Columns
            .First(c => c.Qualifier.ToStringUtf8() == "b")
            .Cells.Select(c => c.Value.ToStringUtf8()).ToList();
        bVals.Should().ContainSingle("second-b");
    }

    [Fact]
    public async Task Ten_versions_single_column_in_one_mutation()
    {
        var mutations = Enumerable.Range(1, 10)
            .Select(i => Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)))
            .ToArray();

        await Client.MutateRowAsync(TN, "mrmo-10v", mutations);

        var row = await Client.ReadRowAsync(TN, "mrmo-10v");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(10);
        // Most recent first
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v10");
    }
}

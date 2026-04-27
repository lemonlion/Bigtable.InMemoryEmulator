using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Delete mutation matrix: all delete types, version ranges, family/column/row scoping.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class DeleteMutationMatrixTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "dmm-tests";
    private const string CF = "cf";

    public DeleteMutationMatrixTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task DeleteFromRow_removes_entire_row()
    {
        await Client.MutateRowAsync(TN, "dmm-row",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "v3", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "dmm-row", Mutations.DeleteFromRow());

        var row = await Client.ReadRowAsync(TN, "dmm-row");
        row.Should().BeNull();
    }

    [Fact]
    public async Task DeleteFromFamily_removes_one_family()
    {
        await Client.MutateRowAsync(TN, "dmm-fam",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "b", "v2", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "dmm-fam", Mutations.DeleteFromFamily(CF));

        var row = await Client.ReadRowAsync(TN, "dmm-fam");
        row!.Families.Should().HaveCount(1);
        row.Families[0].Name.Should().Be("cf2");
    }

    [Fact]
    public async Task DeleteFromColumn_all_versions()
    {
        await Client.MutateRowAsync(TN, "dmm-col-all",
            Mutations.SetCell(CF, "target", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "dmm-col-all",
            Mutations.SetCell(CF, "target", "v2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, "dmm-col-all",
            Mutations.SetCell(CF, "keep", "v", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "dmm-col-all", Mutations.DeleteFromColumn(CF, "target"));

        var row = await Client.ReadRowAsync(TN, "dmm-col-all");
        row!.Families[0].Columns.Should().HaveCount(1);
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("keep");
    }

    [Fact]
    public async Task DeleteFromColumn_with_version_range()
    {
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "dmm-col-vr",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));

        // Delete versions [2000, 4000) — removes v2, v3
        await Client.MutateRowAsync(TN, "dmm-col-vr",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(4000))));

        var row = await Client.ReadRowAsync(TN, "dmm-col-vr");
        var vals = row!.Families[0].Columns[0].Cells.Select(c => c.Value.ToStringUtf8()).ToList();
        vals.Should().Contain("v1");
        vals.Should().Contain("v4");
        vals.Should().Contain("v5");
        vals.Should().NotContain("v2");
        vals.Should().NotContain("v3");
    }

    [Fact]
    public async Task DeleteFromColumn_version_range_start_only()
    {
        for (int i = 1; i <= 3; i++)
            await Client.MutateRowAsync(TN, "dmm-col-vrs",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));

        // Delete [2000, +∞) — removes v2, v3
        await Client.MutateRowAsync(TN, "dmm-col-vrs",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(2000), null)));

        var row = await Client.ReadRowAsync(TN, "dmm-col-vrs");
        var vals = row!.Families[0].Columns[0].Cells.Select(c => c.Value.ToStringUtf8()).ToList();
        vals.Should().ContainSingle().Which.Should().Be("v1");
    }

    [Fact]
    public async Task DeleteFromColumn_version_range_end_only()
    {
        for (int i = 1; i <= 3; i++)
            await Client.MutateRowAsync(TN, "dmm-col-vre",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));

        // Delete [0, 2000) — removes v1
        await Client.MutateRowAsync(TN, "dmm-col-vre",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(null, new BigtableVersion(2000))));

        var row = await Client.ReadRowAsync(TN, "dmm-col-vre");
        var vals = row!.Families[0].Columns[0].Cells.Select(c => c.Value.ToStringUtf8()).ToList();
        vals.Should().Contain("v2");
        vals.Should().Contain("v3");
        vals.Should().NotContain("v1");
    }

    [Fact]
    public async Task Delete_multiple_columns_in_one_mutation()
    {
        await Client.MutateRowAsync(TN, "dmm-multi-col",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "dmm-multi-col",
            Mutations.DeleteFromColumn(CF, "a"),
            Mutations.DeleteFromColumn(CF, "b"));

        var row = await Client.ReadRowAsync(TN, "dmm-multi-col");
        row!.Families[0].Columns.Should().HaveCount(1);
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("c");
    }

    [Fact]
    public async Task Delete_then_re_create_same_column()
    {
        await Client.MutateRowAsync(TN, "dmm-del-recreate",
            Mutations.SetCell(CF, "c", "original", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "dmm-del-recreate", Mutations.DeleteFromColumn(CF, "c"));
        await Client.MutateRowAsync(TN, "dmm-del-recreate",
            Mutations.SetCell(CF, "c", "recreated", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, "dmm-del-recreate");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("recreated");
    }

    [Fact]
    public async Task Delete_family_then_re_create()
    {
        await Client.MutateRowAsync(TN, "dmm-fam-recreate",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "b", "v", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "dmm-fam-recreate", Mutations.DeleteFromFamily(CF));
        await Client.MutateRowAsync(TN, "dmm-fam-recreate",
            Mutations.SetCell(CF, "new", "v", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, "dmm-fam-recreate");
        row!.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Delete_row_then_re_create()
    {
        await Client.MutateRowAsync(TN, "dmm-row-recreate",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "dmm-row-recreate", Mutations.DeleteFromRow());
        await Client.MutateRowAsync(TN, "dmm-row-recreate",
            Mutations.SetCell(CF, "a", "new", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, "dmm-row-recreate");
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Delete_nonexistent_column_is_noop()
    {
        await Client.MutateRowAsync(TN, "dmm-noop-col",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "dmm-noop-col",
            Mutations.DeleteFromColumn(CF, "nonexistent"));

        var row = await Client.ReadRowAsync(TN, "dmm-noop-col");
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("a");
    }

    [Fact]
    public async Task Delete_row_with_set_in_same_mutation()
    {
        await Client.MutateRowAsync(TN, "dmm-del-set",
            Mutations.SetCell(CF, "old", "v", new BigtableVersion(1000)));

        // Delete the row then set a new cell in the same mutation
        await Client.MutateRowAsync(TN, "dmm-del-set",
            Mutations.DeleteFromRow(),
            Mutations.SetCell(CF, "new", "v", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, "dmm-del-set");
        row.Should().NotBeNull();
        row!.Families[0].Columns.Should().HaveCount(1);
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Delete_family_with_set_in_same_mutation()
    {
        await Client.MutateRowAsync(TN, "dmm-fam-set",
            Mutations.SetCell(CF, "old", "v", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "dmm-fam-set",
            Mutations.DeleteFromFamily(CF),
            Mutations.SetCell(CF, "new", "v", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, "dmm-fam-set");
        row.Should().NotBeNull();
        row!.Families[0].Columns.Should().HaveCount(1);
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Delete_column_preserves_other_families()
    {
        await Client.MutateRowAsync(TN, "dmm-col-xfam",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "a", "v2", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "dmm-col-xfam",
            Mutations.DeleteFromColumn(CF, "a"));

        var row = await Client.ReadRowAsync(TN, "dmm-col-xfam");
        row!.Families.Should().HaveCount(1);
        row.Families[0].Name.Should().Be("cf2");
    }

    [Fact]
    public async Task Delete_from_column_version_range_empty_range_noop()
    {
        await Client.MutateRowAsync(TN, "dmm-empty-vr",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(5000)));

        // Delete range [1000, 2000) — no versions in that range
        await Client.MutateRowAsync(TN, "dmm-empty-vr",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(1000), new BigtableVersion(2000))));

        var row = await Client.ReadRowAsync(TN, "dmm-empty-vr");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
    }

    [Fact]
    public async Task Delete_last_column_removes_family_from_output()
    {
        await Client.MutateRowAsync(TN, "dmm-last-col",
            Mutations.SetCell(CF, "only", "v", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "keep", "v", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "dmm-last-col",
            Mutations.DeleteFromColumn(CF, "only"));

        var row = await Client.ReadRowAsync(TN, "dmm-last-col");
        row!.Families.Should().HaveCount(1);
        row.Families[0].Name.Should().Be("cf2");
    }

    [Fact]
    public async Task Batch_delete_and_set_across_entries()
    {
        await Client.MutateRowAsync(TN, "dmm-batch-a",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "dmm-batch-b",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("dmm-batch-a", Mutations.DeleteFromRow()),
            Mutations.CreateEntry("dmm-batch-b",
                Mutations.SetCell(CF, "new", "v", new BigtableVersion(2000)))
        };
        await Client.MutateRowsAsync(TN, entries);

        (await Client.ReadRowAsync(TN, "dmm-batch-a")).Should().BeNull();
        var rowB = await Client.ReadRowAsync(TN, "dmm-batch-b");
        rowB!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).Should().Contain("new");
    }

    [Fact]
    public async Task Delete_all_versions_then_check_with_filter()
    {
        for (int i = 1; i <= 3; i++)
            await Client.MutateRowAsync(TN, "dmm-del-filter",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));

        await Client.MutateRowAsync(TN, "dmm-del-filter", Mutations.DeleteFromColumn(CF, "c"));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.CellsPerColumnLimit(1),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("dmm-del-filter") } }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckAndMutate_true_branch_deletes()
    {
        await Client.MutateRowAsync(TN, "dmm-cam-del",
            Mutations.SetCell(CF, "a", "keep", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "remove", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, "dmm-cam-del",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.DeleteFromColumn(CF, "b") });

        var row = await Client.ReadRowAsync(TN, "dmm-cam-del");
        row!.Families[0].Columns.Should().HaveCount(1);
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("a");
    }
}

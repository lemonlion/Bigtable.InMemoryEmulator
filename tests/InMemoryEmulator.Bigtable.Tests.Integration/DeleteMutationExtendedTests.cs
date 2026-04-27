using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for delete mutations: column-specific, family-specific, version range, and full row.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class DeleteMutationExtendedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "dme-tests";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public DeleteMutationExtendedTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task DeleteFromRow_removes_all_data()
    {
        await Client.MutateRowAsync(TN, "dme-all",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "3", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "dme-all", Mutations.DeleteFromRow());
        var row = await Client.ReadRowAsync(TN, "dme-all");
        row.Should().BeNull();
    }

    [Fact]
    public async Task DeleteFromFamily_removes_one_family()
    {
        await Client.MutateRowAsync(TN, "dme-fam",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "b", "2", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "dme-fam", Mutations.DeleteFromFamily(CF));

        var row = await Client.ReadRowAsync(TN, "dme-fam");
        row!.Families.Should().ContainSingle().Which.Name.Should().Be("cf2");
    }

    [Fact]
    public async Task DeleteFromColumn_removes_all_versions()
    {
        await Client.MutateRowAsync(TN, "dme-col",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)),
            Mutations.SetCell(CF, "keep", "x", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "dme-col", Mutations.DeleteFromColumn(CF, "c"));

        var row = await Client.ReadRowAsync(TN, "dme-col");
        row!.Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("keep");
    }

    [Fact]
    public async Task DeleteFromColumn_with_version_range()
    {
        await Client.MutateRowAsync(TN, "dme-ver",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)),
            Mutations.SetCell(CF, "c", "v4", new BigtableVersion(4000)));

        // Delete versions [2000, 4000) — removes v2 and v3
        await Client.MutateRowAsync(TN, "dme-ver",
            Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(4000))));

        var row = await Client.ReadRowAsync(TN, "dme-ver");
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2);
        var timestamps = cells.Select(c => c.TimestampMicros).OrderBy(t => t).ToList();
        timestamps.Should().Contain(1000 * 1000); // v1 kept
        timestamps.Should().Contain(4000 * 1000); // v4 kept
    }

    [Fact]
    public async Task Delete_nonexistent_row_is_no_op()
    {
        await Client.MutateRowAsync(TN, "dme-norow", Mutations.DeleteFromRow());
        var row = await Client.ReadRowAsync(TN, "dme-norow");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Delete_nonexistent_column_is_no_op()
    {
        await Client.MutateRowAsync(TN, "dme-nocol",
            Mutations.SetCell(CF, "existing", "v", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "dme-nocol",
            Mutations.DeleteFromColumn(CF, "nonexistent"));

        var row = await Client.ReadRowAsync(TN, "dme-nocol");
        row!.Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("existing");
    }

    [Fact]
    public async Task Delete_nonexistent_family_is_no_op()
    {
        await Client.MutateRowAsync(TN, "dme-nofam",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "dme-nofam", Mutations.DeleteFromFamily("cf2"));

        var row = await Client.ReadRowAsync(TN, "dme-nofam");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_then_write_same_column()
    {
        await Client.MutateRowAsync(TN, "dme-delwrite",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "dme-delwrite",
            Mutations.DeleteFromColumn(CF, "c"));

        await Client.MutateRowAsync(TN, "dme-delwrite",
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, "dme-delwrite");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task DeleteFromRow_and_write_in_same_call()
    {
        await Client.MutateRowAsync(TN, "dme-combo",
            Mutations.SetCell(CF, "old", "before", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "dme-combo",
            Mutations.DeleteFromRow(),
            Mutations.SetCell(CF, "new", "after", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, "dme-combo");
        row!.Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Multiple_delete_mutations_in_one_call()
    {
        await Client.MutateRowAsync(TN, "dme-multidel",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "dme-multidel",
            Mutations.DeleteFromColumn(CF, "a"),
            Mutations.DeleteFromColumn(CF, "c"));

        var row = await Client.ReadRowAsync(TN, "dme-multidel");
        row!.Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("b");
    }

    [Fact]
    public async Task Delete_all_columns_individually()
    {
        await Client.MutateRowAsync(TN, "dme-allcol",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "dme-allcol",
            Mutations.DeleteFromColumn(CF, "a"),
            Mutations.DeleteFromColumn(CF, "b"));

        var row = await Client.ReadRowAsync(TN, "dme-allcol");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Version_range_delete_all_up_to()
    {
        await Client.MutateRowAsync(TN, "dme-upto",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));

        // Delete all up to version 3000 (exclusive)
        await Client.MutateRowAsync(TN, "dme-upto",
            Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(new BigtableVersion(0), new BigtableVersion(3000))));

        var row = await Client.ReadRowAsync(TN, "dme-upto");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("v3");
    }

    [Fact]
    public async Task Delete_from_both_families()
    {
        await Client.MutateRowAsync(TN, "dme-bothfam",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "v2", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "dme-bothfam",
            Mutations.DeleteFromFamily(CF),
            Mutations.DeleteFromFamily("cf2"));

        var row = await Client.ReadRowAsync(TN, "dme-bothfam");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Batch_delete_multiple_rows()
    {
        for (int i = 0; i < 5; i++)
            await Client.MutateRowAsync(TN, $"dme-batch-{i}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)));

        var entries = Enumerable.Range(0, 5)
            .Select(i => Mutations.CreateEntry($"dme-batch-{i}", Mutations.DeleteFromRow()))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);

        for (int i = 0; i < 5; i++)
        {
            var row = await Client.ReadRowAsync(TN, $"dme-batch-{i}");
            row.Should().BeNull();
        }
    }

    [Fact]
    public async Task Delete_column_preserves_other_rows()
    {
        await Client.MutateRowAsync(TN, "dme-pres1",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "dme-pres2",
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "dme-pres1", Mutations.DeleteFromColumn(CF, "c"));

        (await Client.ReadRowAsync(TN, "dme-pres1")).Should().BeNull();
        (await Client.ReadRowAsync(TN, "dme-pres2")).Should().NotBeNull();
    }
}

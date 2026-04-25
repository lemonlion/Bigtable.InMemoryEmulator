using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for DeleteFromColumn with various version range boundaries.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
///   "DeleteFromColumn: delete all cells from the specified column, optionally restricted to a given timestamp range."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class DeleteFromColumnRangeTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public DeleteFromColumnRangeTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync("del-col-range", new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("del-col-range");

    private async Task Seed(string rowKey, int versions)
    {
        for (int i = 1; i <= versions; i++)
            await Client.MutateRowAsync(TN, rowKey,
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
    }

    [Fact]
    public async Task Delete_all_versions_from_column()
    {
        await Seed("del-all", 5);
        await Client.MutateRowAsync(TN, "del-all", Mutations.DeleteFromColumn(CF, "c"));
        var row = await Client.ReadRowAsync(TN, "del-all");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Delete_with_version_range_closed_open()
    {
        // Delete versions in [2000, 4000) → deletes timestamps 2000000, 3000000
        await Seed("del-range", 5);
        await Client.MutateRowAsync(TN, "del-range",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(4000))));

        var row = await Client.ReadRowAsync(TN, "del-range");
        row.Should().NotBeNull();
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(3);
        cells.Select(c => c.Value.ToStringUtf8()).Should().BeEquivalentTo("v5", "v4", "v1");
    }

    [Fact]
    public async Task Delete_with_range_covering_all()
    {
        await Seed("del-range-all", 5);
        await Client.MutateRowAsync(TN, "del-range-all",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(1000), new BigtableVersion(6000))));

        var row = await Client.ReadRowAsync(TN, "del-range-all");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Delete_with_range_covering_none()
    {
        await Seed("del-range-none", 5);
        // Range [10000, 20000) - no versions in this range
        await Client.MutateRowAsync(TN, "del-range-none",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(10000), new BigtableVersion(20000))));

        var row = await Client.ReadRowAsync(TN, "del-range-none");
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells.Should().HaveCount(5);
    }

    [Fact]
    public async Task Delete_single_version()
    {
        await Seed("del-single-ver", 5);
        // Range [3000, 4000) deletes only version 3
        await Client.MutateRowAsync(TN, "del-single-ver",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(3000), new BigtableVersion(4000))));

        var row = await Client.ReadRowAsync(TN, "del-single-ver");
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(4);
        cells.Select(c => c.Value.ToStringUtf8()).Should().BeEquivalentTo("v5", "v4", "v2", "v1");
    }

    [Fact]
    public async Task Delete_preserves_other_columns()
    {
        await Client.MutateRowAsync(TN, "del-other-col",
            Mutations.SetCell(CF, "c1", "val1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c2", "val2", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "del-other-col",
            Mutations.DeleteFromColumn(CF, "c1"));

        var row = await Client.ReadRowAsync(TN, "del-other-col");
        row.Should().NotBeNull();
        row!.Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("c2");
    }

    [Fact]
    public async Task Delete_from_column_and_add_new_version()
    {
        await Seed("del-add", 3);
        await Client.MutateRowAsync(TN, "del-add",
            Mutations.DeleteFromColumn(CF, "c"),
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(10000)));

        var row = await Client.ReadRowAsync(TN, "del-add");
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Delete_oldest_versions()
    {
        await Seed("del-oldest", 5);
        // Delete versions in [1000, 3000) → deletes v1, v2
        await Client.MutateRowAsync(TN, "del-oldest",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(1000), new BigtableVersion(3000))));

        var row = await Client.ReadRowAsync(TN, "del-oldest");
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(3);
        cells.Select(c => c.Value.ToStringUtf8()).Should().BeEquivalentTo("v5", "v4", "v3");
    }

    [Fact]
    public async Task Delete_newest_versions()
    {
        await Seed("del-newest", 5);
        // Delete versions in [4000, 6000) → deletes v4, v5
        await Client.MutateRowAsync(TN, "del-newest",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(4000), new BigtableVersion(6000))));

        var row = await Client.ReadRowAsync(TN, "del-newest");
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(3);
        cells.Select(c => c.Value.ToStringUtf8()).Should().BeEquivalentTo("v3", "v2", "v1");
    }

    [Fact]
    public async Task Delete_from_nonexistent_column_is_noop()
    {
        await Client.MutateRowAsync(TN, "del-nocol",
            Mutations.SetCell(CF, "exists", "v", new BigtableVersion(1000)));

        // Deleting a column that doesn't exist should not throw
        await Client.MutateRowAsync(TN, "del-nocol",
            Mutations.DeleteFromColumn(CF, "nonexistent"));

        var row = await Client.ReadRowAsync(TN, "del-nocol");
        row.Should().NotBeNull();
    }
}

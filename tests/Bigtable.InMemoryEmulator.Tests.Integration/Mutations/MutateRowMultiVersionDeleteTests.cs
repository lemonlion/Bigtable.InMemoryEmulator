using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutateRowMultiVersionDeleteTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "mut-mvd";
    private const string CF = "cf";

    public MutateRowMultiVersionDeleteTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        // Create row with 5 versions
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "r1",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Delete_oldest_version()
    {
        await Client.MutateRowAsync(TN, "r1",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(1000), new BigtableVersion(1001))));
        var row = await Client.ReadRowAsync(TN, "r1");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(4);
    }

    [Fact]
    public async Task Delete_newest_version()
    {
        await Client.MutateRowAsync(TN, "r1",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(5000), new BigtableVersion(5001))));
        var row = await Client.ReadRowAsync(TN, "r1");
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(4);
        cells[0].Value.ToStringUtf8().Should().Be("v4"); // v5 deleted, v4 is now newest
    }

    [Fact]
    public async Task Delete_middle_range()
    {
        await Client.MutateRowAsync(TN, "r1",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(4001))));
        var row = await Client.ReadRowAsync(TN, "r1");
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2); // v1 and v5 remain
        cells.Select(c => c.Value.ToStringUtf8()).Should().BeEquivalentTo(new[] { "v5", "v1" });
    }

    [Fact]
    public async Task Delete_all_versions()
    {
        await Client.MutateRowAsync(TN, "r1", Mutations.DeleteFromColumn(CF, "c"));
        (await Client.ReadRowAsync(TN, "r1")).Should().BeNull();
    }

    [Fact]
    public async Task Delete_range_beyond_existing()
    {
        await Client.MutateRowAsync(TN, "r1",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(6000), new BigtableVersion(10000))));
        var row = await Client.ReadRowAsync(TN, "r1");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(5); // nothing deleted
    }

    [Fact]
    public async Task Delete_range_before_existing()
    {
        await Client.MutateRowAsync(TN, "r1",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(0), new BigtableVersion(1000))));
        var row = await Client.ReadRowAsync(TN, "r1");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(5); // 0-999 micros, but versions start at 1000ms=1M micros
    }

    [Fact]
    public async Task Sequential_delete_narrow_ranges()
    {
        // Delete v2 then v4
        await Client.MutateRowAsync(TN, "r1",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(2001))));
        await Client.MutateRowAsync(TN, "r1",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(4000), new BigtableVersion(4001))));
        var row = await Client.ReadRowAsync(TN, "r1");
        var vals = row!.Families[0].Columns[0].Cells.Select(c => c.Value.ToStringUtf8()).ToList();
        vals.Should().BeEquivalentTo(new[] { "v5", "v3", "v1" });
    }

    [Fact]
    public async Task Delete_then_write_same_version()
    {
        await Client.MutateRowAsync(TN, "r1",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(3000), new BigtableVersion(3001))));
        await Client.MutateRowAsync(TN, "r1",
            Mutations.SetCell(CF, "c", "v3-new", new BigtableVersion(3000)));
        var row = await Client.ReadRowAsync(TN, "r1");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(5);
        row.Families[0].Columns[0].Cells.Any(c => c.Value.ToStringUtf8() == "v3-new").Should().BeTrue();
    }
}

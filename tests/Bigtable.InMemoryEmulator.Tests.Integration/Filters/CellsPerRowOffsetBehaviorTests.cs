using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CellsPerRowOffsetBehaviorTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "cpro-beh";
    private const string CF = "cf";

    public CellsPerRowOffsetBehaviorTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        await Client.MutateRowAsync(TN, "r1",
            Mutations.SetCell(CF, "a", "1"),
            Mutations.SetCell(CF, "b", "2"),
            Mutations.SetCell(CF, "c", "3"),
            Mutations.SetCell(CF, "d", "4"),
            Mutations.SetCell(CF, "e", "5"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Offset_0_returns_all()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.CellsPerRowOffset(0));
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(5);
    }

    [Fact]
    public async Task Offset_2_skips_first_2()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.CellsPerRowOffset(2));
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(3);
    }

    [Fact]
    public async Task Offset_equal_to_count_returns_nothing()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.CellsPerRowOffset(5));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Offset_greater_than_count_returns_nothing()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.CellsPerRowOffset(10));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Offset_with_limit()
    {
        var chain = RowFilters.Chain(
            RowFilters.CellsPerRowOffset(1),
            RowFilters.CellsPerRowLimit(2));
        var row = await Client.ReadRowAsync(TN, "r1", chain);
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(2);
    }

    [Fact]
    public async Task Offset_1_skips_first()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.CellsPerRowOffset(1));
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(4);
    }

    [Fact]
    public async Task Offset_4_returns_last_cell()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.CellsPerRowOffset(4));
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().ContainSingle();
    }

    [Fact]
    public async Task Offset_on_missing_row()
    {
        var row = await Client.ReadRowAsync(TN, "missing", RowFilters.CellsPerRowOffset(1));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Offset_across_multiple_rows()
    {
        await Client.MutateRowAsync(TN, "r2",
            Mutations.SetCell(CF, "a", "1"),
            Mutations.SetCell(CF, "b", "2"),
            Mutations.SetCell(CF, "c", "3"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.CellsPerRowOffset(2)))
            rows.Add(r);
        // r1 has 5 cells -> 3 after offset, r2 has 3 cells -> 1 after offset
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Offset_with_versions()
    {
        await Client.MutateRowAsync(TN, "r3",
            Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "col", "v3", new BigtableVersion(3000)));
        var row = await Client.ReadRowAsync(TN, "r3", RowFilters.CellsPerRowOffset(1));
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(2);
    }
}

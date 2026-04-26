using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ChainFilterBehaviorTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "chain-beh";
    private const string CF = "cf";

    public ChainFilterBehaviorTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        await Client.MutateRowAsync(TN, "r1",
            Mutations.SetCell(CF, "name", "Alice"),
            Mutations.SetCell(CF, "status", "active"),
            Mutations.SetCell(CF, "score", "95"));
        await Client.MutateRowAsync(TN, "r2",
            Mutations.SetCell(CF, "name", "Bob"),
            Mutations.SetCell(CF, "status", "inactive"),
            Mutations.SetCell(CF, "score", "80"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Chain_column_then_value()
    {
        var chain = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("status"),
            RowFilters.ValueExact("active"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: chain)) rows.Add(r);
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("r1");
    }

    [Fact]
    public async Task Chain_family_then_column()
    {
        var chain = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.ColumnQualifierExact("name"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: chain)) rows.Add(r);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Chain_with_pass_all()
    {
        var chain = RowFilters.Chain(
            RowFilters.PassAllFilter(),
            RowFilters.ColumnQualifierExact("name"));
        var row = await Client.ReadRowAsync(TN, "r1", chain);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().ContainSingle();
    }

    [Fact]
    public async Task Chain_with_block_all_returns_nothing()
    {
        var chain = RowFilters.Chain(
            RowFilters.BlockAllFilter(),
            RowFilters.ColumnQualifierExact("name"));
        var row = await Client.ReadRowAsync(TN, "r1", chain);
        row.Should().BeNull();
    }

    [Fact]
    public async Task Chain_block_all_at_end()
    {
        var chain = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.BlockAllFilter());
        var row = await Client.ReadRowAsync(TN, "r1", chain);
        row.Should().BeNull();
    }

    [Fact]
    public async Task Chain_three_filters()
    {
        var chain = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.ColumnQualifierExact("score"),
            RowFilters.ValueRegex("9.*"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: chain)) rows.Add(r);
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("r1");
    }

    [Fact]
    public async Task Chain_single_filter()
    {
        var chain = RowFilters.Chain(RowFilters.ColumnQualifierExact("name"));
        var row = await Client.ReadRowAsync(TN, "r1", chain);
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Chain_with_cells_per_row_limit()
    {
        var chain = RowFilters.Chain(
            RowFilters.PassAllFilter(),
            RowFilters.CellsPerRowLimit(1));
        var row = await Client.ReadRowAsync(TN, "r1", chain);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().ContainSingle();
    }

    [Fact]
    public async Task Chain_narrows_results()
    {
        // First filter: all columns; second: only "name"
        var chain = RowFilters.Chain(
            RowFilters.FamilyNameRegex(".*"),
            RowFilters.ColumnQualifierExact("name"));
        var row = await Client.ReadRowAsync(TN, "r1", chain);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().ContainSingle();
    }

    [Fact]
    public async Task Chain_regex_then_exact()
    {
        var chain = RowFilters.Chain(
            RowFilters.ColumnQualifierRegex("na.*|sc.*"),
            RowFilters.ValueExact("Alice"));
        var row = await Client.ReadRowAsync(TN, "r1", chain);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("name");
    }

    [Fact]
    public async Task Chain_with_row_key_regex()
    {
        var chain = RowFilters.Chain(
            RowFilters.RowKeyRegex("r1"),
            RowFilters.ColumnQualifierExact("name"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: chain)) rows.Add(r);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Chain_on_missing_row()
    {
        var chain = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.ValueExact("Alice"));
        var row = await Client.ReadRowAsync(TN, "missing", chain);
        row.Should().BeNull();
    }

    [Fact]
    public async Task Chain_preserves_order_of_cells()
    {
        var chain = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.PassAllFilter());
        var row = await Client.ReadRowAsync(TN, "r1", chain);
        row.Should().NotBeNull();
        var cols = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().BeInAscendingOrder(); // Columns are sorted
    }
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class BlockAllPassAllFilterTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "block-pass";
    private const string CF = "cf";

    public BlockAllPassAllFilterTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        await Client.MutateRowAsync(TN, "r1", Mutations.SetCell(CF, "a", "v1"));
        await Client.MutateRowAsync(TN, "r2", Mutations.SetCell(CF, "b", "v2"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task PassAll_returns_all()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.PassAllFilter()))
            rows.Add(r);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task BlockAll_returns_nothing()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.BlockAllFilter()))
            rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task BlockAll_on_single_row()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.BlockAllFilter());
        row.Should().BeNull();
    }

    [Fact]
    public async Task PassAll_preserves_values()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.PassAllFilter());
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v1");
    }

    [Fact]
    public async Task Chain_passall_passall()
    {
        var filter = RowFilters.Chain(RowFilters.PassAllFilter(), RowFilters.PassAllFilter());
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: filter)) rows.Add(r);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Chain_passall_blockall()
    {
        var filter = RowFilters.Chain(RowFilters.PassAllFilter(), RowFilters.BlockAllFilter());
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: filter)) rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Chain_blockall_passall()
    {
        var filter = RowFilters.Chain(RowFilters.BlockAllFilter(), RowFilters.PassAllFilter());
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: filter)) rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Interleave_passall_blockall()
    {
        // Interleave is union — passall wins
        var filter = RowFilters.Interleave(RowFilters.PassAllFilter(), RowFilters.BlockAllFilter());
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Interleave_blockall_blockall()
    {
        var filter = RowFilters.Interleave(RowFilters.BlockAllFilter(), RowFilters.BlockAllFilter());
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().BeNull();
    }

    [Fact]
    public async Task Condition_with_blockall_predicate()
    {
        // BlockAll predicate → false branch
        var filter = RowFilters.Condition(
            RowFilters.BlockAllFilter(),
            RowFilters.PassAllFilter(),
            RowFilters.StripValueTransformer());
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.Should().BeEmpty(); // strip applied
    }

    [Fact]
    public async Task Condition_with_passall_predicate()
    {
        // PassAll predicate → true branch
        var filter = RowFilters.Condition(
            RowFilters.PassAllFilter(),
            RowFilters.StripValueTransformer(),
            RowFilters.PassAllFilter());
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.Should().BeEmpty(); // strip applied
    }
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class FilterIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "filter-tests";
    private const string Family1 = "cf1";
    private const string Family2 = "cf2";

    public FilterIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { Family1, Family2 });
        var c = _fixture.Client;
        var tn = _fixture.GetTableName(Table);
        await c.MutateRowAsync(tn, new BigtableByteString("r1"),
            Mutations.SetCell(Family1, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(Family1, "b", "v2", new BigtableVersion(1000)),
            Mutations.SetCell(Family2, "c", "v3", new BigtableVersion(1000)));
        await c.MutateRowAsync(tn, new BigtableByteString("r2"),
            Mutations.SetCell(Family1, "a", "hello", new BigtableVersion(1000)));
        await c.MutateRowAsync(tn, new BigtableByteString("r3"),
            Mutations.SetCell(Family1, "a", "old", new BigtableVersion(1000)));
        await c.MutateRowAsync(tn, new BigtableByteString("r3"),
            Mutations.SetCell(Family1, "a", "new", new BigtableVersion(2000)));
        await c.MutateRowAsync(tn, new BigtableByteString("r3"),
            Mutations.SetCell(Family1, "a", "newest", new BigtableVersion(3000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null)
    {
        var list = new List<Row>();
        var stream = Client.ReadRows(TN, rows: rows, filter: filter);
        var e = stream.GetAsyncEnumerator(default);
        while (await e.MoveNextAsync()) list.Add(e.Current);
        return list;
    }

    [Fact]
    public async Task PassAllFilter_returns_all_rows()
    {
        var rows = await ReadAll(filter: RowFilters.PassAllFilter());
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task BlockAllFilter_returns_no_rows()
    {
        var rows = await ReadAll(filter: RowFilters.BlockAllFilter());
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task FamilyNameRegex_filters_by_family()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("r1"), RowFilters.FamilyNameRegex("cf1"));
        rows.Should().ContainSingle();
        rows[0].Families.Should().AllSatisfy(f => f.Name.Should().Be(Family1));
    }

    [Fact]
    public async Task ColumnQualifierExact_filters_by_qualifier()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("r1"), RowFilters.ColumnQualifierExact("a"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns.Should().ContainSingle();
    }

    [Fact]
    public async Task ValueRegex_filters_by_value()
    {
        var rows = await ReadAll(filter: RowFilters.ValueRegex("hello"));
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("r2");
    }

    [Fact]
    public async Task CellsPerColumnLimit_limits_versions()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("r3"), RowFilters.CellsPerColumnLimit(1));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("newest");
    }

    [Fact]
    public async Task Chain_applies_filters_sequentially()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameRegex("cf1"),
            RowFilters.ColumnQualifierExact("a"));
        var rows = await ReadAll(RowSet.FromRowKeys("r1"), filter);
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle();
        rows[0].Families[0].Columns.Should().ContainSingle();
    }

    [Fact]
    public async Task Interleave_unions_filter_results()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("a"),
            RowFilters.ColumnQualifierExact("b"));
        var rows = await ReadAll(RowSet.FromRowKeys("r1"), filter);
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns.Should().HaveCount(2);
    }

    [Fact]
    public async Task StripValueTransformer_clears_values()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("r1"), RowFilters.StripValueTransformer());
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells[0].Value.Should().BeEquivalentTo(ByteString.Empty);
    }

    [Fact]
    public async Task RowKeyRegex_filters_rows()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("r[12]"));
        rows.Should().HaveCount(2);
    }
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ColumnRangeBehaviorTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "cr-beh";
    private const string CF = "cf";

    public ColumnRangeBehaviorTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        await Client.MutateRowAsync(TN, "r1",
            Mutations.SetCell(CF, "col-a", "1"),
            Mutations.SetCell(CF, "col-b", "2"),
            Mutations.SetCell(CF, "col-c", "3"),
            Mutations.SetCell(CF, "col-d", "4"),
            Mutations.SetCell(CF, "col-e", "5"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Closed_range()
    {
        var range = ColumnRange.Closed(CF, "col-b", "col-d");
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.ColumnRange(range));
        var cols = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().BeEquivalentTo(new[] { "col-b", "col-c", "col-d" });
    }

    [Fact]
    public async Task Open_range()
    {
        var range = ColumnRange.Open(CF, "col-a", "col-e");
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.ColumnRange(range));
        var cols = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().BeEquivalentTo(new[] { "col-b", "col-c", "col-d" });
    }

    [Fact]
    public async Task ClosedOpen_range()
    {
        var range = ColumnRange.ClosedOpen(CF, "col-b", "col-d");
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.ColumnRange(range));
        var cols = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().BeEquivalentTo(new[] { "col-b", "col-c" });
    }

    [Fact]
    public async Task OpenClosed_range()
    {
        var range = ColumnRange.OpenClosed(CF, "col-a", "col-c");
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.ColumnRange(range));
        var cols = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().BeEquivalentTo(new[] { "col-b", "col-c" });
    }

    [Fact]
    public async Task Single_column_range()
    {
        var range = ColumnRange.Closed(CF, "col-c", "col-c");
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.ColumnRange(range));
        row!.Families.SelectMany(f => f.Columns).Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("col-c");
    }

    [Fact]
    public async Task No_match_range()
    {
        var range = ColumnRange.Closed(CF, "col-x", "col-z");
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.ColumnRange(range));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Full_range()
    {
        var range = ColumnRange.Closed(CF, "col-a", "col-e");
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.ColumnRange(range));
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(5);
    }

    [Fact]
    public async Task Column_range_across_rows()
    {
        await Client.MutateRowAsync(TN, "r2",
            Mutations.SetCell(CF, "col-a", "a"),
            Mutations.SetCell(CF, "col-e", "e"));
        var range = ColumnRange.Closed(CF, "col-b", "col-d");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ColumnRange(range)))
            rows.Add(r);
        // r1 has col-b,c,d in range; r2 has none in range
        rows.Should().ContainSingle().Which.Key.ToStringUtf8().Should().Be("r1");
    }

    [Fact]
    public async Task Column_range_with_cells_limit()
    {
        var chain = RowFilters.Chain(
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "col-a", "col-e")),
            RowFilters.CellsPerRowLimit(2));
        var row = await Client.ReadRowAsync(TN, "r1", chain);
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(2);
    }

    [Fact]
    public async Task Empty_open_range()
    {
        var range = ColumnRange.Open(CF, "col-c", "col-c");
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.ColumnRange(range));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Column_range_on_missing_row()
    {
        var range = ColumnRange.Closed(CF, "col-a", "col-e");
        var row = await Client.ReadRowAsync(TN, "missing", RowFilters.ColumnRange(range));
        row.Should().BeNull();
    }
}

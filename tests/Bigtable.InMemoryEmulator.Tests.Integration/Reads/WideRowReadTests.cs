using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class WideRowReadTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "wide-row";
    private const string CF = "cf";

    public WideRowReadTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        // Create a wide row with 50 columns
        var mutations = Enumerable.Range(0, 50)
            .Select(i => Mutations.SetCell(CF, $"col-{i:D3}", $"val-{i}"))
            .ToArray();
        await Client.MutateRowAsync(TN, "wide", mutations);
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Read_all_columns()
    {
        var row = await Client.ReadRowAsync(TN, "wide");
        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns).ToList();
        cells.Should().HaveCount(50);
    }

    [Fact]
    public async Task CellsPerRow_limits_total_cells()
    {
        var row = await Client.ReadRowAsync(TN, "wide", RowFilters.CellsPerRowLimit(10));
        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns.SelectMany(c => c.Cells)).ToList();
        cells.Should().HaveCount(10);
    }

    [Fact]
    public async Task CellsPerRowOffset_skips_cells()
    {
        var row = await Client.ReadRowAsync(TN, "wide", RowFilters.CellsPerRowOffset(45));
        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns.SelectMany(c => c.Cells)).ToList();
        cells.Should().HaveCount(5);
    }

    [Fact]
    public async Task Offset_then_limit()
    {
        var filter = RowFilters.Chain(
            RowFilters.CellsPerRowOffset(10),
            RowFilters.CellsPerRowLimit(5));
        var row = await Client.ReadRowAsync(TN, "wide", filter);
        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns.SelectMany(c => c.Cells)).ToList();
        cells.Should().HaveCount(5);
    }

    [Fact]
    public async Task Column_range_on_wide_row()
    {
        var range = ColumnRange.Closed(CF, "col-020", "col-029");
        var row = await Client.ReadRowAsync(TN, "wide", RowFilters.ColumnRange(range));
        row.Should().NotBeNull();
        var cols = row!.Families.SelectMany(f => f.Columns).ToList();
        cols.Should().HaveCount(10);
    }

    [Fact]
    public async Task Regex_filter_on_wide_row()
    {
        // Match col-00X (0-9)
        var row = await Client.ReadRowAsync(TN, "wide", RowFilters.ColumnQualifierRegex("col-00."));
        row.Should().NotBeNull();
        var cols = row!.Families.SelectMany(f => f.Columns).ToList();
        cols.Should().HaveCount(10); // col-000 through col-009
    }

    [Fact]
    public async Task Offset_beyond_total_returns_empty()
    {
        var row = await Client.ReadRowAsync(TN, "wide", RowFilters.CellsPerRowOffset(50));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Offset_at_exact_boundary()
    {
        var row = await Client.ReadRowAsync(TN, "wide", RowFilters.CellsPerRowOffset(49));
        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns.SelectMany(c => c.Cells)).ToList();
        cells.Should().ContainSingle();
    }

    [Fact]
    public async Task Wide_row_columns_sorted()
    {
        var row = await Client.ReadRowAsync(TN, "wide");
        var colNames = row!.Families
            .SelectMany(f => f.Columns)
            .Select(c => c.Qualifier.ToStringUtf8())
            .ToList();
        colNames.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Value_filter_on_wide_row()
    {
        var row = await Client.ReadRowAsync(TN, "wide", RowFilters.ValueRegex("val-4."));
        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns.SelectMany(c => c.Cells)).ToList();
        cells.Should().HaveCount(10); // val-40 through val-49
    }
}

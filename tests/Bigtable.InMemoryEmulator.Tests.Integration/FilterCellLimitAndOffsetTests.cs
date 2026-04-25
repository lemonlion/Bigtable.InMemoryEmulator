using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for filter combinations involving CellsPerRowLimit, CellsPerRowOffset,
/// CellsPerColumnLimit, ColumnRange, and StripValueTransformer.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class FilterCellLimitAndOffsetTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "filt-cell";

    public FilterCellLimitAndOffsetTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
        var tn = TN;
        // Row with multiple columns and multiple versions
        await _fixture.Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell(CF, "a", "a1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "a2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "a", "a3", new BigtableVersion(3000)),
            Mutations.SetCell(CF, "b", "b1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "b2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "c1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "d", "d1", new BigtableVersion(1000)));

        // Row with single cell
        await _fixture.Client.MutateRowAsync(tn, "r2",
            Mutations.SetCell(CF, "only", "val", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<(string Col, string Val)>> ReadCells(string rowKey, RowFilter? filter)
    {
        var cells = new List<(string, string)>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys(rowKey), filter: filter))
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    foreach (var cell in col.Cells)
                        cells.Add((col.Qualifier.ToStringUtf8(), cell.Value.ToStringUtf8()));
        return cells;
    }

    #region CellsPerRowLimit

    [Fact]
    public async Task CellsPerRowLimit_1_returns_first_cell()
    {
        var cells = await ReadCells("r1", RowFilters.CellsPerRowLimit(1));
        cells.Should().ContainSingle();
    }

    [Fact]
    public async Task CellsPerRowLimit_3_returns_3_cells()
    {
        var cells = await ReadCells("r1", RowFilters.CellsPerRowLimit(3));
        cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task CellsPerRowLimit_exceeding_total_returns_all()
    {
        var cells = await ReadCells("r1", RowFilters.CellsPerRowLimit(100));
        cells.Should().HaveCount(7); // 3+2+1+1
    }

    [Fact]
    public async Task CellsPerRowLimit_on_single_cell_row()
    {
        var cells = await ReadCells("r2", RowFilters.CellsPerRowLimit(5));
        cells.Should().ContainSingle();
    }

    #endregion

    #region CellsPerRowOffset

    [Fact]
    public async Task CellsPerRowOffset_0_returns_all()
    {
        var cells = await ReadCells("r1", RowFilters.CellsPerRowOffset(0));
        cells.Should().HaveCount(7);
    }

    [Fact]
    public async Task CellsPerRowOffset_3_skips_first_3()
    {
        var cells = await ReadCells("r1", RowFilters.CellsPerRowOffset(3));
        cells.Should().HaveCount(4); // 7 - 3
    }

    [Fact]
    public async Task CellsPerRowOffset_exceeding_total_returns_empty()
    {
        var cells = await ReadCells("r1", RowFilters.CellsPerRowOffset(100));
        cells.Should().BeEmpty();
    }

    [Fact]
    public async Task CellsPerRowOffset_equal_to_total()
    {
        var cells = await ReadCells("r1", RowFilters.CellsPerRowOffset(7));
        cells.Should().BeEmpty();
    }

    #endregion

    #region CellsPerColumnLimit

    [Fact]
    public async Task CellsPerColumnLimit_1_returns_latest_per_column()
    {
        var cells = await ReadCells("r1", RowFilters.CellsPerColumnLimit(1));
        // Column a has 3 versions → 1, column b has 2 → 1, c has 1, d has 1 = 4 cells
        cells.Should().HaveCount(4);
        cells.Should().Contain(("a", "a3")); // latest
        cells.Should().Contain(("b", "b2")); // latest
    }

    [Fact]
    public async Task CellsPerColumnLimit_2()
    {
        var cells = await ReadCells("r1", RowFilters.CellsPerColumnLimit(2));
        // a: 2 of 3, b: 2 of 2, c: 1, d: 1 = 6
        cells.Should().HaveCount(6);
    }

    [Fact]
    public async Task CellsPerColumnLimit_exceeds_versions()
    {
        var cells = await ReadCells("r1", RowFilters.CellsPerColumnLimit(10));
        cells.Should().HaveCount(7); // all cells
    }

    #endregion

    #region CellsPerRowLimit + CellsPerRowOffset combined

    [Fact]
    public async Task Offset_then_limit()
    {
        var cells = await ReadCells("r1", RowFilters.Chain(
            RowFilters.CellsPerRowOffset(2),
            RowFilters.CellsPerRowLimit(3)));
        cells.Should().HaveCount(3); // skip 2, then take 3
    }

    [Fact]
    public async Task Limit_then_offset()
    {
        var cells = await ReadCells("r1", RowFilters.Chain(
            RowFilters.CellsPerRowLimit(5),
            RowFilters.CellsPerRowOffset(2)));
        cells.Should().HaveCount(3); // take 5, then skip 2 = 3
    }

    #endregion

    #region StripValueTransformer

    [Fact]
    public async Task StripValue_returns_empty_values()
    {
        var cells = await ReadCells("r2", RowFilters.StripValueTransformer());
        cells.Should().ContainSingle();
        cells[0].Val.Should().BeEmpty();
    }

    [Fact]
    public async Task StripValue_preserves_column_structure()
    {
        var filter = RowFilters.Chain(
            RowFilters.CellsPerColumnLimit(1),
            RowFilters.StripValueTransformer());
        var cells = await ReadCells("r1", filter);
        cells.Should().HaveCount(4);
        cells.Should().OnlyContain(c => c.Val == "");
    }

    #endregion

    #region ColumnRange

    [Fact]
    public async Task ColumnRange_closed_closed()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.ClosedOpen(CF, "a", "c"));
        var cells = await ReadCells("r1", filter);
        // Columns a and b (closed on a, open on c)
        cells.Select(c => c.Col).Distinct().Should().BeEquivalentTo(new[] { "a", "b" });
    }

    [Fact]
    public async Task ColumnRange_closed_closed_inclusive()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "b"));
        var cells = await ReadCells("r1", filter);
        cells.Select(c => c.Col).Distinct().Should().BeEquivalentTo(new[] { "a", "b" });
    }

    [Fact]
    public async Task ColumnRange_open_closed()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.OpenClosed(CF, "a", "c"));
        var cells = await ReadCells("r1", filter);
        cells.Select(c => c.Col).Distinct().Should().BeEquivalentTo(new[] { "b", "c" });
    }

    [Fact]
    public async Task ColumnRange_open_open()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.Open(CF, "a", "c"));
        var cells = await ReadCells("r1", filter);
        cells.Select(c => c.Col).Distinct().Should().BeEquivalentTo(new[] { "b" });
    }

    [Fact]
    public async Task ColumnRange_no_match()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.Closed(CF, "x", "z"));
        var cells = await ReadCells("r1", filter);
        cells.Should().BeEmpty();
    }

    [Fact]
    public async Task ColumnRange_single_column_match()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.Closed(CF, "b", "b"));
        var cells = await ReadCells("r1", filter);
        cells.Select(c => c.Col).Distinct().Should().ContainSingle().Which.Should().Be("b");
    }

    #endregion

    #region Combined filters across families

    [Fact]
    public async Task FamilyFilter_then_CellsPerRowLimit()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameRegex(CF),
            RowFilters.CellsPerRowLimit(2));
        var cells = await ReadCells("r1", filter);
        cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task FamilyFilter_then_CellsPerColumnLimit()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameRegex(CF),
            RowFilters.CellsPerColumnLimit(1));
        var cells = await ReadCells("r1", filter);
        cells.Should().HaveCount(3); // Latest of a, b, c
    }

    [Fact]
    public async Task QualifierExact_then_CellsPerColumnLimit()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("a"),
            RowFilters.CellsPerColumnLimit(2));
        var cells = await ReadCells("r1", filter);
        cells.Should().HaveCount(2);
        cells[0].Val.Should().Be("a3");
        cells[1].Val.Should().Be("a2");
    }

    #endregion
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for cells-per-row and cells-per-column limit/offset filters.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CellLimitFilterTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "clf-test";
    private const string CF = "cf";

    public CellLimitFilterTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        // Row with many columns
        var mutations = new List<Mutation>();
        for (int col = 0; col < 20; col++)
            mutations.Add(Mutations.SetCell(CF, $"col{col:D2}", $"val{col}", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "wide", mutations.ToArray());

        // Row with many versions of one column
        for (int ver = 1; ver <= 15; ver++)
            await Client.MutateRowAsync(TN, "tall",
                Mutations.SetCell(CF, "c", $"v{ver}", new BigtableVersion(ver)));

        // Row with few columns  
        await Client.MutateRowAsync(TN, "narrow",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)));

        // Multiple rows with 3 versions each
        for (int r = 0; r < 5; r++)
            for (int ver = 1; ver <= 3; ver++)
                await Client.MutateRowAsync(TN, $"multi{r}",
                    Mutations.SetCell(CF, "c", $"v{ver}", new BigtableVersion(ver)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null)
    {
        var list = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: rows, filter: filter))
            list.Add(row);
        return list;
    }

    private int CellCount(Row row) =>
        row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();

    #region CellsPerRowLimit

    [Fact]
    public async Task CellsPerRowLimit_1_on_wide_row()
    {
        var rows = await ReadAll(rows: RowSet.FromRowKeys("wide"),
            filter: RowFilters.CellsPerRowLimit(1));
        rows.Should().ContainSingle();
        CellCount(rows[0]).Should().Be(1);
    }

    [Fact]
    public async Task CellsPerRowLimit_5_on_wide_row()
    {
        var rows = await ReadAll(rows: RowSet.FromRowKeys("wide"),
            filter: RowFilters.CellsPerRowLimit(5));
        rows.Should().ContainSingle();
        CellCount(rows[0]).Should().Be(5);
    }

    [Fact]
    public async Task CellsPerRowLimit_100_on_wide_row()
    {
        // Limit > actual cells: returns all cells
        var rows = await ReadAll(rows: RowSet.FromRowKeys("wide"),
            filter: RowFilters.CellsPerRowLimit(100));
        rows.Should().ContainSingle();
        CellCount(rows[0]).Should().Be(20);
    }

    [Fact]
    public async Task CellsPerRowLimit_1_on_narrow_row()
    {
        var rows = await ReadAll(rows: RowSet.FromRowKeys("narrow"),
            filter: RowFilters.CellsPerRowLimit(1));
        rows.Should().ContainSingle();
        CellCount(rows[0]).Should().Be(1);
    }

    [Fact]
    public async Task CellsPerRowLimit_on_tall_row()
    {
        var rows = await ReadAll(rows: RowSet.FromRowKeys("tall"),
            filter: RowFilters.CellsPerRowLimit(3));
        rows.Should().ContainSingle();
        CellCount(rows[0]).Should().Be(3);
    }

    [Fact]
    public async Task CellsPerRowLimit_applied_per_row()
    {
        var rows = await ReadAll(filter: RowFilters.Chain(
            RowFilters.RowKeyRegex("multi.*"),
            RowFilters.CellsPerRowLimit(1)));
        rows.Should().HaveCount(5);
        foreach (var row in rows)
            CellCount(row).Should().Be(1);
    }

    #endregion

    #region CellsPerRowOffset

    [Fact]
    public async Task CellsPerRowOffset_0_returns_all()
    {
        var rows = await ReadAll(rows: RowSet.FromRowKeys("wide"),
            filter: RowFilters.CellsPerRowOffset(0));
        CellCount(rows[0]).Should().Be(20);
    }

    [Fact]
    public async Task CellsPerRowOffset_5_skips_first_5()
    {
        var rows = await ReadAll(rows: RowSet.FromRowKeys("wide"),
            filter: RowFilters.CellsPerRowOffset(5));
        CellCount(rows[0]).Should().Be(15);
    }

    [Fact]
    public async Task CellsPerRowOffset_19_returns_1()
    {
        var rows = await ReadAll(rows: RowSet.FromRowKeys("wide"),
            filter: RowFilters.CellsPerRowOffset(19));
        CellCount(rows[0]).Should().Be(1);
    }

    [Fact]
    public async Task CellsPerRowOffset_20_returns_empty()
    {
        var rows = await ReadAll(rows: RowSet.FromRowKeys("wide"),
            filter: RowFilters.CellsPerRowOffset(20));
        rows.Should().BeEmpty(); // No cells left after offset
    }

    [Fact]
    public async Task CellsPerRowOffset_large_returns_empty()
    {
        var rows = await ReadAll(rows: RowSet.FromRowKeys("wide"),
            filter: RowFilters.CellsPerRowOffset(100));
        rows.Should().BeEmpty();
    }

    #endregion

    #region CellsPerRowOffset + CellsPerRowLimit combined

    [Fact]
    public async Task Offset_then_limit_pagination()
    {
        var filter = RowFilters.Chain(
            RowFilters.CellsPerRowOffset(5),
            RowFilters.CellsPerRowLimit(5));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("wide"), filter: filter);
        CellCount(rows[0]).Should().Be(5);
    }

    [Fact]
    public async Task Offset_0_limit_3()
    {
        var filter = RowFilters.Chain(
            RowFilters.CellsPerRowOffset(0),
            RowFilters.CellsPerRowLimit(3));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("wide"), filter: filter);
        CellCount(rows[0]).Should().Be(3);
    }

    [Fact]
    public async Task Offset_18_limit_10()
    {
        // Only 2 cells remain after offset 18
        var filter = RowFilters.Chain(
            RowFilters.CellsPerRowOffset(18),
            RowFilters.CellsPerRowLimit(10));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("wide"), filter: filter);
        CellCount(rows[0]).Should().Be(2);
    }

    #endregion

    #region CellsPerColumnLimit

    [Fact]
    public async Task CellsPerColumnLimit_1_latest_only()
    {
        var rows = await ReadAll(rows: RowSet.FromRowKeys("tall"),
            filter: RowFilters.CellsPerColumnLimit(1));
        rows.Should().ContainSingle();
        CellCount(rows[0]).Should().Be(1);
        // Latest version (highest timestamp) should be returned
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v15");
    }

    [Fact]
    public async Task CellsPerColumnLimit_5()
    {
        var rows = await ReadAll(rows: RowSet.FromRowKeys("tall"),
            filter: RowFilters.CellsPerColumnLimit(5));
        CellCount(rows[0]).Should().Be(5);
    }

    [Fact]
    public async Task CellsPerColumnLimit_100_returns_all()
    {
        var rows = await ReadAll(rows: RowSet.FromRowKeys("tall"),
            filter: RowFilters.CellsPerColumnLimit(100));
        CellCount(rows[0]).Should().Be(15);
    }

    [Fact]
    public async Task CellsPerColumnLimit_on_wide_row()
    {
        // Wide row has 20 columns with 1 version each
        var rows = await ReadAll(rows: RowSet.FromRowKeys("wide"),
            filter: RowFilters.CellsPerColumnLimit(1));
        CellCount(rows[0]).Should().Be(20);
    }

    [Fact]
    public async Task CellsPerColumnLimit_per_row_applied()
    {
        // multi rows each have 3 versions of "c"
        var rows = await ReadAll(filter: RowFilters.Chain(
            RowFilters.RowKeyRegex("multi.*"),
            RowFilters.CellsPerColumnLimit(1)));
        rows.Should().HaveCount(5);
        foreach (var row in rows)
            CellCount(row).Should().Be(1);
    }

    [Fact]
    public async Task CellsPerColumnLimit_2_returns_latest_2()
    {
        var rows = await ReadAll(rows: RowSet.FromRowKeys("tall"),
            filter: RowFilters.CellsPerColumnLimit(2));
        CellCount(rows[0]).Should().Be(2);
        var cells = rows[0].Families[0].Columns[0].Cells;
        cells[0].Value.ToStringUtf8().Should().Be("v15");
        cells[1].Value.ToStringUtf8().Should().Be("v14");
    }

    #endregion

    #region StripValueTransformer

    [Fact]
    public async Task StripValueTransformer_removes_values()
    {
        var rows = await ReadAll(rows: RowSet.FromRowKeys("narrow"),
            filter: RowFilters.StripValueTransformer());
        rows.Should().ContainSingle();
        CellCount(rows[0]).Should().Be(2);
        foreach (var fam in rows[0].Families)
            foreach (var col in fam.Columns)
                foreach (var cell in col.Cells)
                    cell.Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task StripValueTransformer_preserves_structure()
    {
        var rows = await ReadAll(rows: RowSet.FromRowKeys("wide"),
            filter: RowFilters.StripValueTransformer());
        CellCount(rows[0]).Should().Be(20);
    }

    [Fact]
    public async Task StripValueTransformer_chain_with_limit()
    {
        var filter = RowFilters.Chain(
            RowFilters.CellsPerColumnLimit(1),
            RowFilters.StripValueTransformer());
        var rows = await ReadAll(rows: RowSet.FromRowKeys("tall"), filter: filter);
        CellCount(rows[0]).Should().Be(1);
        rows[0].Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    #endregion

    #region PassAllFilter and BlockAllFilter

    [Fact]
    public async Task PassAllFilter_returns_all()
    {
        var noFilter = await ReadAll(rows: RowSet.FromRowKeys("wide"));
        var passAll = await ReadAll(rows: RowSet.FromRowKeys("wide"),
            filter: RowFilters.PassAllFilter());
        CellCount(noFilter[0]).Should().Be(CellCount(passAll[0]));
    }

    [Fact]
    public async Task BlockAllFilter_returns_empty()
    {
        var rows = await ReadAll(rows: RowSet.FromRowKeys("wide"),
            filter: RowFilters.BlockAllFilter());
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task PassAll_in_chain()
    {
        var filter = RowFilters.Chain(
            RowFilters.PassAllFilter(),
            RowFilters.CellsPerRowLimit(3));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("wide"), filter: filter);
        CellCount(rows[0]).Should().Be(3);
    }

    [Fact]
    public async Task BlockAll_in_chain()
    {
        var filter = RowFilters.Chain(
            RowFilters.BlockAllFilter(),
            RowFilters.CellsPerRowLimit(3));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("wide"), filter: filter);
        rows.Should().BeEmpty();
    }

    #endregion
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for CellsPerRowOffset and CellsPerRowLimit interaction.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
///   "cells_per_row_offset_filter: Skips the first N cells of each row, matching all subsequent cells."
///   "cells_per_row_limit_filter: Matches only the first N cells of each row."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CellsPerRowOffsetLimitTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string CF2 = "cf2";
    private const string Table = "cpro-limit";

    public CellsPerRowOffsetLimitTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
        var tn = TN;
        // Row with 5 cells across 2 families
        await Client.MutateRowAsync(tn, "cpr-r1",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "d", "4", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "e", "5", new BigtableVersion(1000)));
        // Row with multiple versions per column
        await Client.MutateRowAsync(tn, "cpr-r2",
            Mutations.SetCell(CF, "x", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "x", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "x", "v3", new BigtableVersion(3000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private int CountCells(Row? row)
    {
        if (row == null) return 0;
        return row.Families.SelectMany(f => f.Columns).Sum(c => c.Cells.Count);
    }

    #region CellsPerRowOffset

    [Fact]
    public async Task Offset_0_returns_all()
    {
        var row = await Client.ReadRowAsync(TN, "cpr-r1", RowFilters.CellsPerRowOffset(0));
        CountCells(row).Should().Be(5);
    }

    [Fact]
    public async Task Offset_1_skips_first()
    {
        var row = await Client.ReadRowAsync(TN, "cpr-r1", RowFilters.CellsPerRowOffset(1));
        CountCells(row).Should().Be(4);
    }

    [Fact]
    public async Task Offset_3_skips_three()
    {
        var row = await Client.ReadRowAsync(TN, "cpr-r1", RowFilters.CellsPerRowOffset(3));
        CountCells(row).Should().Be(2);
    }

    [Fact]
    public async Task Offset_exceeding_count_returns_empty()
    {
        var row = await Client.ReadRowAsync(TN, "cpr-r1", RowFilters.CellsPerRowOffset(100));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Offset_equal_to_count()
    {
        var row = await Client.ReadRowAsync(TN, "cpr-r1", RowFilters.CellsPerRowOffset(5));
        row.Should().BeNull();
    }

    #endregion

    #region CellsPerRowLimit

    [Fact]
    public async Task Limit_1_returns_first()
    {
        var row = await Client.ReadRowAsync(TN, "cpr-r1", RowFilters.CellsPerRowLimit(1));
        CountCells(row).Should().Be(1);
    }

    [Fact]
    public async Task Limit_3_returns_three()
    {
        var row = await Client.ReadRowAsync(TN, "cpr-r1", RowFilters.CellsPerRowLimit(3));
        CountCells(row).Should().Be(3);
    }

    [Fact]
    public async Task Limit_exceeding_count()
    {
        var row = await Client.ReadRowAsync(TN, "cpr-r1", RowFilters.CellsPerRowLimit(100));
        CountCells(row).Should().Be(5);
    }

    #endregion

    #region Offset + Limit combined

    [Fact]
    public async Task Offset_then_limit()
    {
        var filter = RowFilters.Chain(
            RowFilters.CellsPerRowOffset(1),
            RowFilters.CellsPerRowLimit(2));
        var row = await Client.ReadRowAsync(TN, "cpr-r1", filter);
        CountCells(row).Should().Be(2);
    }

    [Fact]
    public async Task Offset_2_limit_2()
    {
        var filter = RowFilters.Chain(
            RowFilters.CellsPerRowOffset(2),
            RowFilters.CellsPerRowLimit(2));
        var row = await Client.ReadRowAsync(TN, "cpr-r1", filter);
        CountCells(row).Should().Be(2);
    }

    #endregion

    #region With versions

    [Fact]
    public async Task Offset_counts_versions()
    {
        // Row cpr-r2 has 3 cells (3 versions of same column)
        var row = await Client.ReadRowAsync(TN, "cpr-r2", RowFilters.CellsPerRowOffset(1));
        CountCells(row).Should().Be(2);
    }

    [Fact]
    public async Task Limit_counts_versions()
    {
        var row = await Client.ReadRowAsync(TN, "cpr-r2", RowFilters.CellsPerRowLimit(2));
        CountCells(row).Should().Be(2);
    }

    #endregion

    #region Across rows

    [Fact]
    public async Task Offset_applied_per_row()
    {
        var totalCells = 0;
        await foreach (var row in Client.ReadRows(TN, filter: RowFilters.CellsPerRowOffset(1)))
        {
            totalCells += CountCells(row);
        }
        // cpr-r1: 5-1=4, cpr-r2: 3-1=2 = 6 total
        totalCells.Should().Be(6);
    }

    [Fact]
    public async Task Limit_applied_per_row()
    {
        var totalCells = 0;
        await foreach (var row in Client.ReadRows(TN, filter: RowFilters.CellsPerRowLimit(2)))
        {
            totalCells += CountCells(row);
        }
        // cpr-r1: 2, cpr-r2: 2 = 4 total
        totalCells.Should().Be(4);
    }

    #endregion
}

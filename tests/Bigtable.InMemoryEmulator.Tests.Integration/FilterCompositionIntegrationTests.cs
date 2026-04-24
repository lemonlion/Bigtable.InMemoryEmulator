using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Complex filter composition tests — deep Chain/Interleave nesting,
/// condition with chain predicates, complex filter trees.
///
/// Ref: https://cloud.google.com/bigtable/docs/using-filters
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class FilterCompositionIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "filter-comp-tests";
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public FilterCompositionIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
        await SeedData();
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task SeedData()
    {
        // Row with multiple families, columns, and versions
        for (int r = 1; r <= 5; r++)
        {
            var rk = $"fc-{r:D2}";
            for (int c = 1; c <= 3; c++)
            {
                for (int v = 1; v <= 3; v++)
                {
                    await Client.MutateRowAsync(TN, rk,
                        Mutations.SetCell(CF, $"col{c}", $"r{r}-c{c}-v{v}",
                            new BigtableVersion(v * 1000)));
                }
            }
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF2, "x", $"cf2-{r}", new BigtableVersion(1000)));
        }
    }

    #region Nested chains

    // Go emulator divergence: does not correctly handle nested Chain(Chain(...), Chain(...)) filter composition.
    // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.RowFilter.Chain
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task Chain_of_chains()
    {
        var inner1 = RowFilters.Chain(
            RowFilters.FamilyNameRegex("cf"),
            RowFilters.ColumnQualifierRegex("col1"));
        var inner2 = RowFilters.Chain(
            RowFilters.CellsPerColumnLimit(1));
        var outer = RowFilters.Chain(inner1, inner2);

        var rows = await ReadWithFilter(outer);
        rows.Should().HaveCount(5);
        foreach (var row in rows)
        {
            row.Families.Should().ContainSingle().Which.Name.Should().Be("cf");
            row.Families[0].Columns.Should().ContainSingle()
                .Which.Qualifier.ToStringUtf8().Should().Be("col1");
            row.Families[0].Columns[0].Cells.Should().ContainSingle();
        }
    }

    [Fact]
    public async Task Chain_three_levels()
    {
        var filter = RowFilters.Chain(
            RowFilters.RowKeyRegex("fc-0[1-3]"),
            RowFilters.FamilyNameRegex("cf"),
            RowFilters.ColumnQualifierRegex("col1"),
            RowFilters.CellsPerColumnLimit(2));

        var rows = await ReadWithFilter(filter);
        rows.Should().HaveCount(3);
        foreach (var row in rows)
        {
            row.Families[0].Columns[0].Cells.Should().HaveCount(2);
        }
    }

    #endregion

    #region Nested interleave

    [Fact]
    public async Task Interleave_of_chains()
    {
        var chain1 = RowFilters.Chain(
            RowFilters.FamilyNameRegex("cf"),
            RowFilters.ColumnQualifierRegex("col1"));
        var chain2 = RowFilters.Chain(
            RowFilters.FamilyNameRegex("cf2"),
            RowFilters.ColumnQualifierRegex("x"));
        var interleave = RowFilters.Interleave(chain1, chain2);

        var rows = await ReadWithFilter(interleave, RowSet.FromRowKeys("fc-01"));
        rows.Should().ContainSingle();
        rows[0].Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Interleave_three_filters()
    {
        var interleave = RowFilters.Interleave(
            RowFilters.ColumnQualifierRegex("col1"),
            RowFilters.ColumnQualifierRegex("col2"),
            RowFilters.ColumnQualifierRegex("col3"));

        var rows = await ReadWithFilter(interleave, RowSet.FromRowKeys("fc-01"));
        rows.Should().ContainSingle();
        rows[0].Families.First(f => f.Name == "cf").Columns.Should().HaveCount(3);
    }

    #endregion

    #region Condition filter compositions

    [Fact]
    public async Task Condition_with_chain_predicate()
    {
        // If the row has col1 with value starting with "r1-" → return col2; else → return col3
        var filter = RowFilters.Condition(
            RowFilters.Chain(
                RowFilters.ColumnQualifierRegex("col1"),
                RowFilters.ValueRegex("r1-.*")),
            trueFilter: RowFilters.ColumnQualifierRegex("col2"),
            falseFilter: RowFilters.ColumnQualifierRegex("col3"));

        var rows = await ReadWithFilter(filter);
        // fc-01 has col1 = "r1-c1-v3" → true → col2
        var r1 = rows.FirstOrDefault(r => r.Key.ToStringUtf8() == "fc-01");
        r1.Should().NotBeNull();
        r1!.Families.First(f => f.Name == "cf").Columns
            .Should().OnlyContain(c => c.Qualifier.ToStringUtf8() == "col2");

        // fc-02 has col1 = "r2-c1-v3" → false → col3
        var r2 = rows.FirstOrDefault(r => r.Key.ToStringUtf8() == "fc-02");
        r2.Should().NotBeNull();
        r2!.Families.First(f => f.Name == "cf").Columns
            .Should().OnlyContain(c => c.Qualifier.ToStringUtf8() == "col3");
    }

    [Fact]
    public async Task Condition_true_branch_with_family_filter()
    {
        var filter = RowFilters.Condition(
            RowFilters.PassAllFilter(),
            trueFilter: RowFilters.FamilyNameRegex("cf2"),
            falseFilter: RowFilters.BlockAllFilter());

        var rows = await ReadWithFilter(filter, RowSet.FromRowKeys("fc-01"));
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be("cf2");
    }

    [Fact]
    public async Task Condition_false_branch_block_all()
    {
        var filter = RowFilters.Condition(
            RowFilters.ValueRegex("NEVER_MATCHES"),
            trueFilter: RowFilters.PassAllFilter(),
            falseFilter: RowFilters.BlockAllFilter());

        var rows = await ReadWithFilter(filter, RowSet.FromRowKeys("fc-01"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Condition_true_branch_limits_versions()
    {
        var filter = RowFilters.Condition(
            RowFilters.ColumnQualifierRegex("col1"),
            trueFilter: RowFilters.CellsPerColumnLimit(1),
            falseFilter: RowFilters.PassAllFilter());

        var rows = await ReadWithFilter(filter, RowSet.FromRowKeys("fc-01"));
        rows.Should().ContainSingle();
        // All cells should have only 1 version (the CellsPerColumnLimit applied)
        foreach (var col in rows[0].Families.SelectMany(f => f.Columns))
        {
            col.Cells.Should().HaveCount(1);
        }
    }

    #endregion

    #region Chain with CellsPerRow

    [Fact]
    public async Task Chain_family_filter_then_cells_per_row_limit()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameRegex("cf"),
            RowFilters.CellsPerRowLimit(2));

        var rows = await ReadWithFilter(filter, RowSet.FromRowKeys("fc-01"));
        rows.Should().ContainSingle();
        var totalCells = rows[0].Families.SelectMany(f => f.Columns)
            .SelectMany(c => c.Cells).Count();
        totalCells.Should().Be(2);
    }

    [Fact]
    public async Task Chain_cells_per_column_then_cells_per_row()
    {
        var filter = RowFilters.Chain(
            RowFilters.CellsPerColumnLimit(2),
            RowFilters.CellsPerRowLimit(3));

        var rows = await ReadWithFilter(filter, RowSet.FromRowKeys("fc-01"));
        rows.Should().ContainSingle();
        var totalCells = rows[0].Families.SelectMany(f => f.Columns)
            .SelectMany(c => c.Cells).Count();
        totalCells.Should().BeLessThanOrEqualTo(3);
    }

    #endregion

    #region StripValueTransformer

    [Fact]
    public async Task StripValue_removes_cell_values()
    {
        var filter = RowFilters.StripValueTransformer();
        var rows = await ReadWithFilter(filter, RowSet.FromRowKeys("fc-01"));
        rows.Should().ContainSingle();
        foreach (var cell in rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells))
        {
            cell.Value.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task StripValue_in_chain_preserves_other_filters()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierRegex("col1"),
            RowFilters.CellsPerColumnLimit(1),
            RowFilters.StripValueTransformer());

        var rows = await ReadWithFilter(filter, RowSet.FromRowKeys("fc-01"));
        rows.Should().ContainSingle();
        var cells = rows[0].Families[0].Columns;
        cells.Should().ContainSingle().Which.Qualifier.ToStringUtf8().Should().Be("col1");
        cells[0].Cells[0].Value.Should().BeEmpty();
    }

    #endregion

    #region Multiple row ranges with filters

    [Fact]
    public async Task Filter_with_multiple_ranges()
    {
        var rowSet = RowSet.FromRowRanges(
            RowRange.ClosedOpen("fc-01", "fc-02"),
            RowRange.ClosedOpen("fc-04", "fc-05"));
        var filter = RowFilters.CellsPerColumnLimit(1);
        var rows = await ReadWithFilter(filter, rowSet);
        rows.Should().HaveCount(2);
        rows[0].Key.ToStringUtf8().Should().Be("fc-01");
        rows[1].Key.ToStringUtf8().Should().Be("fc-04");
    }

    [Fact]
    public async Task Filter_with_limit_across_ranges()
    {
        var rowSet = RowSet.FromRowRanges(
            RowRange.ClosedOpen("fc-01", "fc-06"));
        var filter = RowFilters.FamilyNameRegex("cf");
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet, filter: filter, rowsLimit: 2))
        {
            rows.Add(row);
        }
        rows.Should().HaveCount(2);
    }

    #endregion

    #region PassAll / BlockAll

    [Fact]
    public async Task PassAll_returns_all_data()
    {
        var filter = RowFilters.PassAllFilter();
        var rows = await ReadWithFilter(filter, RowSet.FromRowKeys("fc-01"));
        rows.Should().ContainSingle();
        rows[0].Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task BlockAll_returns_no_data()
    {
        var filter = RowFilters.BlockAllFilter();
        var rows = await ReadWithFilter(filter, RowSet.FromRowKeys("fc-01"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Chain_passall_with_family_filter()
    {
        var filter = RowFilters.Chain(
            RowFilters.PassAllFilter(),
            RowFilters.FamilyNameRegex("^cf$"));
        var rows = await ReadWithFilter(filter, RowSet.FromRowKeys("fc-01"));
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be("cf");
    }

    #endregion

    #region Helpers

    private async Task<List<Row>> ReadWithFilter(RowFilter filter, RowSet? rowSet = null)
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet, filter: filter))
        {
            rows.Add(row);
        }
        return rows;
    }

    #endregion
}

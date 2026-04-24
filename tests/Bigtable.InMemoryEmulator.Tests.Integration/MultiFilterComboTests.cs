using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for multi-filter combinations: deep chains + interleaves + conditions.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MultiFilterComboTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF1 = "cf1";
    private const string CF2 = "cf2";

    public MultiFilterComboTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync("mf-combo", new[] { CF1, CF2 });
        // Seed 20 rows with diverse data
        for (int i = 0; i < 20; i++)
        {
            await Client.MutateRowAsync(TN, $"mf-{i:D4}",
                Mutations.SetCell(CF1, "name", $"name-{i}", new BigtableVersion(1000)),
                Mutations.SetCell(CF1, "type", i % 2 == 0 ? "even" : "odd", new BigtableVersion(1000)),
                Mutations.SetCell(CF1, "score", $"{i * 10}", new BigtableVersion(1000)),
                Mutations.SetCell(CF2, "tag", i < 10 ? "low" : "high", new BigtableVersion(1000)),
                Mutations.SetCell(CF2, "flag", i % 3 == 0 ? "yes" : "no", new BigtableVersion(1000)));
            // Add a second version to score
            await Client.MutateRowAsync(TN, $"mf-{i:D4}",
                Mutations.SetCell(CF1, "score", $"{i * 10 + 1}", new BigtableVersion(2000)));
        }
    }
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("mf-combo");

    private async Task<int> CountRows(RowFilter filter) =>
        await CountRows(null, filter);

    private async Task<int> CountRows(RowSet? rowSet, RowFilter filter)
    {
        int count = 0;
        if (rowSet != null)
            await foreach (var _ in Client.ReadRows(TN, rowSet, filter)) count++;
        else
            await foreach (var _ in Client.ReadRows(TN, rows: null, filter)) count++;
        return count;
    }

    #region Chain with Interleave

    [Fact]
    public async Task Chain_of_FamilyFilter_and_Interleave_columns()
    {
        // Get name and type from cf1
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF1),
            RowFilters.Interleave(
                RowFilters.ColumnQualifierExact("name"),
                RowFilters.ColumnQualifierExact("type")));
        var count = await CountRows(filter);
        count.Should().Be(20); // All 20 rows should have both columns from cf1
    }

    [Fact]
    public async Task Chain_RowKeyRegex_and_Interleave_families()
    {
        // Rows 0-9 with columns from both families
        var filter = RowFilters.Chain(
            RowFilters.RowKeyRegex("mf-000[0-9]"),
            RowFilters.Interleave(
                RowFilters.FamilyNameExact(CF1),
                RowFilters.FamilyNameExact(CF2)));
        var count = await CountRows(filter);
        count.Should().Be(10);
    }

    #endregion

    #region Interleave with Chain branches

    [Fact]
    public async Task Interleave_of_two_chain_branches()
    {
        // Branch 1: cf1/name, Branch 2: cf2/tag
        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.FamilyNameExact(CF1), RowFilters.ColumnQualifierExact("name")),
            RowFilters.Chain(RowFilters.FamilyNameExact(CF2), RowFilters.ColumnQualifierExact("tag")));
        var count = await CountRows(filter);
        count.Should().Be(20); // All rows have both columns
    }

    [Fact]
    public async Task Interleave_of_chain_with_value_filter()
    {
        // Branch 1: cf1/type == "even", Branch 2: cf2/flag == "yes"
        var filter = RowFilters.Interleave(
            RowFilters.Chain(
                RowFilters.FamilyNameExact(CF1),
                RowFilters.ColumnQualifierExact("type"),
                RowFilters.ValueExact("even")),
            RowFilters.Chain(
                RowFilters.FamilyNameExact(CF2),
                RowFilters.ColumnQualifierExact("flag"),
                RowFilters.ValueExact("yes")));
        var count = await CountRows(filter);
        // Even rows: 0,2,4,6,8,10,12,14,16,18 = 10
        // Flag "yes": 0,3,6,9,12,15,18 = 7
        // Union (not intersection): rows with either match
        count.Should().BeGreaterThanOrEqualTo(10);
    }

    #endregion

    #region Condition with Chain/Interleave

    [Fact]
    public async Task Condition_with_chain_predicate()
    {
        // Predicate: cf1/type == "even"
        // true: return name column, false: return tag column
        var filter = RowFilters.Condition(
            RowFilters.Chain(
                RowFilters.FamilyNameExact(CF1),
                RowFilters.ColumnQualifierExact("type"),
                RowFilters.ValueExact("even"),
                RowFilters.CellsPerColumnLimit(1)),
            RowFilters.Chain(RowFilters.FamilyNameExact(CF1), RowFilters.ColumnQualifierExact("name")),
            RowFilters.Chain(RowFilters.FamilyNameExact(CF2), RowFilters.ColumnQualifierExact("tag")));
        var count = await CountRows(filter);
        count.Should().Be(20); // Every row gets one result
    }

    [Fact]
    public async Task Condition_true_branch_interleave()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(
                RowFilters.FamilyNameExact(CF2),
                RowFilters.ColumnQualifierExact("tag"),
                RowFilters.ValueExact("low"),
                RowFilters.CellsPerColumnLimit(1)),
            RowFilters.Interleave(
                RowFilters.Chain(RowFilters.FamilyNameExact(CF1), RowFilters.ColumnQualifierExact("name")),
                RowFilters.Chain(RowFilters.FamilyNameExact(CF1), RowFilters.ColumnQualifierExact("type"))),
            RowFilters.Chain(RowFilters.FamilyNameExact(CF2), RowFilters.ColumnQualifierExact("flag")));
        var count = await CountRows(filter);
        count.Should().Be(20); // rows 0-9 get true branch (interleave), 10-19 get false branch
    }

    #endregion

    #region Deep nesting

    [Fact]
    public async Task Three_level_chain()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF1),
            RowFilters.ColumnQualifierExact("type"),
            RowFilters.ValueExact("even"));
        var count = await CountRows(filter);
        count.Should().Be(10); // 0,2,4,...,18
    }

    [Fact]
    public async Task Four_level_chain()
    {
        var filter = RowFilters.Chain(
            RowFilters.RowKeyRegex("mf-000[0-4]"),
            RowFilters.FamilyNameExact(CF1),
            RowFilters.ColumnQualifierExact("type"),
            RowFilters.ValueExact("even"));
        var count = await CountRows(filter);
        count.Should().Be(3); // 0, 2, 4
    }

    [Fact]
    public async Task Chain_with_CellsPerColumnLimit()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF1),
            RowFilters.ColumnQualifierExact("score"),
            RowFilters.CellsPerColumnLimit(1));
        int totalCells = 0;
        await foreach (var row in Client.ReadRows(TN, rows: null, filter))
            totalCells += row.Families.Sum(f => f.Columns.Sum(c => c.Cells.Count));
        totalCells.Should().Be(20); // 20 rows × 1 cell each
    }

    #endregion

    #region Combined with RowSet

    [Fact]
    public async Task Filter_with_specific_row_keys()
    {
        var rowSet = RowSet.FromRowKeys("mf-0000", "mf-0005", "mf-0010", "mf-0015");
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF1),
            RowFilters.ColumnQualifierExact("type"));
        var count = await CountRows(rowSet, filter);
        count.Should().Be(4);
    }

    [Fact]
    public async Task Filter_with_row_range()
    {
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("mf-0005", "mf-0010"));
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF2),
            RowFilters.ColumnQualifierExact("flag"),
            RowFilters.ValueExact("yes"));
        var count = await CountRows(rowSet, filter);
        // Rows 5-9 with flag "yes": 6, 9 → 2
        count.Should().Be(2);
    }

    [Fact]
    public async Task Filter_with_limit()
    {
        var filter = RowFilters.PassAllFilter();
        int count = 0;
        await foreach (var _ in Client.ReadRows(TN, rows: null, filter, rowsLimit: 7))
            count++;
        count.Should().Be(7);
    }

    #endregion

    #region Strip value and block

    [Fact]
    public async Task StripValue_in_chain_returns_empty_values()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF1),
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.StripValueTransformer());
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter, rowsLimit: 3))
            rows.Add(row);
        rows.Should().HaveCount(3);
        foreach (var row in rows)
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    foreach (var cell in col.Cells)
                        cell.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task BlockAll_returns_no_rows()
    {
        var filter = RowFilters.BlockAllFilter();
        var count = await CountRows(filter);
        count.Should().Be(0);
    }

    [Fact]
    public async Task Interleave_with_block_and_pass()
    {
        // One branch passes, one blocks — results should contain what passes
        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.FamilyNameExact(CF1), RowFilters.ColumnQualifierExact("name")),
            RowFilters.BlockAllFilter());
        var count = await CountRows(filter);
        count.Should().Be(20); // name column passes for all rows
    }

    #endregion

    #region Cross-family filtering

    [Fact]
    public async Task Cross_family_interleave()
    {
        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.FamilyNameExact(CF1), RowFilters.ColumnQualifierExact("name")),
            RowFilters.Chain(RowFilters.FamilyNameExact(CF2), RowFilters.ColumnQualifierExact("tag")));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter, rowsLimit: 5))
            rows.Add(row);
        rows.Should().HaveCount(5);
        // Each row should have data from both families
        foreach (var row in rows)
        {
            var famNames = row.Families.Select(f => f.Name).ToList();
            famNames.Should().Contain(CF1).And.Contain(CF2);
        }
    }

    [Fact]
    public async Task FamilyName_regex_matches_both()
    {
        var filter = RowFilters.FamilyNameRegex("cf[12]");
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter, rowsLimit: 3))
            rows.Add(row);
        rows.Should().HaveCount(3);
        foreach (var row in rows)
            row.Families.Should().HaveCount(2);
    }

    #endregion
}

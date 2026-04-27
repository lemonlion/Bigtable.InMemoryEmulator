using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for condition filter (ternary: predicate ? true_filter : false_filter)
/// including nested conditions, with chains, interleaves, and edge cases.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
///   "condition: Applies one of two possible RowFilters to the data based on the output of a predicate."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ConditionFilterAdvancedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string CF2 = "cf2";
    private const string Table = "cond-adv";

    public ConditionFilterAdvancedTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
        var tn = TN;
        // Setup test rows
        await _fixture.Client.MutateRowAsync(tn, "ca-r1",
            Mutations.SetCell(CF, "status", "active", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "name", "alice", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "score", "100", new BigtableVersion(1000)));
        await _fixture.Client.MutateRowAsync(tn, "ca-r2",
            Mutations.SetCell(CF, "status", "inactive", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "name", "bob", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "score", "50", new BigtableVersion(1000)));
        await _fixture.Client.MutateRowAsync(tn, "ca-r3",
            Mutations.SetCell(CF, "flag", "true", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "data", "payload", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<(string Row, string Fam, string Col, string Val)>> ReadAllCells(RowFilter filter, string? rowKey = null)
    {
        var cells = new List<(string, string, string, string)>();
        var rowSet = rowKey != null ? RowSet.FromRowKeys(rowKey) : null;
        await foreach (var row in Client.ReadRows(TN, rowSet, filter: filter))
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    foreach (var cell in col.Cells)
                        cells.Add((row.Key.ToStringUtf8(), fam.Name, col.Qualifier.ToStringUtf8(), cell.Value.ToStringUtf8()));
        return cells;
    }

    #region Basic condition filter

    [Fact]
    public async Task Condition_true_branch_executed()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueRegex("active")),
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.BlockAllFilter());
        var cells = await ReadAllCells(filter, "ca-r1");
        cells.Should().ContainSingle();
        cells[0].Col.Should().Be("name");
        cells[0].Val.Should().Be("alice");
    }

    [Fact]
    public async Task Condition_false_branch_executed()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueRegex("active")),
            RowFilters.BlockAllFilter(),
            RowFilters.ColumnQualifierExact("name"));
        var cells = await ReadAllCells(filter, "ca-r2");
        cells.Should().ContainSingle();
        cells[0].Val.Should().Be("bob");
    }

    [Fact]
    public async Task Condition_pass_all_true_returns_all_cells()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("flag"), RowFilters.ValueRegex("true")),
            RowFilters.PassAllFilter(),
            RowFilters.BlockAllFilter());
        var cells = await ReadAllCells(filter, "ca-r3");
        cells.Should().HaveCount(2);
    }

    #endregion

    #region Condition with strip value

    [Fact]
    public async Task Condition_true_strips_values()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueRegex("active")),
            RowFilters.StripValueTransformer(),
            RowFilters.PassAllFilter());
        var cells = await ReadAllCells(filter, "ca-r1");
        cells.Should().AllSatisfy(c => c.Val.Should().BeEmpty());
    }

    #endregion

    #region Condition with family filter

    [Fact]
    public async Task Condition_true_branch_filters_family()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueRegex("active")),
            RowFilters.FamilyNameRegex(CF2),
            RowFilters.FamilyNameRegex(CF));
        var cells = await ReadAllCells(filter, "ca-r1");
        cells.Should().ContainSingle();
        cells[0].Fam.Should().Be(CF2);
    }

    [Fact]
    public async Task Condition_false_branch_filters_family()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueRegex("active")),
            RowFilters.FamilyNameRegex(CF2),
            RowFilters.FamilyNameRegex(CF));
        var cells = await ReadAllCells(filter, "ca-r2");
        cells.Should().OnlyContain(c => c.Fam == CF);
    }

    #endregion

    #region Condition with limits

    [Fact]
    public async Task Condition_true_with_cells_per_row_limit()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueRegex("active")),
            RowFilters.CellsPerRowLimit(1),
            RowFilters.PassAllFilter());
        var cells = await ReadAllCells(filter, "ca-r1");
        cells.Should().ContainSingle();
    }

    #endregion

    #region Condition across multiple rows

    [Fact]
    public async Task Condition_applied_per_row()
    {
        // Each row independently evaluates the condition
        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueRegex("active")),
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.ColumnQualifierExact("score"));
        var cells = await ReadAllCells(filter);
        // r1 (active) → name column, r2 (inactive) → score column, r3 (no status) → score column (false)
        var r1Cells = cells.Where(c => c.Row == "ca-r1").ToList();
        r1Cells.Should().ContainSingle().Which.Col.Should().Be("name");
        var r2Cells = cells.Where(c => c.Row == "ca-r2").ToList();
        r2Cells.Should().ContainSingle().Which.Col.Should().Be("score");
    }

    #endregion

    #region Nested conditions

    [Fact]
    public async Task Nested_condition_in_true_branch()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueRegex("active")),
            // True: check if score > "0" (has score column)
            RowFilters.Condition(
                RowFilters.Chain(RowFilters.FamilyNameRegex(CF2), RowFilters.ColumnQualifierExact("score")),
                RowFilters.ColumnQualifierExact("score"),
                RowFilters.ColumnQualifierExact("name")),
            RowFilters.BlockAllFilter());
        var cells = await ReadAllCells(filter, "ca-r1");
        cells.Should().ContainSingle().Which.Col.Should().Be("score");
    }

    #endregion

    #region Condition with chain in branches

    [Fact]
    public async Task Condition_true_branch_is_chain()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueRegex("active")),
            RowFilters.Chain(RowFilters.ColumnQualifierExact("name"), RowFilters.CellsPerColumnLimit(1)),
            RowFilters.PassAllFilter());
        var cells = await ReadAllCells(filter, "ca-r1");
        cells.Should().ContainSingle().Which.Col.Should().Be("name");
    }

    #endregion

    #region Condition with interleave in branches

    [Fact]
    public async Task Condition_true_branch_is_interleave()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueRegex("active")),
            RowFilters.Interleave(
                RowFilters.ColumnQualifierExact("name"),
                RowFilters.ColumnQualifierExact("status")),
            RowFilters.BlockAllFilter());
        var cells = await ReadAllCells(filter, "ca-r1");
        cells.Should().HaveCount(2);
        cells.Select(c => c.Col).Should().Contain("name").And.Contain("status");
    }

    #endregion

    #region Label transformer in condition

    [Fact]
    public async Task Condition_true_applies_label()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueRegex("active")),
            RowFilters.Chain(RowFilters.ColumnQualifierExact("name"), new RowFilter { ApplyLabelTransformer = "matched" }),
            RowFilters.PassAllFilter());
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("ca-r1"), filter: filter))
        {
            var cell = row.Families[0].Columns[0].Cells[0];
            cell.Labels.Should().Contain("matched");
        }
    }

    #endregion
}

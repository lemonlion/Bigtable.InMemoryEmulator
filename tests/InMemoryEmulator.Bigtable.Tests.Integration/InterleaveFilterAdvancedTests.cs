using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for interleave filter with complex branch combinations, ordering,
/// and interactions with other filters.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
///   "interleave: Applies several RowFilters to the data in parallel and combines the results."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class InterleaveFilterAdvancedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string CF2 = "cf2";
    private const string Table = "il-adv";

    public InterleaveFilterAdvancedTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
        var tn = TN;
        await _fixture.Client.MutateRowAsync(tn, "il-r1",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "d", "4", new BigtableVersion(1000)));
        await _fixture.Client.MutateRowAsync(tn, "il-r2",
            Mutations.SetCell(CF, "a", "10", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "20", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<(string Col, string Val)>> ReadCells(string rowKey, RowFilter filter)
    {
        var cells = new List<(string, string)>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys(rowKey), filter: filter))
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    foreach (var cell in col.Cells)
                        cells.Add((col.Qualifier.ToStringUtf8(), cell.Value.ToStringUtf8()));
        return cells;
    }

    #region Basic interleave

    [Fact]
    public async Task Interleave_two_qualifier_filters()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("a"),
            RowFilters.ColumnQualifierExact("c"));
        var cells = await ReadCells("il-r1", filter);
        cells.Should().HaveCount(2);
        cells.Select(c => c.Col).Should().BeEquivalentTo(new[] { "a", "c" });
    }

    [Fact]
    public async Task Interleave_three_qualifier_filters()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("a"),
            RowFilters.ColumnQualifierExact("b"),
            RowFilters.ColumnQualifierExact("d"));
        var cells = await ReadCells("il-r1", filter);
        cells.Should().HaveCount(3);
    }

    #endregion

    #region Interleave with family filters

    [Fact]
    public async Task Interleave_across_families()
    {
        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.FamilyNameRegex(CF), RowFilters.ColumnQualifierExact("a")),
            RowFilters.Chain(RowFilters.FamilyNameRegex(CF2), RowFilters.ColumnQualifierExact("d")));
        var cells = await ReadCells("il-r1", filter);
        cells.Should().HaveCount(2);
        cells.Select(c => c.Val).Should().BeEquivalentTo(new[] { "1", "4" });
    }

    #endregion

    #region Interleave with pass/block

    [Fact]
    public async Task Interleave_pass_all_returns_everything()
    {
        var filter = RowFilters.Interleave(
            RowFilters.PassAllFilter(),
            RowFilters.ColumnQualifierExact("a"));
        var cells = await ReadCells("il-r1", filter);
        // PassAll returns all 4 cells, qualifier "a" returns 1 → but duplicates per interleave semantics
        cells.Count.Should().BeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public async Task Interleave_block_all_branch()
    {
        var filter = RowFilters.Interleave(
            RowFilters.BlockAllFilter(),
            RowFilters.ColumnQualifierExact("b"));
        var cells = await ReadCells("il-r1", filter);
        cells.Should().ContainSingle().Which.Col.Should().Be("b");
    }

    [Fact]
    public async Task Interleave_both_block_returns_empty()
    {
        var filter = RowFilters.Interleave(
            RowFilters.BlockAllFilter(),
            RowFilters.BlockAllFilter());
        var cells = await ReadCells("il-r1", filter);
        cells.Should().BeEmpty();
    }

    #endregion

    #region Interleave with value filters

    [Fact]
    public async Task Interleave_value_regex_branches()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ValueRegex("1"),
            RowFilters.ValueRegex("3"));
        var cells = await ReadCells("il-r1", filter);
        cells.Should().HaveCount(2);
        cells.Select(c => c.Val).Should().BeEquivalentTo(new[] { "1", "3" });
    }

    #endregion

    #region Interleave with chain branches

    [Fact]
    public async Task Interleave_chain_branches()
    {
        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("a"), RowFilters.StripValueTransformer()),
            RowFilters.Chain(RowFilters.ColumnQualifierExact("b"), RowFilters.CellsPerColumnLimit(1)));
        var cells = await ReadCells("il-r1", filter);
        cells.Should().HaveCount(2);
        var cellA = cells.First(c => c.Col == "a");
        cellA.Val.Should().BeEmpty(); // stripped
        var cellB = cells.First(c => c.Col == "b");
        cellB.Val.Should().Be("2"); // original
    }

    #endregion

    #region Interleave then chain (outer chain)

    [Fact]
    public async Task Chain_interleave_then_limit()
    {
        var filter = RowFilters.Chain(
            RowFilters.Interleave(
                RowFilters.ColumnQualifierExact("a"),
                RowFilters.ColumnQualifierExact("b"),
                RowFilters.ColumnQualifierExact("c")),
            RowFilters.CellsPerRowLimit(2));
        var cells = await ReadCells("il-r1", filter);
        cells.Should().HaveCount(2);
    }

    #endregion

    #region Interleave across rows

    [Fact]
    public async Task Interleave_applied_per_row()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("a"),
            RowFilters.ColumnQualifierExact("b"));
        var allCells = new List<(string Row, string Col, string Val)>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter: filter))
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    foreach (var cell in col.Cells)
                        allCells.Add((row.Key.ToStringUtf8(), col.Qualifier.ToStringUtf8(), cell.Value.ToStringUtf8()));
        var r1 = allCells.Where(c => c.Row == "il-r1").ToList();
        var r2 = allCells.Where(c => c.Row == "il-r2").ToList();
        r1.Should().HaveCount(2);
        r2.Should().HaveCount(2);
    }

    #endregion

    #region Interleave with single branch

    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task Interleave_single_branch_acts_as_filter()
    {
        var filter = RowFilters.Interleave(RowFilters.ColumnQualifierExact("a"));
        var cells = await ReadCells("il-r1", filter);
        cells.Should().ContainSingle().Which.Col.Should().Be("a");
    }

    #endregion

    #region Interleave with labels

    [Fact]
    public async Task Interleave_branches_with_different_labels()
    {
        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("a"), new RowFilter { ApplyLabelTransformer = "branch-a" }),
            RowFilters.Chain(RowFilters.ColumnQualifierExact("b"), new RowFilter { ApplyLabelTransformer = "branch-b" }));
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("il-r1"), filter: filter))
        {
            var labels = row.Families.SelectMany(f => f.Columns)
                .SelectMany(c => c.Cells)
                .SelectMany(c => c.Labels).ToList();
            labels.Should().Contain("branch-a").And.Contain("branch-b");
        }
    }

    #endregion
}

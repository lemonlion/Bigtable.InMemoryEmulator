using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for filter chains with complex nested structures.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
///   "Chain: output of each is input to the next; Interleave: inputs each applied to row, results merged."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class FilterChainDepthTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "fcd";
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public FilterChainDepthTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
        var c = Client;
        var tn = TN;
        // Seed 10 rows with varied data
        for (int r = 0; r < 10; r++)
        {
            var mutations = new List<Mutation>();
            for (int v = 1; v <= 3; v++)
            {
                mutations.Add(Mutations.SetCell(CF, "name", $"name-{r}-v{v}", new BigtableVersion(v * 1000)));
                mutations.Add(Mutations.SetCell(CF, "type", r % 2 == 0 ? "even" : "odd", new BigtableVersion(v * 1000)));
                mutations.Add(Mutations.SetCell(CF2, "score", $"{r * 10 + v}", new BigtableVersion(v * 1000)));
            }
            await c.MutateRowAsync(tn, $"fcd-{r:D2}", mutations.ToArray());
        }
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

    #region Chain depth

    [Fact]
    public async Task Chain_2_filters()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.CellsPerColumnLimit(1));
        var rows = await ReadAll(RowSet.FromRowKeys("fcd-00"), filter);
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF);
        rows[0].Families[0].Columns.All(c => c.Cells.Count == 1).Should().BeTrue();
    }

    [Fact]
    public async Task Chain_3_filters()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.CellsPerColumnLimit(1));
        var rows = await ReadAll(RowSet.FromRowKeys("fcd-00"), filter);
        rows[0].Families.Should().ContainSingle();
        rows[0].Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("name");
    }

    [Fact]
    public async Task Chain_4_filters()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.ColumnQualifierExact("type"),
            RowFilters.CellsPerColumnLimit(1),
            RowFilters.ValueExact("even"));
        var rows = await ReadAll(RowSet.FromRowKeys("fcd-00"), filter);
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("even");
    }

    [Fact]
    public async Task Chain_4_no_match()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.ColumnQualifierExact("type"),
            RowFilters.CellsPerColumnLimit(1),
            RowFilters.ValueExact("even"));
        var rows = await ReadAll(RowSet.FromRowKeys("fcd-01"), filter); // odd
        rows.Should().BeEmpty();
    }

    #endregion

    #region Interleave depth

    [Fact]
    public async Task Interleave_2_family_filters()
    {
        var filter = RowFilters.Interleave(
            RowFilters.FamilyNameExact(CF),
            RowFilters.FamilyNameExact(CF2));
        var rows = await ReadAll(RowSet.FromRowKeys("fcd-00"), filter);
        rows[0].Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Interleave_3_column_filters()
    {
        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("name")),
            RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("type")),
            RowFilters.Chain(RowFilters.FamilyNameExact(CF2), RowFilters.ColumnQualifierExact("score")));
        var rows = await ReadAll(RowSet.FromRowKeys("fcd-00"), filter);
        var allCols = rows[0].Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        allCols.Should().BeEquivalentTo(new[] { "name", "type", "score" });
    }

    #endregion

    #region Condition filter

    [Fact]
    public async Task Condition_true_branch()
    {
        // If row has "even" in type, return name only; else return score only
        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("type"), RowFilters.CellsPerColumnLimit(1), RowFilters.ValueExact("even")),
            trueFilter: RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("name")),
            falseFilter: RowFilters.Chain(RowFilters.FamilyNameExact(CF2), RowFilters.ColumnQualifierExact("score")));
        var rows = await ReadAll(RowSet.FromRowKeys("fcd-00"), filter); // even
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF);
        rows[0].Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("name");
    }

    [Fact]
    public async Task Condition_false_branch()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("type"), RowFilters.CellsPerColumnLimit(1), RowFilters.ValueExact("even")),
            trueFilter: RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("name")),
            falseFilter: RowFilters.Chain(RowFilters.FamilyNameExact(CF2), RowFilters.ColumnQualifierExact("score")));
        var rows = await ReadAll(RowSet.FromRowKeys("fcd-01"), filter); // odd
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF2);
    }

    [Fact]
    public async Task Condition_with_passall_true()
    {
        var filter = RowFilters.Condition(
            RowFilters.PassAllFilter(),
            trueFilter: RowFilters.FamilyNameExact(CF),
            falseFilter: RowFilters.FamilyNameExact(CF2));
        var rows = await ReadAll(RowSet.FromRowKeys("fcd-00"), filter);
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF);
    }

    [Fact]
    public async Task Condition_with_blockall_false()
    {
        var filter = RowFilters.Condition(
            RowFilters.BlockAllFilter(),
            trueFilter: RowFilters.FamilyNameExact(CF),
            falseFilter: RowFilters.FamilyNameExact(CF2));
        var rows = await ReadAll(RowSet.FromRowKeys("fcd-00"), filter);
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF2);
    }

    #endregion

    #region Nested chains and interleaves

    [Fact]
    public async Task Chain_inside_interleave()
    {
        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("name"), RowFilters.CellsPerColumnLimit(1)),
            RowFilters.Chain(RowFilters.FamilyNameExact(CF2), RowFilters.CellsPerColumnLimit(1)));
        var rows = await ReadAll(RowSet.FromRowKeys("fcd-00"), filter);
        var allCols = rows[0].Families.SelectMany(f => f.Columns).ToList();
        allCols.Should().HaveCount(2); // name from CF, score from CF2
    }

    [Fact]
    public async Task Interleave_inside_chain()
    {
        // Interleave picks both families, then chain filters to latest version
        var filter = RowFilters.Chain(
            RowFilters.Interleave(
                RowFilters.FamilyNameExact(CF),
                RowFilters.FamilyNameExact(CF2)),
            RowFilters.CellsPerColumnLimit(1));
        var rows = await ReadAll(RowSet.FromRowKeys("fcd-00"), filter);
        rows[0].Families.Should().HaveCount(2);
        rows[0].Families.SelectMany(f => f.Columns).All(c => c.Cells.Count == 1).Should().BeTrue();
    }

    [Fact]
    public async Task Multi_layer_nesting()
    {
        // Chain(FamilyExact, Interleave(ColExact(name), ColExact(type)), CellsPerColumnLimit(1))
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.Interleave(
                RowFilters.ColumnQualifierExact("name"),
                RowFilters.ColumnQualifierExact("type")),
            RowFilters.CellsPerColumnLimit(1));
        var rows = await ReadAll(RowSet.FromRowKeys("fcd-00"), filter);
        var cols = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().BeEquivalentTo(new[] { "name", "type" });
        rows[0].Families[0].Columns.All(c => c.Cells.Count == 1).Should().BeTrue();
    }

    #endregion

    #region PassAll and BlockAll

    [Fact]
    public async Task PassAll_returns_everything()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("fcd-00"), RowFilters.PassAllFilter());
        rows.Should().ContainSingle();
        rows[0].Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task BlockAll_returns_nothing()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("fcd-00"), RowFilters.BlockAllFilter());
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Chain_passall_then_family()
    {
        var filter = RowFilters.Chain(RowFilters.PassAllFilter(), RowFilters.FamilyNameExact(CF));
        var rows = await ReadAll(RowSet.FromRowKeys("fcd-00"), filter);
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF);
    }

    [Fact]
    public async Task Chain_family_then_blockall()
    {
        var filter = RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.BlockAllFilter());
        var rows = await ReadAll(RowSet.FromRowKeys("fcd-00"), filter);
        rows.Should().BeEmpty();
    }

    #endregion

    #region Filters across many rows

    [Fact]
    public async Task Chain_filter_on_all_rows()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.ColumnQualifierExact("type"),
            RowFilters.CellsPerColumnLimit(1),
            RowFilters.ValueExact("even"));
        var rows = await ReadAll(filter: filter);
        rows.Should().HaveCount(5); // rows 0,2,4,6,8
    }

    [Fact]
    public async Task Interleave_on_all_rows()
    {
        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("name"), RowFilters.CellsPerColumnLimit(1)),
            RowFilters.Chain(RowFilters.FamilyNameExact(CF2), RowFilters.ColumnQualifierExact("score"), RowFilters.CellsPerColumnLimit(1)));
        var rows = await ReadAll(filter: filter);
        rows.Should().HaveCount(10);
        foreach (var row in rows)
        {
            var allCols = row.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
            allCols.Should().BeEquivalentTo(new[] { "name", "score" });
        }
    }

    [Fact]
    public async Task Condition_filter_on_all_rows_splits_correctly()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("type"), RowFilters.CellsPerColumnLimit(1), RowFilters.ValueExact("even")),
            trueFilter: RowFilters.StripValueTransformer(),
            falseFilter: RowFilters.CellsPerColumnLimit(1));
        var rows = await ReadAll(filter: filter);
        rows.Should().HaveCount(10);
        // Even rows should have stripped values
        var evenRows = rows.Where(r => int.Parse(r.Key.ToStringUtf8().Split('-')[1]) % 2 == 0).ToList();
        evenRows.All(r => r.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).All(c => c.Value.Length == 0)).Should().BeTrue();
        // Odd rows should have non-stripped values, limited to 1 per column
        var oddRows = rows.Where(r => int.Parse(r.Key.ToStringUtf8().Split('-')[1]) % 2 != 0).ToList();
        oddRows.All(r => r.Families.SelectMany(f => f.Columns).All(c => c.Cells.Count == 1)).Should().BeTrue();
    }

    #endregion

    #region Value range filter

    [Fact]
    public async Task ValueRange_closed()
    {
        // Filter cells with values starting with "e" (even/odd)
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("type"),
            RowFilters.CellsPerColumnLimit(1),
            RowFilters.ValueRange(ValueRange.Closed("even", "even")));
        var rows = await ReadAll(filter: filter);
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task ValueRange_open()
    {
        // Values between "n" and "p" (name-* values are in this range for some rows)
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.CellsPerColumnLimit(1),
            RowFilters.ValueRange(ValueRange.Open("name-0", "name-5")));
        var rows = await ReadAll(filter: filter);
        // name-0-v3 through name-4-v3 are all in range ("name-0", "name-5") = 5 rows
        rows.Should().HaveCount(5);
    }

    #endregion

    #region Column range filter

    [Fact]
    public async Task ColumnRange_returns_subset()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.Closed(CF, "name", "name"));
        var rows = await ReadAll(RowSet.FromRowKeys("fcd-00"), filter);
        rows[0].Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("name");
    }

    [Fact]
    public async Task ColumnRange_open_excludes_boundaries()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.Open(CF, "name", "type"));
        var rows = await ReadAll(RowSet.FromRowKeys("fcd-00"), filter);
        // Between "name" and "type" exclusive — nothing matches
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ColumnRange_all_columns()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "z"));
        var rows = await ReadAll(RowSet.FromRowKeys("fcd-00"), filter);
        rows[0].Families[0].Columns.Should().HaveCount(2); // name, type
    }

    #endregion
}

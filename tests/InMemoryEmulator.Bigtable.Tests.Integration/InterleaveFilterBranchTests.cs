using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for interleave filter with many branches.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class InterleaveFilterBranchTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "il-branch";
    private const string CF = "cf";

    public InterleaveFilterBranchTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, "cf2", "cf3" });
        // Rows with many columns
        for (int r = 0; r < 5; r++)
        {
            var mutations = new List<Mutation>();
            for (int col = 0; col < 10; col++)
                mutations.Add(Mutations.SetCell(CF, $"col{col:D2}", $"r{r}-c{col}", new BigtableVersion(1000)));
            mutations.Add(Mutations.SetCell("cf2", "x", $"cf2-r{r}", new BigtableVersion(1000)));
            mutations.Add(Mutations.SetCell("cf3", "y", $"cf3-r{r}", new BigtableVersion(1000)));
            await Client.MutateRowAsync(TN, $"il-{r}", mutations.ToArray());
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

    private List<string> GetQualifiers(List<Row> rows)
    {
        return rows.SelectMany(r => r.Families)
            .SelectMany(f => f.Columns)
            .Select(c => c.Qualifier.ToStringUtf8())
            .ToList();
    }

    #region Basic interleave

    [Fact]
    public async Task Interleave_two_columns()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("col00"),
            RowFilters.ColumnQualifierExact("col01"));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("il-0"), filter: filter);
        var quals = GetQualifiers(rows);
        quals.Should().HaveCount(2);
        quals.Should().Contain("col00");
        quals.Should().Contain("col01");
    }

    [Fact]
    public async Task Interleave_three_columns()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("col00"),
            RowFilters.ColumnQualifierExact("col05"),
            RowFilters.ColumnQualifierExact("col09"));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("il-0"), filter: filter);
        GetQualifiers(rows).Should().HaveCount(3);
    }

    [Fact]
    public async Task Interleave_five_columns()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("col00"),
            RowFilters.ColumnQualifierExact("col02"),
            RowFilters.ColumnQualifierExact("col04"),
            RowFilters.ColumnQualifierExact("col06"),
            RowFilters.ColumnQualifierExact("col08"));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("il-0"), filter: filter);
        GetQualifiers(rows).Should().HaveCount(5);
    }

    #endregion

    #region Cross-family interleave

    [Fact]
    public async Task Interleave_families()
    {
        var filter = RowFilters.Interleave(
            RowFilters.FamilyNameRegex("cf2"),
            RowFilters.FamilyNameRegex("cf3"));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("il-0"), filter: filter);
        var families = rows[0].Families.Select(f => f.Name).ToList();
        families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Interleave_family_and_column()
    {
        var filter = RowFilters.Interleave(
            RowFilters.FamilyNameRegex("cf2"),
            RowFilters.Chain(
                RowFilters.FamilyNameRegex(CF),
                RowFilters.ColumnQualifierExact("col00")));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("il-0"), filter: filter);
        var allCells = rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();
        allCells.Should().Be(2);
    }

    #endregion

    #region Interleave across multiple rows

    [Fact]
    public async Task Interleave_applied_per_row()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("col00"),
            RowFilters.ColumnQualifierExact("col09"));
        var rows = await ReadAll(filter: filter);
        rows.Should().HaveCount(5);
        foreach (var row in rows)
        {
            var quals = row.Families.SelectMany(f => f.Columns)
                .Select(c => c.Qualifier.ToStringUtf8()).ToList();
            quals.Should().HaveCount(2);
        }
    }

    #endregion

    #region Interleave with chains

    [Fact]
    public async Task Interleave_chain_branches()
    {
        var filter = RowFilters.Interleave(
            RowFilters.Chain(
                RowFilters.ColumnQualifierRegex("col0[0-2]"),
                RowFilters.CellsPerRowLimit(2)),
            RowFilters.Chain(
                RowFilters.ColumnQualifierRegex("col0[7-9]"),
                RowFilters.CellsPerRowLimit(2)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("il-0"), filter: filter);
        // Each chain returns max 2 cells, interleave unions them
        var totalCells = rows[0].Families.SelectMany(f => f.Columns)
            .SelectMany(c => c.Cells).Count();
        totalCells.Should().BeLessThanOrEqualTo(4);
    }

    [Fact]
    public async Task Interleave_with_pass_all()
    {
        var filter = RowFilters.Interleave(
            RowFilters.PassAllFilter(),
            RowFilters.ColumnQualifierExact("col00"));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("il-0"), filter: filter);
        // PassAll returns everything, so interleave = everything
        rows[0].Families.SelectMany(f => f.Columns).Count().Should().BeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public async Task Interleave_with_block_all()
    {
        var filter = RowFilters.Interleave(
            RowFilters.BlockAllFilter(),
            RowFilters.ColumnQualifierExact("col00"));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("il-0"), filter: filter);
        // BlockAll returns nothing, col00 returns col00 => union = col00
        var quals = GetQualifiers(rows);
        quals.Should().ContainSingle().Which.Should().Be("col00");
    }

    #endregion

    #region Interleave deduplication

    [Fact]
    public async Task Interleave_same_filter_twice()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("col00"),
            RowFilters.ColumnQualifierExact("col00"));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("il-0"), filter: filter);
        // Interleave may return duplicates
        var quals = GetQualifiers(rows);
        quals.Should().AllSatisfy(q => q.Should().Be("col00"));
    }

    [Fact]
    public async Task Interleave_overlapping_regex()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierRegex("col0[0-4]"),
            RowFilters.ColumnQualifierRegex("col0[3-6]"));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("il-0"), filter: filter);
        // Overlapping: col03, col04 may appear in both branches
        var uniqueQuals = GetQualifiers(rows).Distinct().ToList();
        uniqueQuals.Should().HaveCountGreaterThanOrEqualTo(7); // col00-col06
    }

    #endregion

    #region Nested interleave

    [Fact]
    public async Task Nested_interleave()
    {
        var filter = RowFilters.Interleave(
            RowFilters.Interleave(
                RowFilters.ColumnQualifierExact("col00"),
                RowFilters.ColumnQualifierExact("col01")),
            RowFilters.ColumnQualifierExact("col09"));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("il-0"), filter: filter);
        var quals = GetQualifiers(rows).Distinct().ToList();
        quals.Should().HaveCount(3);
    }

    #endregion

    #region Interleave with strip value

    [Fact]
    public async Task Interleave_one_branch_stripped()
    {
        var filter = RowFilters.Interleave(
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("col00"),
                RowFilters.StripValueTransformer()),
            RowFilters.ColumnQualifierExact("col01"));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("il-0"), filter: filter);
        var cols = rows[0].Families.SelectMany(f => f.Columns).ToList();
        var col00 = cols.First(c => c.Qualifier.ToStringUtf8() == "col00");
        col00.Cells[0].Value.Length.Should().Be(0);
        var col01 = cols.First(c => c.Qualifier.ToStringUtf8() == "col01");
        col01.Cells[0].Value.Length.Should().BeGreaterThan(0);
    }

    #endregion
}

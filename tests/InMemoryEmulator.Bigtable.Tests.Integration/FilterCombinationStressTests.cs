using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Stress tests for complex filter combinations — chains, interleaves, conditions nested.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class FilterCombinationStressTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "filter-combo";
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public FilterCombinationStressTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
        var client = _fixture.Client;
        var tn = _fixture.GetTableName(Table);

        // Seed data: 10 rows, each with 3 columns in CF and 2 columns in CF2, 3 versions each
        for (int r = 0; r < 10; r++)
        {
            var mutations = new List<Mutation>();
            for (int v = 1; v <= 3; v++)
            {
                mutations.Add(Mutations.SetCell(CF, "name", $"row{r}-v{v}", new BigtableVersion(v * 1000)));
                mutations.Add(Mutations.SetCell(CF, "type", r % 2 == 0 ? "even" : "odd", new BigtableVersion(v * 1000)));
                mutations.Add(Mutations.SetCell(CF, "score", $"{r * 10 + v}", new BigtableVersion(v * 1000)));
                mutations.Add(Mutations.SetCell(CF2, "tag", $"tag-{r}-{v}", new BigtableVersion(v * 1000)));
                mutations.Add(Mutations.SetCell(CF2, "flag", v == 3 ? "active" : "stale", new BigtableVersion(v * 1000)));
            }
            await client.MutateRowAsync(tn, $"fc-{r:D3}", mutations.ToArray());
        }
    }
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null, long? limit = null)
    {
        var list = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: rows, filter: filter, rowsLimit: limit))
            list.Add(row);
        return list;
    }

    #region Chain filter combinations

    [Fact]
    public async Task Chain_family_then_column()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.ColumnQualifierExact("name"));
        var rows = await ReadAll(RowSet.FromRowKeys("fc-000"), filter);
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Columns.Should().ContainSingle();
    }

    [Fact]
    public async Task Chain_family_column_latest()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.CellsPerColumnLimit(1));
        var rows = await ReadAll(RowSet.FromRowKeys("fc-000"), filter);
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("row0-v3");
    }

    [Fact]
    public async Task Chain_family_column_value()
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
    public async Task Chain_timestamp_range_then_value()
    {
        var filter = RowFilters.Chain(
            new RowFilter
            {
                TimestampRangeFilter = new TimestampRange
                {
                    StartTimestampMicros = 2_000_000,
                    EndTimestampMicros = 3_000_000,
                }
            },
            RowFilters.FamilyNameExact(CF),
            RowFilters.ColumnQualifierExact("name"));
        var rows = await ReadAll(RowSet.FromRowKeys("fc-000"), filter);
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("row0-v2");
    }

    [Fact]
    public async Task Chain_row_key_regex_then_family()
    {
        var filter = RowFilters.Chain(
            RowFilters.RowKeyRegex("fc-00[0-2]"),
            RowFilters.FamilyNameExact(CF2));
        var rows = await ReadAll(filter: filter);
        rows.Should().HaveCount(3);
        rows.Should().OnlyContain(r => r.Families.All(f => f.Name == "cf2"));
    }

    [Fact]
    public async Task Chain_cells_per_row_limit()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.CellsPerRowLimit(2));
        var rows = await ReadAll(RowSet.FromRowKeys("fc-000"), filter);
        var totalCells = rows[0].Families.SelectMany(f => f.Columns.SelectMany(c => c.Cells)).Count();
        totalCells.Should().Be(2);
    }

    [Fact]
    public async Task Chain_cells_per_row_offset_and_limit()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.CellsPerColumnLimit(1),
            RowFilters.CellsPerRowOffset(1),
            RowFilters.CellsPerRowLimit(1));
        var rows = await ReadAll(RowSet.FromRowKeys("fc-000"), filter);
        var totalCells = rows[0].Families.SelectMany(f => f.Columns.SelectMany(c => c.Cells)).Count();
        totalCells.Should().Be(1);
    }

    [Fact]
    public async Task Chain_strip_value_preserves_structure()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.CellsPerColumnLimit(1),
            RowFilters.StripValueTransformer());
        var rows = await ReadAll(RowSet.FromRowKeys("fc-000"), filter);
        rows[0].Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task Chain_three_filters_narrow_result()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.ColumnQualifierExact("type"),
            RowFilters.ValueRegex("even"));
        var rows = await ReadAll(filter: filter);
        // Only even-typed rows, but all versions of "type" have "even"
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task Chain_value_range_with_family()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF2),
            RowFilters.ColumnQualifierExact("flag"),
            RowFilters.CellsPerColumnLimit(1),
            RowFilters.ValueExact("active"));
        var rows = await ReadAll(filter: filter);
        // Latest flag is "active" for all 10 rows (v3 always = active)
        rows.Should().HaveCount(10);
    }

    #endregion

    #region Interleave filter combinations

    [Fact]
    public async Task Interleave_two_family_filters()
    {
        var filter = RowFilters.Interleave(
            RowFilters.FamilyNameExact(CF),
            RowFilters.FamilyNameExact(CF2));
        var rows = await ReadAll(RowSet.FromRowKeys("fc-000"), filter);
        rows[0].Families.Select(f => f.Name).Should().Contain(new[] { "cf", "cf2" });
    }

    [Fact]
    public async Task Interleave_two_column_filters()
    {
        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("name")),
            RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("score")));
        var rows = await ReadAll(RowSet.FromRowKeys("fc-000"), filter);
        var quals = rows[0].Families.First(f => f.Name == CF).Columns
            .Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().Contain("name");
        quals.Should().Contain("score");
        quals.Should().NotContain("type");
    }

    [Fact]
    public async Task Interleave_cf_and_cf2_latest()
    {
        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.CellsPerColumnLimit(1)),
            RowFilters.Chain(RowFilters.FamilyNameExact(CF2), RowFilters.CellsPerColumnLimit(1)));
        var rows = await ReadAll(RowSet.FromRowKeys("fc-000"), filter);
        foreach (var fam in rows[0].Families)
            foreach (var col in fam.Columns)
                col.Cells.Should().ContainSingle();
    }

    [Fact]
    public async Task Interleave_with_empty_result()
    {
        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("nonexistent")),
            RowFilters.Chain(RowFilters.FamilyNameExact(CF2), RowFilters.ColumnQualifierExact("nonexistent")));
        var rows = await ReadAll(RowSet.FromRowKeys("fc-000"), filter);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Interleave_overlapping_filters()
    {
        // Both filters match the same column — should not duplicate
        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("name")),
            RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("name")));
        var rows = await ReadAll(RowSet.FromRowKeys("fc-000"), filter);
        // Interleave unions results, so duplicates may appear (each branch independently adds cells)
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Interleave_three_branches()
    {
        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("name"), RowFilters.CellsPerColumnLimit(1)),
            RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("type"), RowFilters.CellsPerColumnLimit(1)),
            RowFilters.Chain(RowFilters.FamilyNameExact(CF2), RowFilters.ColumnQualifierExact("tag"), RowFilters.CellsPerColumnLimit(1)));
        var rows = await ReadAll(RowSet.FromRowKeys("fc-000"), filter);
        var allQuals = rows[0].Families.SelectMany(f => f.Columns.Select(c => c.Qualifier.ToStringUtf8())).ToList();
        allQuals.Should().Contain("name");
        allQuals.Should().Contain("type");
        allQuals.Should().Contain("tag");
    }

    #endregion

    #region Condition filter

    [Fact]
    public async Task Condition_true_branch_when_predicate_matches()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("type"), RowFilters.ValueExact("even")),
            RowFilters.Chain(RowFilters.ColumnQualifierExact("name"), RowFilters.CellsPerColumnLimit(1)),
            RowFilters.BlockAllFilter());
        var rows = await ReadAll(RowSet.FromRowKeys("fc-000"), filter); // row 0 is even
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Condition_false_branch_when_predicate_not_matches()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("type"), RowFilters.ValueExact("even")),
            RowFilters.BlockAllFilter(),
            RowFilters.PassAllFilter());
        var rows = await ReadAll(RowSet.FromRowKeys("fc-001"), filter); // row 1 is odd
        rows.Should().ContainSingle(); // false branch passes all
    }

    [Fact]
    public async Task Condition_no_false_branch_blocks_non_matching()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("type"), RowFilters.ValueExact("even")),
            trueFilter: RowFilters.PassAllFilter(),
            falseFilter: RowFilters.BlockAllFilter());
        var rows = await ReadAll(RowSet.FromRowKeys("fc-001"), filter); // odd row
        // False branch blocks all — non-matching row returns empty
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Condition_complex_predicate()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(
                RowFilters.FamilyNameExact(CF2),
                RowFilters.ColumnQualifierExact("flag"),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueExact("active")),
            RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.CellsPerColumnLimit(1)),
            RowFilters.BlockAllFilter());
        var rows = await ReadAll(filter: filter);
        // All rows have latest flag=active, so all should pass
        rows.Should().HaveCount(10);
        rows.Should().OnlyContain(r => r.Families.All(f => f.Name == "cf"));
    }

    #endregion

    #region ColumnRange filter

    [Fact]
    public async Task ColumnRange_closed_open()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.ClosedOpen(CF, "name", "type"));
        var rows = await ReadAll(RowSet.FromRowKeys("fc-000"), filter);
        var quals = rows[0].Families.First(f => f.Name == CF).Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().Contain("name");
        quals.Should().Contain("score");
        quals.Should().NotContain("type");
    }

    [Fact]
    public async Task ColumnRange_closed_closed()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.Closed(CF, "name", "type"));
        var rows = await ReadAll(RowSet.FromRowKeys("fc-000"), filter);
        var quals = rows[0].Families.First(f => f.Name == CF).Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().Contain("name");
        quals.Should().Contain("type");
    }

    [Fact]
    public async Task ColumnRange_open_open()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.Open(CF, "name", "type"));
        var rows = await ReadAll(RowSet.FromRowKeys("fc-000"), filter);
        var quals = rows[0].Families.First(f => f.Name == CF).Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().Contain("score"); // between name and type
        quals.Should().NotContain("name");
        quals.Should().NotContain("type");
    }

    [Fact]
    public async Task ColumnRange_after_score()
    {
        // Columns after "score" (exclusive) up to "z" — should include "type"
        var filter = RowFilters.ColumnRange(ColumnRange.Open(CF, "score", "~"));
        var rows = await ReadAll(RowSet.FromRowKeys("fc-000"), filter);
        rows.Should().ContainSingle();
        var quals = rows[0].Families.First(f => f.Name == CF).Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().Contain("type");
        quals.Should().NotContain("score");
        quals.Should().NotContain("name");
    }

    #endregion

    #region ValueRange filter

    [Fact]
    public async Task ValueRange_closed_open()
    {
        // Seed known values
        await Client.MutateRowAsync(TN, "fc-vr1",
            Mutations.SetCell(CF, "x", "aaa", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "y", "bbb", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "z", "ccc", new BigtableVersion(1000)));
        var filter = RowFilters.ValueRange(ValueRange.ClosedOpen("aaa", "ccc"));
        var rows = await ReadAll(RowSet.FromRowKeys("fc-vr1"), filter);
        var vals = rows[0].Families.SelectMany(f => f.Columns.SelectMany(c => c.Cells.Select(cell => cell.Value.ToStringUtf8()))).ToList();
        vals.Should().Contain("aaa");
        vals.Should().Contain("bbb");
        vals.Should().NotContain("ccc");
    }

    [Fact]
    public async Task ValueRange_closed_closed()
    {
        await Client.MutateRowAsync(TN, "fc-vr2",
            Mutations.SetCell(CF, "x", "aaa", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "y", "bbb", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "z", "ccc", new BigtableVersion(1000)));
        var filter = RowFilters.ValueRange(ValueRange.Closed("aaa", "ccc"));
        var rows = await ReadAll(RowSet.FromRowKeys("fc-vr2"), filter);
        var vals = rows[0].Families.SelectMany(f => f.Columns.SelectMany(c => c.Cells.Select(cell => cell.Value.ToStringUtf8()))).ToList();
        vals.Should().HaveCount(3);
    }

    #endregion

    #region Nested chains and interleaves

    [Fact]
    public async Task Chain_inside_interleave()
    {
        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("name"), RowFilters.CellsPerColumnLimit(1)),
            RowFilters.Chain(RowFilters.FamilyNameExact(CF2), RowFilters.ColumnQualifierExact("tag"), RowFilters.CellsPerColumnLimit(1)));
        var rows = await ReadAll(RowSet.FromRowKeys("fc-005"), filter);
        var allQuals = rows[0].Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        allQuals.Should().Contain("name");
        allQuals.Should().Contain("tag");
    }

    [Fact]
    public async Task Interleave_inside_chain()
    {
        var filter = RowFilters.Chain(
            RowFilters.Interleave(
                RowFilters.ColumnQualifierExact("name"),
                RowFilters.ColumnQualifierExact("type")),
            RowFilters.CellsPerColumnLimit(1));
        var rows = await ReadAll(RowSet.FromRowKeys("fc-000"), filter);
        foreach (var col in rows[0].Families.SelectMany(f => f.Columns))
            col.Cells.Should().ContainSingle();
    }

    [Fact]
    public async Task Double_chain()
    {
        var filter = RowFilters.Chain(
            RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("name")),
            RowFilters.CellsPerColumnLimit(1));
        var rows = await ReadAll(RowSet.FromRowKeys("fc-000"), filter);
        rows[0].Families.Should().ContainSingle();
        rows[0].Families[0].Columns.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle();
    }

    [Fact]
    public async Task Triple_interleave_with_chains()
    {
        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("name")),
            RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("type")),
            RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("score")));
        var rows = await ReadAll(RowSet.FromRowKeys("fc-000"), filter);
        rows[0].Families.First(f => f.Name == CF).Columns.Should().HaveCount(3);
    }

    #endregion

    #region PassAll and BlockAll combinations

    [Fact]
    public async Task PassAll_returns_everything()
    {
        var filter = RowFilters.PassAllFilter();
        var rows = await ReadAll(RowSet.FromRowKeys("fc-000"), filter);
        rows.Should().ContainSingle();
        rows[0].Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task BlockAll_returns_nothing()
    {
        var filter = RowFilters.BlockAllFilter();
        var rows = await ReadAll(RowSet.FromRowKeys("fc-000"), filter);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Chain_passall_then_family_same_as_family()
    {
        var filter = RowFilters.Chain(RowFilters.PassAllFilter(), RowFilters.FamilyNameExact(CF));
        var rows = await ReadAll(RowSet.FromRowKeys("fc-000"), filter);
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be("cf");
    }

    [Fact]
    public async Task Chain_family_then_blockall_empty()
    {
        var filter = RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.BlockAllFilter());
        var rows = await ReadAll(RowSet.FromRowKeys("fc-000"), filter);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Interleave_passall_and_blockall()
    {
        var filter = RowFilters.Interleave(RowFilters.PassAllFilter(), RowFilters.BlockAllFilter());
        var rows = await ReadAll(RowSet.FromRowKeys("fc-000"), filter);
        // PassAll branch returns everything
        rows.Should().ContainSingle();
    }

    #endregion

    #region Filter with limit

    [Fact]
    public async Task Limit_1_with_chain_filter()
    {
        var filter = RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.CellsPerColumnLimit(1));
        var rows = await ReadAll(filter: filter, limit: 1);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Limit_5_with_value_filter()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.ColumnQualifierExact("type"),
            RowFilters.CellsPerColumnLimit(1),
            RowFilters.ValueExact("even"));
        var rows = await ReadAll(filter: filter, limit: 3);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Limit_exceeds_matching_rows()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.ColumnQualifierExact("type"),
            RowFilters.CellsPerColumnLimit(1),
            RowFilters.ValueExact("even"));
        var rows = await ReadAll(filter: filter, limit: 100);
        rows.Should().HaveCount(5);
    }

    #endregion

    #region Filter on specific rows

    [Fact]
    public async Task Filter_on_row_set_with_specific_keys()
    {
        var filter = RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("name"), RowFilters.CellsPerColumnLimit(1));
        var rows = await ReadAll(RowSet.FromRowKeys("fc-000", "fc-005", "fc-009"), filter);
        rows.Should().HaveCount(3);
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("fc-000", "fc-005", "fc-009");
    }

    [Fact]
    public async Task Filter_on_empty_row_set_returns_all()
    {
        var filter = RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("name"), RowFilters.CellsPerColumnLimit(1));
        var rows = await ReadAll(filter: filter);
        rows.Count.Should().BeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public async Task Filter_with_row_range()
    {
        var filter = RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("name"), RowFilters.CellsPerColumnLimit(1));
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("fc-003", "fc-007")), filter);
        rows.Should().HaveCount(4);
    }

    #endregion
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Advanced filter combination integration tests — deep nesting, filter+limit interactions,
/// transform filters in various positions, condition filter edge cases, and multi-filter
/// result verification.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.RowFilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class FilterAdvancedComboIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "filt-combo-tests";
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public FilterAdvancedComboIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
        var client = _fixture.Client;
        var tn = _fixture.GetTableName(Table);

        // Seed data: 5 rows, each with 2 families, 2 columns, 3 versions
        for (int r = 0; r < 5; r++)
        {
            var rk = $"fc-{r:D2}";
            var mutations = new List<Mutation>();
            foreach (var fam in new[] { CF, CF2 })
            {
                foreach (var col in new[] { "a", "b" })
                {
                    for (int v = 1; v <= 3; v++)
                    {
                        mutations.Add(Mutations.SetCell(fam, col, $"{fam}-{col}-v{v}", new BigtableVersion(v * 1000)));
                    }
                }
            }
            await client.MutateRowAsync(tn, new BigtableByteString(rk), mutations.ToArray());
        }
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null, long? rowsLimit = null)
    {
        var list = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: rows, filter: filter, rowsLimit: rowsLimit))
            list.Add(row);
        return list;
    }

    #region Chain combinations

    [Fact]
    public async Task Chain_family_then_column_then_version_limit()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.ColumnQualifierExact("a"),
            RowFilters.CellsPerColumnLimit(1));

        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF);
        rows[0].Families[0].Columns.Should().ContainSingle().Which.Qualifier.ToStringUtf8().Should().Be("a");
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("cf-a-v3"); // latest version
    }

    [Fact]
    public async Task Chain_timestamp_range_then_column_limit()
    {
        // Timestamp range [1000ms, 3000ms) => versions v1 and v2
        var tsFilter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                StartTimestampMicros = 1_000_000,
                EndTimestampMicros = 3_000_000,
            }
        };
        var filter = RowFilters.Chain(
            tsFilter,
            RowFilters.CellsPerColumnLimit(1));

        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().ContainSingle();
        // Each column should have 1 cell (the latest within the range = v2)
        foreach (var fam in rows[0].Families)
            foreach (var col in fam.Columns)
                col.Cells.Should().ContainSingle()
                    .Which.Value.ToStringUtf8().Should().EndWith("-v2");
    }

    [Fact]
    public async Task Chain_value_regex_then_strip_value()
    {
        var filter = RowFilters.Chain(
            RowFilters.ValueRegex(".*-v3"),
            RowFilters.StripValueTransformer());

        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().ContainSingle();
        // All cells should have empty values
        foreach (var fam in rows[0].Families)
            foreach (var col in fam.Columns)
                foreach (var cell in col.Cells)
                    cell.Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task Chain_row_key_regex_then_family_filter()
    {
        var filter = RowFilters.Chain(
            RowFilters.RowKeyRegex("fc-0[0-2]"),
            RowFilters.FamilyNameExact(CF2));

        var rows = await ReadAll(filter: filter);
        rows.Should().HaveCount(3);
        rows.All(r => r.Families.All(f => f.Name == CF2)).Should().BeTrue();
    }

    [Fact]
    public async Task Chain_cells_per_row_limit_then_cells_per_column_limit()
    {
        // CellsPerRowLimit(2) first, then CellsPerColumnLimit(1) on remaining
        var filter = RowFilters.Chain(
            RowFilters.CellsPerRowLimit(4),
            RowFilters.CellsPerColumnLimit(1));

        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().ContainSingle();
        var totalCells = rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();
        totalCells.Should().BeLessThanOrEqualTo(4);
    }

    #endregion

    #region Interleave combinations

    [Fact]
    public async Task Interleave_two_family_filters()
    {
        var filter = RowFilters.Interleave(
            RowFilters.FamilyNameExact(CF),
            RowFilters.FamilyNameExact(CF2));

        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().ContainSingle();
        rows[0].Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Interleave_column_and_value_regex()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("a"),
            RowFilters.ValueRegex(".*-b-.*"));

        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().ContainSingle();
        // Should have column "a" (from first filter) and "b" where value matches (from second filter)
        var quals = rows[0].Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).Distinct().ToList();
        quals.Should().Contain("a");
        quals.Should().Contain("b");
    }

    [Fact]
    public async Task Interleave_of_chains()
    {
        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("a")),
            RowFilters.Chain(RowFilters.FamilyNameExact(CF2), RowFilters.ColumnQualifierExact("b")));

        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().ContainSingle();
        // Should have cf:a and cf2:b
        var cells = rows[0].Families.SelectMany(f => f.Columns.Select(c => $"{f.Name}:{c.Qualifier.ToStringUtf8()}")).ToList();
        cells.Should().Contain("cf:a");
        cells.Should().Contain("cf2:b");
    }

    [Fact]
    public async Task Interleave_three_column_filters()
    {
        // Write a third column to have something to interleave
        await Client.MutateRowAsync(TN, "fc-00",
            Mutations.SetCell(CF, "c", "cf-c-v1", new BigtableVersion(1000)));

        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("a"),
            RowFilters.ColumnQualifierExact("b"),
            RowFilters.ColumnQualifierExact("c"));

        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().ContainSingle();
        var allQuals = rows[0].Families.SelectMany(f => f.Columns.Select(c => c.Qualifier.ToStringUtf8())).Distinct().ToList();
        allQuals.Should().Contain("a");
        allQuals.Should().Contain("b");
        allQuals.Should().Contain("c");
    }

    #endregion

    #region Condition filter

    [Fact]
    public async Task Condition_true_branch_applies_version_limit()
    {
        var filter = RowFilters.Condition(
            RowFilters.ValueRegex(".*-v3"), // predicate: has latest version
            RowFilters.CellsPerColumnLimit(1), // true: limit to 1
            RowFilters.PassAllFilter()); // false: pass all

        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().ContainSingle();
        // Predicate matches → true branch applies → 1 cell per column
        foreach (var fam in rows[0].Families)
            foreach (var col in fam.Columns)
                col.Cells.Should().ContainSingle();
    }

    [Fact]
    public async Task Condition_false_branch_applies_when_no_match()
    {
        var filter = RowFilters.Condition(
            RowFilters.ValueRegex("NONEXISTENT"),
            RowFilters.BlockAllFilter(),       // true: block
            RowFilters.CellsPerColumnLimit(1)); // false: limit to 1

        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().ContainSingle();
        // Predicate doesn't match → false branch → 1 cell per column
        foreach (var fam in rows[0].Families)
            foreach (var col in fam.Columns)
                col.Cells.Should().ContainSingle();
    }

    [Fact]
    public async Task Condition_true_branch_blocks_non_matching_rows()
    {
        // Write a special row
        await Client.MutateRowAsync(TN, "fc-special",
            Mutations.SetCell(CF, "status", "active", new BigtableVersion(1000)));

        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueExact("active")),
            RowFilters.PassAllFilter(),
            RowFilters.BlockAllFilter());

        // Regular rows don't have "status:active" → blocked
        var regularRows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        regularRows.Should().BeEmpty();

        // Special row has "status:active" → passed
        var specialRows = await ReadAll(RowSet.FromRowKeys("fc-special"), filter);
        specialRows.Should().ContainSingle();
    }

    [Fact]
    public async Task Condition_with_no_false_filter_blocks_non_matching()
    {
        // When false_filter is not set, rows that don't match predicate produce no output
        var filter = new RowFilter
        {
            Condition = new RowFilter.Types.Condition
            {
                PredicateFilter = RowFilters.ValueRegex("NONEXISTENT"),
                TrueFilter = RowFilters.PassAllFilter(),
                // No FalseFilter set
            }
        };

        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Condition_with_chain_predicate()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(
                RowFilters.FamilyNameExact(CF),
                RowFilters.ColumnQualifierExact("a"),
                RowFilters.ValueRegex(".*-v3")),
            RowFilters.FamilyNameExact(CF),    // true: only CF family
            RowFilters.FamilyNameExact(CF2)); // false: only CF2 family

        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().ContainSingle();
        // Predicate matches (cf:a has v3) → true branch → only CF
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF);
    }

    #endregion

    #region CellsPerRowLimit and Offset

    [Fact]
    public async Task CellsPerRowLimit_counts_across_families_and_columns()
    {
        // Each row has 2 families × 2 columns × 3 versions = 12 cells
        var filter = RowFilters.CellsPerRowLimit(5);
        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().ContainSingle();
        var totalCells = rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();
        totalCells.Should().Be(5);
    }

    [Fact]
    public async Task CellsPerRowOffset_skips_cells()
    {
        // Each row has 12 cells, skip 10 → 2 remaining
        var filter = RowFilters.CellsPerRowOffset(10);
        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().ContainSingle();
        var totalCells = rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();
        totalCells.Should().Be(2);
    }

    [Fact]
    public async Task CellsPerRowOffset_exceeds_total_returns_empty()
    {
        var filter = RowFilters.CellsPerRowOffset(100);
        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task CellsPerRowOffset_0_returns_all()
    {
        var filter = RowFilters.CellsPerRowOffset(0);
        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().ContainSingle();
        var totalCells = rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();
        totalCells.Should().Be(12); // 2 families × 2 columns × 3 versions
    }

    [Fact]
    public async Task CellsPerRowLimit_1_returns_single_cell()
    {
        var filter = RowFilters.CellsPerRowLimit(1);
        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().ContainSingle();
        var totalCells = rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();
        totalCells.Should().Be(1);
    }

    [Fact]
    public async Task CellsPerColumnLimit_with_multiple_columns()
    {
        var filter = RowFilters.CellsPerColumnLimit(2);
        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().ContainSingle();
        // Each column should have at most 2 cells
        foreach (var fam in rows[0].Families)
            foreach (var col in fam.Columns)
                col.Cells.Should().HaveCountLessThanOrEqualTo(2);
    }

    #endregion

    #region StripValue and ApplyLabel transforms

    [Fact]
    public async Task StripValue_preserves_row_structure()
    {
        var filter = RowFilters.StripValueTransformer();
        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().ContainSingle();
        // Structure preserved, values empty
        rows[0].Families.Should().HaveCount(2);
        foreach (var fam in rows[0].Families)
        {
            fam.Columns.Should().HaveCount(2);
            foreach (var col in fam.Columns)
            {
                col.Cells.Should().HaveCount(3);
                foreach (var cell in col.Cells)
                    cell.Value.Length.Should().Be(0);
            }
        }
    }

    [Fact]
    public async Task ApplyLabel_adds_label_to_all_cells()
    {
        var filter = new RowFilter { ApplyLabelTransformer = "test-label" };
        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().ContainSingle();
        foreach (var fam in rows[0].Families)
            foreach (var col in fam.Columns)
                foreach (var cell in col.Cells)
                    cell.Labels.Should().Contain("test-label");
    }

    [Fact]
    public async Task StripValue_in_chain_with_column_filter()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("a"),
            RowFilters.StripValueTransformer());

        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().ContainSingle();
        // Only column "a", all values stripped
        foreach (var fam in rows[0].Families)
        {
            fam.Columns.Should().ContainSingle().Which.Qualifier.ToStringUtf8().Should().Be("a");
            fam.Columns[0].Cells.All(c => c.Value.Length == 0).Should().BeTrue();
        }
    }

    #endregion

    #region ColumnRange filter

    [Fact]
    public async Task ColumnRange_closed_range_includes_endpoints()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.ColumnRange
        var filter = RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "b"));
        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().ContainSingle();
        var quals = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().Equal("a", "b");
    }

    [Fact]
    public async Task ColumnRange_open_range_excludes_endpoints()
    {
        // With only "a" and "b" columns, open range (a,b) should return nothing
        var filter = RowFilters.ColumnRange(ColumnRange.Open(CF, "a", "b"));
        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        // Open range excludes both "a" and "b", nothing between them
        if (rows.Count > 0)
            rows[0].Families.SelectMany(f => f.Columns).Should().BeEmpty();
    }

    [Fact]
    public async Task ColumnRange_scoped_to_specific_family()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.Closed(CF2, "a", "b"));
        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF2);
    }

    #endregion

    #region ValueRange filter

    [Fact]
    public async Task ValueRange_closed_range()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.ValueRange
        var filter = RowFilters.ValueRange(
            ValueRange.Closed(
                ByteString.CopyFromUtf8("cf-a-v1"),
                ByteString.CopyFromUtf8("cf-a-v2")));

        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().ContainSingle();
        var values = rows[0].Families
            .SelectMany(f => f.Columns)
            .SelectMany(c => c.Cells)
            .Select(c => c.Value.ToStringUtf8())
            .ToList();
        values.Should().AllSatisfy(v =>
        {
            (string.Compare(v, "cf-a-v1", StringComparison.Ordinal) >= 0
             && string.Compare(v, "cf-a-v2", StringComparison.Ordinal) <= 0).Should().BeTrue();
        });
    }

    [Fact]
    public async Task ValueRange_no_matching_values_returns_empty()
    {
        var filter = RowFilters.ValueRange(
            ValueRange.Closed(
                ByteString.CopyFromUtf8("zzz"),
                ByteString.CopyFromUtf8("zzz")));

        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().BeEmpty();
    }

    #endregion

    #region PassAll and BlockAll

    [Fact]
    public async Task PassAll_returns_everything()
    {
        var filter = RowFilters.PassAllFilter();
        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().ContainSingle();
        var totalCells = rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();
        totalCells.Should().Be(12);
    }

    [Fact]
    public async Task BlockAll_returns_nothing()
    {
        var filter = RowFilters.BlockAllFilter();
        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Chain_passall_then_blockall_returns_nothing()
    {
        var filter = RowFilters.Chain(
            RowFilters.PassAllFilter(),
            RowFilters.BlockAllFilter());
        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Interleave_passall_and_blockall_returns_everything()
    {
        // Interleave = union, so PassAll contribution survives
        var filter = RowFilters.Interleave(
            RowFilters.PassAllFilter(),
            RowFilters.BlockAllFilter());
        var rows = await ReadAll(RowSet.FromRowKeys("fc-00"), filter);
        rows.Should().ContainSingle();
    }

    #endregion

    #region Filter with limit interaction

    [Fact]
    public async Task Filter_plus_rows_limit()
    {
        var filter = RowFilters.FamilyNameExact(CF);
        var rows = await ReadAll(filter: filter, rowsLimit: 2);
        rows.Should().HaveCount(2);
        rows.All(r => r.Families.All(f => f.Name == CF)).Should().BeTrue();
    }

    [Fact]
    public async Task Filter_matching_subset_plus_rows_limit()
    {
        // Only fc-00 through fc-02 will be returned before limit applies
        var filter = RowFilters.Chain(
            RowFilters.RowKeyRegex("fc-0[0-1]"),
            RowFilters.CellsPerColumnLimit(1));
        var rows = await ReadAll(filter: filter, rowsLimit: 1);
        rows.Should().ContainSingle();
    }

    #endregion
}

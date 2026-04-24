using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Advanced RowFilter integration tests — covers filter types not yet tested:
/// ColumnRange, ValueRange, TimestampRange, CellsPerRowLimit, CellsPerRowOffset,
/// RowSample, ApplyLabelTransformer, Condition, Sink, and complex compositions.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class AdvancedFilterIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "adv-filter-tests";
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public AdvancedFilterIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
        var c = Client;
        var tn = TN;

        // Row r1: cf:a=alpha, cf:b=beta, cf:c=gamma, cf2:x=xray — all at ts 1000
        await c.MutateRowAsync(tn, new BigtableByteString("r1"),
            Mutations.SetCell(CF, "a", "alpha", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "beta", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "gamma", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "x", "xray", new BigtableVersion(1000)));

        // Row r2: cf:a with 5 versions at ts 1000..5000
        for (int i = 1; i <= 5; i++)
            await c.MutateRowAsync(tn, new BigtableByteString("r2"),
                Mutations.SetCell(CF, "a", $"v{i}", new BigtableVersion(i * 1000)));

        // Row r3: cf:a=100 (numeric), cf:b=200
        await c.MutateRowAsync(tn, new BigtableByteString("r3"),
            Mutations.SetCell(CF, "a", "100", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "200", new BigtableVersion(1000)));

        // Rows r4..r8 for sampling/range tests
        for (int i = 4; i <= 8; i++)
            await c.MutateRowAsync(tn, new BigtableByteString($"r{i}"),
                Mutations.SetCell(CF, "a", $"val{i}", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null)
    {
        var list = new List<Row>();
        var stream = Client.ReadRows(TN, rows: rows, filter: filter);
        var e = stream.GetAsyncEnumerator(default);
        while (await e.MoveNextAsync()) list.Add(e.Current);
        return list;
    }

    #region ColumnRangeFilter

    [Fact]
    public async Task ColumnRangeFilter_closed_open_boundary()
    {
        // Ref: ColumnRange — start_qualifier_closed/end_qualifier_open
        var filter = new RowFilter
        {
            ColumnRangeFilter = new ColumnRange
            {
                FamilyName = CF,
                StartQualifierClosed = ByteString.CopyFromUtf8("a"),
                EndQualifierOpen = ByteString.CopyFromUtf8("c"),
            }
        };
        var rows = await ReadAll(RowSet.FromRowKeys("r1"), filter);
        rows.Should().ContainSingle();
        var cols = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("a");
        cols.Should().Contain("b");
        cols.Should().NotContain("c"); // open end
    }

    [Fact]
    public async Task ColumnRangeFilter_open_closed_boundary()
    {
        // Ref: ColumnRange — start_qualifier_open/end_qualifier_closed
        var filter = new RowFilter
        {
            ColumnRangeFilter = new ColumnRange
            {
                FamilyName = CF,
                StartQualifierOpen = ByteString.CopyFromUtf8("a"),
                EndQualifierClosed = ByteString.CopyFromUtf8("c"),
            }
        };
        var rows = await ReadAll(RowSet.FromRowKeys("r1"), filter);
        rows.Should().ContainSingle();
        var cols = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().NotContain("a"); // open start
        cols.Should().Contain("b");
        cols.Should().Contain("c");
    }

    [Fact]
    public async Task ColumnRangeFilter_no_matching_columns_returns_empty_row()
    {
        var filter = new RowFilter
        {
            ColumnRangeFilter = new ColumnRange
            {
                FamilyName = CF,
                StartQualifierClosed = ByteString.CopyFromUtf8("z"),
                EndQualifierOpen = ByteString.CopyFromUtf8("zz"),
            }
        };
        var rows = await ReadAll(RowSet.FromRowKeys("r1"), filter);
        rows.Should().BeEmpty();
    }

    #endregion

    #region ValueRangeFilter

    [Fact]
    public async Task ValueRangeFilter_closed_open_boundary()
    {
        // Ref: ValueRange — start_value_closed/end_value_open
        var filter = new RowFilter
        {
            ValueRangeFilter = new ValueRange
            {
                StartValueClosed = ByteString.CopyFromUtf8("alpha"),
                EndValueOpen = ByteString.CopyFromUtf8("gamma"),
            }
        };
        var rows = await ReadAll(RowSet.FromRowKeys("r1"), filter);
        rows.Should().ContainSingle();
        var values = rows[0].Families.SelectMany(f => f.Columns)
            .SelectMany(c => c.Cells).Select(c => c.Value.ToStringUtf8()).ToList();
        values.Should().Contain("alpha");
        values.Should().Contain("beta");
        values.Should().NotContain("gamma"); // open end
    }

    [Fact]
    public async Task ValueRangeFilter_open_closed_boundary()
    {
        var filter = new RowFilter
        {
            ValueRangeFilter = new ValueRange
            {
                StartValueOpen = ByteString.CopyFromUtf8("alpha"),
                EndValueClosed = ByteString.CopyFromUtf8("gamma"),
            }
        };
        var rows = await ReadAll(RowSet.FromRowKeys("r1"), filter);
        rows.Should().ContainSingle();
        var values = rows[0].Families.SelectMany(f => f.Columns)
            .SelectMany(c => c.Cells).Select(c => c.Value.ToStringUtf8()).ToList();
        values.Should().NotContain("alpha"); // open start
        values.Should().Contain("beta");
        values.Should().Contain("gamma");
    }

    #endregion

    #region TimestampRangeFilter

    [Fact]
    public async Task TimestampRangeFilter_inclusive_start_exclusive_end()
    {
        // Ref: TimestampRange — start_timestamp_micros is inclusive, end_timestamp_micros is exclusive
        // Row r2 has versions at ts 1000ms..5000ms → micros 1000000..5000000
        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                StartTimestampMicros = 2_000_000, // includes ts=2000ms
                EndTimestampMicros = 4_000_000,   // excludes ts=4000ms
            }
        };
        var rows = await ReadAll(RowSet.FromRowKeys("r2"), filter);
        rows.Should().ContainSingle();
        var cells = rows[0].Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2);
        cells.Select(c => c.Value.ToStringUtf8()).Should().Contain("v2");
        cells.Select(c => c.Value.ToStringUtf8()).Should().Contain("v3");
    }

    [Fact]
    public async Task TimestampRangeFilter_start_only()
    {
        // Only startTimestampMicros set — returns cells from that timestamp onwards
        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                StartTimestampMicros = 4_000_000,
            }
        };
        var rows = await ReadAll(RowSet.FromRowKeys("r2"), filter);
        rows.Should().ContainSingle();
        var cells = rows[0].Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2); // v4 and v5
    }

    [Fact]
    public async Task TimestampRangeFilter_end_only()
    {
        // Only endTimestampMicros set — returns cells before that timestamp
        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                EndTimestampMicros = 3_000_000, // excludes ts=3000ms
            }
        };
        var rows = await ReadAll(RowSet.FromRowKeys("r2"), filter);
        rows.Should().ContainSingle();
        var cells = rows[0].Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2); // v1 and v2
    }

    #endregion

    #region CellsPerRowLimit and CellsPerRowOffset

    [Fact]
    public async Task CellsPerRowLimit_returns_first_n_cells()
    {
        // Ref: "Return at most this many cells per row."
        var filter = RowFilters.CellsPerRowLimit(2);
        var rows = await ReadAll(RowSet.FromRowKeys("r1"), filter);
        rows.Should().ContainSingle();
        var totalCells = rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();
        totalCells.Should().Be(2);
    }

    [Fact]
    public async Task CellsPerRowOffset_skips_first_n_cells()
    {
        // Ref: "Skip the first N cells of each row, matching subsequent cells."
        var filter = RowFilters.CellsPerRowOffset(2);
        var rows = await ReadAll(RowSet.FromRowKeys("r1"), filter);
        rows.Should().ContainSingle();
        // r1 has 4 cells total (cf:a, cf:b, cf:c, cf2:x), skip 2 → 2 remain
        var totalCells = rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();
        totalCells.Should().Be(2);
    }

    [Fact]
    public async Task CellsPerRowOffset_exceeds_cell_count_returns_empty()
    {
        var filter = RowFilters.CellsPerRowOffset(100);
        var rows = await ReadAll(RowSet.FromRowKeys("r1"), filter);
        rows.Should().BeEmpty();
    }

    #endregion

    #region RowSampleFilter

    [Fact]
    public async Task RowSampleFilter_probability_1_returns_all()
    {
        // Ref: "Match a row with probability p."
        var filter = RowFilters.RowSample(1.0);
        var rows = await ReadAll(filter: filter);
        rows.Count.Should().BeGreaterThanOrEqualTo(8); // all seeded rows
    }

    [Fact]
    public async Task RowSampleFilter_probability_0_returns_none()
    {
        var filter = RowFilters.RowSample(0.0);
        var rows = await ReadAll(filter: filter);
        rows.Should().BeEmpty();
    }

    #endregion

    #region ApplyLabelTransformer

    [Fact]
    public async Task ApplyLabelTransformer_adds_label_to_cells()
    {
        // Ref: "Applies the given label to all cells in the output row."
        var filter = new RowFilter { ApplyLabelTransformer = "test-label" };
        var rows = await ReadAll(RowSet.FromRowKeys("r1"), filter);
        rows.Should().ContainSingle();
        var cells = rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells);
        cells.Should().AllSatisfy(c => c.Labels.Should().Contain("test-label"));
    }

    #endregion

    #region Condition filter

    [Fact]
    public async Task Condition_filter_applies_true_branch_when_predicate_matches()
    {
        // Ref: "if predicate matches, apply true_filter; otherwise apply false_filter"
        var filter = RowFilters.Condition(
            RowFilters.ValueRegex("alpha"),
            trueFilter: RowFilters.StripValueTransformer(),
            falseFilter: RowFilters.PassAllFilter());
        var rows = await ReadAll(RowSet.FromRowKeys("r1"), filter);
        rows.Should().ContainSingle();
        // r1 matches the predicate (has "alpha"), so StripValue should be applied
        var values = rows[0].Families.SelectMany(f => f.Columns)
            .SelectMany(c => c.Cells).Select(c => c.Value.ToStringUtf8()).ToList();
        values.Should().AllSatisfy(v => v.Should().BeEmpty());
    }

    [Fact]
    public async Task Condition_filter_applies_false_branch_when_predicate_does_not_match()
    {
        var filter = RowFilters.Condition(
            RowFilters.ValueRegex("NONEXISTENT"),
            trueFilter: RowFilters.BlockAllFilter(),
            falseFilter: RowFilters.PassAllFilter());
        var rows = await ReadAll(RowSet.FromRowKeys("r3"), filter);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Condition_filter_with_no_false_filter_blocks_non_matching()
    {
        // When false_filter is BlockAll, non-matching rows produce no output
        var filter = RowFilters.Condition(
            RowFilters.ValueRegex("NONEXISTENT"),
            trueFilter: RowFilters.PassAllFilter(),
            falseFilter: RowFilters.BlockAllFilter());
        var rows = await ReadAll(RowSet.FromRowKeys("r3"), filter);
        rows.Should().BeEmpty();
    }

    #endregion

    #region Sink filter

    [Fact]
    [Trait(TestTraits.Target, TestTraits.InMemoryOnly)]
    public async Task Sink_filter_passes_all_cells()
    {
        // Ref: "Hook for introspection — outputs cells directly to final output"
        var filter = new RowFilter { Sink = true };
        var rows = await ReadAll(RowSet.FromRowKeys("r1"), filter);
        rows.Should().ContainSingle();
    }

    #endregion

    #region Complex filter compositions

    [Fact]
    public async Task Chain_of_three_filters()
    {
        // Chain: family regex → column qualifier exact → cells per column limit
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameRegex(CF),
            RowFilters.ColumnQualifierExact("a"),
            RowFilters.CellsPerColumnLimit(1));
        var rows = await ReadAll(RowSet.FromRowKeys("r2"), filter);
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v5");
    }

    [Fact]
    public async Task Interleave_with_overlapping_filters_deduplicates()
    {
        // Interleave of two filters that both match column "a"
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("a"),
            RowFilters.FamilyNameRegex(CF));
        var rows = await ReadAll(RowSet.FromRowKeys("r3"), filter);
        rows.Should().ContainSingle();
        // Both filters match cf:a and cf:b — interleave unions them
        var cols = rows[0].Families.SelectMany(f => f.Columns)
            .Select(c => c.Qualifier.ToStringUtf8()).Distinct().ToList();
        cols.Should().Contain("a");
        cols.Should().Contain("b");
    }

    [Fact]
    public async Task Chain_then_interleave_composition()
    {
        // Chain strips values, then interleave checks columns
        var filter = RowFilters.Chain(
            RowFilters.StripValueTransformer(),
            RowFilters.Interleave(
                RowFilters.ColumnQualifierExact("a"),
                RowFilters.ColumnQualifierExact("c")));
        var rows = await ReadAll(RowSet.FromRowKeys("r1"), filter);
        rows.Should().ContainSingle();
        var allCells = rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        allCells.Should().AllSatisfy(c => c.Value.Should().BeEquivalentTo(ByteString.Empty));
    }

    [Fact]
    public async Task ColumnQualifierRegex_filters_by_pattern()
    {
        // Ref: ColumnQualifierRegexFilter — "Matches only cells from columns whose qualifiers satisfy the given RE2 regex."
        var filter = RowFilters.ColumnQualifierRegex("[ab]");
        var rows = await ReadAll(RowSet.FromRowKeys("r1"), filter);
        rows.Should().ContainSingle();
        var cols = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("a");
        cols.Should().Contain("b");
        cols.Should().NotContain("c");
    }

    [Fact]
    public async Task FamilyNameRegex_filters_across_families()
    {
        var filter = RowFilters.FamilyNameRegex("cf2");
        var rows = await ReadAll(RowSet.FromRowKeys("r1"), filter);
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle();
        rows[0].Families[0].Name.Should().Be(CF2);
    }

    [Fact]
    public async Task ValueExact_filter_matches_exact_value()
    {
        var filter = RowFilters.ValueExact("alpha");
        var rows = await ReadAll(RowSet.FromRowKeys("r1"), filter);
        rows.Should().ContainSingle();
        rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Should().AllSatisfy(c => c.Value.ToStringUtf8().Should().Be("alpha"));
    }

    #endregion
}

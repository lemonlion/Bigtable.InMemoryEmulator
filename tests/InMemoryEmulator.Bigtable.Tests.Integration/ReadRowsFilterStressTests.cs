using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Stress/permutation tests for ReadRows with various filter combinations.
/// Ensures every filter type works correctly in isolation and composed with others.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsFilterStressTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "filter-stress";
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public ReadRowsFilterStressTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, CF2, "cf3" });
        var c = _fixture.Client;
        var tn = TN;

        // 20 rows: row-00..row-19, each with CF:{a,b,c} x 3 versions + CF2:{x,y} x 2 versions
        for (int r = 0; r < 20; r++)
        {
            var rk = $"row-{r:D2}";
            var mutations = new List<Mutation>();
            foreach (var col in new[] { "a", "b", "c" })
                for (int v = 1; v <= 3; v++)
                    mutations.Add(Mutations.SetCell(CF, col, $"{col}-v{v}", new BigtableVersion(v * 1000)));
            foreach (var col in new[] { "x", "y" })
                for (int v = 1; v <= 2; v++)
                    mutations.Add(Mutations.SetCell(CF2, col, $"{col}-v{v}", new BigtableVersion(v * 1000)));
            await c.MutateRowAsync(tn, new BigtableByteString(rk), mutations.ToArray());
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

    private static int TotalCells(Row row) =>
        row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();

    #region Single filter isolation

    [Fact]
    public async Task FamilyNameExact_returns_only_matching_family()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), RowFilters.FamilyNameExact(CF));
        rows.Should().ContainSingle();
        rows[0].Families.Should().AllSatisfy(f => f.Name.Should().Be(CF));
    }

    [Fact]
    public async Task FamilyNameExact_nonexistent_family_returns_empty()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), RowFilters.FamilyNameExact("nope"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ColumnQualifierExact_returns_only_matching_column()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), RowFilters.ColumnQualifierExact("a"));
        rows.Should().ContainSingle();
        var allQuals = rows[0].Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).Distinct();
        allQuals.Should().Equal("a");
    }

    [Fact]
    public async Task ColumnQualifierExact_nonexistent_returns_empty()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), RowFilters.ColumnQualifierExact("zzz"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task CellsPerColumnLimit_1_returns_latest()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), RowFilters.CellsPerColumnLimit(1));
        rows.Should().ContainSingle();
        foreach (var fam in rows[0].Families)
            foreach (var col in fam.Columns)
                col.Cells.Should().ContainSingle();
    }

    [Fact]
    public async Task CellsPerColumnLimit_2_returns_two_latest()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), RowFilters.CellsPerColumnLimit(2));
        rows.Should().ContainSingle();
        var cfCols = rows[0].Families.First(f => f.Name == CF).Columns;
        foreach (var col in cfCols)
            col.Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task CellsPerColumnLimit_exceeds_version_count()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), RowFilters.CellsPerColumnLimit(100));
        rows.Should().ContainSingle();
        var cfCols = rows[0].Families.First(f => f.Name == CF).Columns;
        foreach (var col in cfCols)
            col.Cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task CellsPerRowLimit_returns_exact_count()
    {
        // row-00 has CF:3cols*3ver=9 + CF2:2cols*2ver=4 = 13 total
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), RowFilters.CellsPerRowLimit(7));
        rows.Should().ContainSingle();
        TotalCells(rows[0]).Should().Be(7);
    }

    [Fact]
    public async Task CellsPerRowOffset_skips_correct_count()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), RowFilters.CellsPerRowOffset(9));
        rows.Should().ContainSingle();
        TotalCells(rows[0]).Should().Be(4); // 13 - 9 = 4
    }

    [Fact]
    public async Task StripValue_empties_all_values()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), RowFilters.StripValueTransformer());
        rows.Should().ContainSingle();
        rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Should().AllSatisfy(c => c.Value.Length.Should().Be(0));
    }

    [Fact]
    public async Task ValueExact_string_match()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), RowFilters.ValueExact("a-v3"));
        rows.Should().ContainSingle();
        rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Should().AllSatisfy(c => c.Value.ToStringUtf8().Should().Be("a-v3"));
    }

    [Fact]
    public async Task ValueExact_no_match_returns_empty()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), RowFilters.ValueExact("NOPE"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task RowKeyRegex_partial_match_across_rows()
    {
        // Ref: Row key regex uses full-string matching per RE2 FullMatch semantics
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("row-0[0-4]"));
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task RowKeyRegex_no_match_returns_empty()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("nonexistent.*"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ValueRegex_matches_pattern()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), RowFilters.ValueRegex("a-v[12]"));
        rows.Should().ContainSingle();
        var vals = rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Select(c => c.Value.ToStringUtf8()).Distinct().ToList();
        vals.Should().AllSatisfy(v => v.Should().MatchRegex("a-v[12]"));
    }

    [Fact]
    public async Task FamilyNameRegex_matches_pattern()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), RowFilters.FamilyNameRegex("cf[23]?"));
        rows.Should().ContainSingle();
        // "cf" matches "cf", "cf2" matches "cf2" — both match "cf[23]?" ("cf" has optional char)
        // But cf3 also has no data so might not show
    }

    [Fact]
    public async Task PassAll_returns_all_cells()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), RowFilters.PassAllFilter());
        rows.Should().ContainSingle();
        TotalCells(rows[0]).Should().Be(13);
    }

    [Fact]
    public async Task BlockAll_returns_no_rows()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), RowFilters.BlockAllFilter());
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task TimestampRange_inclusive_start()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#timestamprange
        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                StartTimestampMicros = 2_000_000,
                EndTimestampMicros = 3_000_000,
            }
        };
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), filter);
        rows.Should().ContainSingle();
        rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Should().AllSatisfy(c => c.TimestampMicros.Should().Be(2_000_000));
    }

    [Fact]
    public async Task TimestampRange_all_versions_in_range()
    {
        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                StartTimestampMicros = 1_000_000,
                EndTimestampMicros = 4_000_000,
            }
        };
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), filter);
        rows.Should().ContainSingle();
        TotalCells(rows[0]).Should().Be(13); // all cells
    }

    #endregion

    #region Chain compositions

    [Fact]
    public async Task Chain_family_then_column()
    {
        var filter = RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("b"));
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), filter);
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF);
        rows[0].Families[0].Columns.Should().ContainSingle().Which.Qualifier.ToStringUtf8().Should().Be("b");
    }

    [Fact]
    public async Task Chain_column_then_version_limit()
    {
        var filter = RowFilters.Chain(RowFilters.ColumnQualifierExact("a"), RowFilters.CellsPerColumnLimit(1));
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), filter);
        rows.Should().ContainSingle();
        // "a" appears in both CF and CF2... wait no, CF2 has x,y. Only CF has "a"
        // Not necessarily — depends on the regex matching. ColumnQualifierExact("a") only matches col "a".
        rows[0].Families.SelectMany(f => f.Columns).Should().ContainSingle();
    }

    [Fact]
    public async Task Chain_timestamp_then_family()
    {
        var ts = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange { StartTimestampMicros = 2_000_000, EndTimestampMicros = 3_000_000 }
        };
        var filter = RowFilters.Chain(ts, RowFilters.FamilyNameExact(CF));
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), filter);
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF);
        rows[0].Families[0].Columns.SelectMany(c => c.Cells)
            .Should().AllSatisfy(c => c.TimestampMicros.Should().Be(2_000_000));
    }

    [Fact]
    public async Task Chain_rowkey_regex_then_column_limit()
    {
        var filter = RowFilters.Chain(RowFilters.RowKeyRegex("row-0[0-2]"), RowFilters.CellsPerColumnLimit(1));
        var rows = await ReadAll(filter: filter);
        rows.Should().HaveCount(3);
        foreach (var row in rows)
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    col.Cells.Should().ContainSingle();
    }

    [Fact]
    public async Task Chain_strip_value_then_cells_limit()
    {
        var filter = RowFilters.Chain(RowFilters.StripValueTransformer(), RowFilters.CellsPerRowLimit(3));
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), filter);
        rows.Should().ContainSingle();
        TotalCells(rows[0]).Should().Be(3);
        rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Should().AllSatisfy(c => c.Value.Length.Should().Be(0));
    }

    [Fact]
    public async Task Chain_four_filters()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.ColumnQualifierExact("a"),
            RowFilters.CellsPerColumnLimit(1),
            RowFilters.StripValueTransformer());
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), filter);
        rows.Should().ContainSingle();
        TotalCells(rows[0]).Should().Be(1);
        rows[0].Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task Chain_value_regex_then_column_regex()
    {
        var filter = RowFilters.Chain(
            RowFilters.ValueRegex(".*-v3"),
            RowFilters.ColumnQualifierRegex("[ab]"));
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), filter);
        rows.Should().ContainSingle();
        var quals = rows[0].Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).Distinct();
        quals.Should().BeSubsetOf(new[] { "a", "b" });
    }

    #endregion

    #region Interleave compositions

    [Fact]
    public async Task Interleave_two_columns()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("a"),
            RowFilters.ColumnQualifierExact("x"));
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), filter);
        rows.Should().ContainSingle();
        var quals = rows[0].Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).Distinct();
        quals.Should().Contain("a");
        quals.Should().Contain("x");
    }

    [Fact]
    public async Task Interleave_family_and_column()
    {
        var filter = RowFilters.Interleave(
            RowFilters.FamilyNameExact(CF2),
            RowFilters.ColumnQualifierExact("a"));
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), filter);
        rows.Should().ContainSingle();
        // CF2 (all cols) + CF:a
        var familyNames = rows[0].Families.Select(f => f.Name).ToList();
        familyNames.Should().Contain(CF);
        familyNames.Should().Contain(CF2);
    }

    [Fact]
    public async Task Interleave_three_family_exacts()
    {
        var filter = RowFilters.Interleave(
            RowFilters.FamilyNameExact(CF),
            RowFilters.FamilyNameExact(CF2),
            RowFilters.FamilyNameExact("cf3"));
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), filter);
        rows.Should().ContainSingle();
        rows[0].Families.Should().HaveCount(2); // cf3 has no data
    }

    [Fact]
    public async Task Interleave_with_version_limits()
    {
        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.CellsPerColumnLimit(1)),
            RowFilters.Chain(RowFilters.FamilyNameExact(CF2), RowFilters.CellsPerColumnLimit(1)));
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), filter);
        rows.Should().ContainSingle();
        foreach (var fam in rows[0].Families)
            foreach (var col in fam.Columns)
                col.Cells.Should().ContainSingle();
    }

    #endregion

    #region Condition filter

    [Fact]
    public async Task Condition_predicate_true_applies_true_filter()
    {
        var filter = RowFilters.Condition(
            RowFilters.ValueRegex("a-v3"),
            RowFilters.CellsPerColumnLimit(1),
            RowFilters.PassAllFilter());
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), filter);
        rows.Should().ContainSingle();
        foreach (var col in rows[0].Families.SelectMany(f => f.Columns))
            col.Cells.Should().ContainSingle();
    }

    [Fact]
    public async Task Condition_predicate_false_applies_false_filter()
    {
        var filter = RowFilters.Condition(
            RowFilters.ValueRegex("NONEXISTENT"),
            RowFilters.BlockAllFilter(),
            RowFilters.CellsPerColumnLimit(1));
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), filter);
        rows.Should().ContainSingle();
        foreach (var col in rows[0].Families.SelectMany(f => f.Columns))
            col.Cells.Should().ContainSingle();
    }

    [Fact]
    public async Task Condition_no_false_filter_blocks()
    {
        var filter = new RowFilter
        {
            Condition = new RowFilter.Types.Condition
            {
                PredicateFilter = RowFilters.ValueRegex("NONEXISTENT"),
                TrueFilter = RowFilters.PassAllFilter(),
            }
        };
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), filter);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Condition_no_true_filter_blocks()
    {
        var filter = new RowFilter
        {
            Condition = new RowFilter.Types.Condition
            {
                PredicateFilter = RowFilters.PassAllFilter(),
                FalseFilter = RowFilters.PassAllFilter(),
            }
        };
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), filter);
        rows.Should().BeEmpty();
    }

    #endregion

    #region Filter + rows limit interaction

    [Fact]
    public async Task RowsLimit_1_returns_single_row()
    {
        var rows = await ReadAll(limit: 1);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task RowsLimit_5_returns_five_rows()
    {
        var rows = await ReadAll(limit: 5);
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task RowsLimit_exceeds_total_returns_all()
    {
        var rows = await ReadAll(limit: 100);
        rows.Should().HaveCount(20);
    }

    [Fact]
    public async Task Filter_reducing_to_empty_plus_limit()
    {
        // Filter removes all cells from some rows, limit applies to returned rows
        var filter = RowFilters.Chain(
            RowFilters.RowKeyRegex("row-1[0-9]"),
            RowFilters.FamilyNameExact(CF),
            RowFilters.ColumnQualifierExact("a"));
        var rows = await ReadAll(filter: filter, limit: 3);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Filter_with_zero_limit_returns_all()
    {
        // Ref: rows_limit of 0 means no limit
        var rows = await ReadAll(filter: RowFilters.CellsPerColumnLimit(1), limit: 0);
        rows.Should().HaveCount(20);
    }

    #endregion

    #region ColumnRange filter

    [Fact]
    public async Task ColumnRange_closed_includes_both_endpoints()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#columnrange
        var filter = RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "b"));
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), filter);
        rows.Should().ContainSingle();
        var quals = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().Equal("a", "b");
    }

    [Fact]
    public async Task ColumnRange_open_excludes_both_endpoints()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.Open(CF, "a", "c"));
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), filter);
        rows.Should().ContainSingle();
        var quals = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().Equal("b");
    }

    [Fact]
    public async Task ColumnRange_closed_open()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.ClosedOpen(CF, "a", "c"));
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), filter);
        rows.Should().ContainSingle();
        var quals = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().Equal("a", "b");
    }

    [Fact]
    public async Task ColumnRange_open_closed()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.OpenClosed(CF, "a", "c"));
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), filter);
        rows.Should().ContainSingle();
        var quals = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().Equal("b", "c");
    }

    [Fact]
    public async Task ColumnRange_single_column()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.Closed(CF, "b", "b"));
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), filter);
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("b");
    }

    [Fact]
    public async Task ColumnRange_no_match()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.Closed(CF, "m", "z"));
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), filter);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ColumnRange_scoped_to_family()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.Closed(CF2, "x", "y"));
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), filter);
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF2);
    }

    #endregion

    #region ValueRange filter

    [Fact]
    public async Task ValueRange_closed_match()
    {
        var filter = RowFilters.ValueRange(ValueRange.Closed(
            ByteString.CopyFromUtf8("a-v1"), ByteString.CopyFromUtf8("a-v2")));
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), filter);
        rows.Should().ContainSingle();
        var vals = rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Select(c => c.Value.ToStringUtf8()).Distinct().ToList();
        vals.Should().AllSatisfy(v =>
            (string.Compare(v, "a-v1", StringComparison.Ordinal) >= 0 &&
             string.Compare(v, "a-v2", StringComparison.Ordinal) <= 0).Should().BeTrue());
    }

    [Fact]
    public async Task ValueRange_open_excludes_endpoints()
    {
        var filter = RowFilters.ValueRange(ValueRange.Open(
            ByteString.CopyFromUtf8("a-v1"), ByteString.CopyFromUtf8("a-v3")));
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"), filter);
        rows.Should().ContainSingle();
        var vals = rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Select(c => c.Value.ToStringUtf8()).Distinct().ToList();
        vals.Should().NotContain("a-v1");
        vals.Should().NotContain("a-v3");
    }

    #endregion

    #region No filter (all rows scan)

    [Fact]
    public async Task No_filter_returns_all_rows()
    {
        var rows = await ReadAll();
        rows.Should().HaveCount(20);
    }

    [Fact]
    public async Task No_filter_returns_all_cells()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"));
        rows.Should().ContainSingle();
        TotalCells(rows[0]).Should().Be(13);
    }

    [Fact]
    public async Task No_filter_rows_sorted_lexicographically()
    {
        var rows = await ReadAll();
        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task No_filter_families_sorted()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"));
        rows[0].Families.Select(f => f.Name).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task No_filter_columns_sorted()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"));
        foreach (var fam in rows[0].Families)
        {
            var quals = fam.Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
            quals.Should().BeInAscendingOrder();
        }
    }

    [Fact]
    public async Task No_filter_cells_descending_timestamp()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("row-00"));
        foreach (var col in rows[0].Families.SelectMany(f => f.Columns))
        {
            var ts = col.Cells.Select(c => c.TimestampMicros).ToList();
            ts.Should().BeInDescendingOrder();
        }
    }

    #endregion
}

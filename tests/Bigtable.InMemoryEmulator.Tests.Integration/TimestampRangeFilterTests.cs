using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for timestamp range filter using raw protobuf construction.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#timestamprange
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class TimestampRangeFilterTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "tsrf-test";
    private const string CF = "cf";

    public TimestampRangeFilterTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        // Write cells with specific timestamps (BigtableVersion stores millis * 1000 = micros)
        // v1000 = 1_000_000 micros, v2000 = 2_000_000 micros, etc.
        for (int v = 1; v <= 10; v++)
            await Client.MutateRowAsync(TN, "ts-row",
                Mutations.SetCell(CF, "c", $"v{v}", new BigtableVersion(v * 1000)));

        // Rows with single timestamp each
        await Client.MutateRowAsync(TN, "ts-1k",
            Mutations.SetCell(CF, "c", "at-1k", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ts-5k",
            Mutations.SetCell(CF, "c", "at-5k", new BigtableVersion(5000)));
        await Client.MutateRowAsync(TN, "ts-10k",
            Mutations.SetCell(CF, "c", "at-10k", new BigtableVersion(10000)));
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

    private int CellCount(List<Row> rows) =>
        rows.SelectMany(r => r.Families).SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();

    /// <summary>
    /// Creates a TimestampRange filter in micros.
    /// BigtableVersion(1000) = 1_000_000 micros.
    /// </summary>
    private RowFilter TsRangeFilter(long startMicros, long endMicros)
    {
        return new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                StartTimestampMicros = startMicros,
                EndTimestampMicros = endMicros
            }
        };
    }

    #region Basic range

    [Fact]
    public async Task TimestampRange_all_versions()
    {
        // Range covering all: [1_000_000, 11_000_000) should get all 10 versions
        var filter = TsRangeFilter(1_000_000, 11_000_000);
        var rows = await ReadAll(rows: RowSet.FromRowKeys("ts-row"), filter: filter);
        CellCount(rows).Should().Be(10);
    }

    [Fact]
    public async Task TimestampRange_single_version()
    {
        // [1_000_000, 2_000_000) should get exactly version at 1_000_000
        var filter = TsRangeFilter(1_000_000, 2_000_000);
        var rows = await ReadAll(rows: RowSet.FromRowKeys("ts-row"), filter: filter);
        CellCount(rows).Should().Be(1);
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v1");
    }

    [Fact]
    public async Task TimestampRange_first_half()
    {
        // [1_000_000, 6_000_000) should get versions 1-5
        var filter = TsRangeFilter(1_000_000, 6_000_000);
        var rows = await ReadAll(rows: RowSet.FromRowKeys("ts-row"), filter: filter);
        CellCount(rows).Should().Be(5);
    }

    [Fact]
    public async Task TimestampRange_second_half()
    {
        // [6_000_000, 11_000_000) should get versions 6-10
        var filter = TsRangeFilter(6_000_000, 11_000_000);
        var rows = await ReadAll(rows: RowSet.FromRowKeys("ts-row"), filter: filter);
        CellCount(rows).Should().Be(5);
    }

    [Fact]
    public async Task TimestampRange_no_match()
    {
        // [20_000_000, 30_000_000) - no versions in this range
        var filter = TsRangeFilter(20_000_000, 30_000_000);
        var rows = await ReadAll(rows: RowSet.FromRowKeys("ts-row"), filter: filter);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task TimestampRange_exact_boundary_exclusive_end()
    {
        // End is exclusive: [5_000_000, 6_000_000) should get version 5 only
        var filter = TsRangeFilter(5_000_000, 6_000_000);
        var rows = await ReadAll(rows: RowSet.FromRowKeys("ts-row"), filter: filter);
        CellCount(rows).Should().Be(1);
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v5");
    }

    #endregion

    #region Start-only and end-only

    [Fact]
    public async Task TimestampRange_start_only()
    {
        // From version 8 onwards
        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                StartTimestampMicros = 8_000_000
            }
        };
        var rows = await ReadAll(rows: RowSet.FromRowKeys("ts-row"), filter: filter);
        CellCount(rows).Should().Be(3); // versions 8, 9, 10
    }

    [Fact]
    public async Task TimestampRange_end_only()
    {
        // Up to version 3 (exclusive)
        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                EndTimestampMicros = 3_000_000
            }
        };
        var rows = await ReadAll(rows: RowSet.FromRowKeys("ts-row"), filter: filter);
        CellCount(rows).Should().Be(2); // versions 1, 2
    }

    #endregion

    #region Combined with other filters

    [Fact]
    public async Task TimestampRange_with_cells_per_column_limit()
    {
        var filter = RowFilters.Chain(
            TsRangeFilter(1_000_000, 11_000_000),
            RowFilters.CellsPerColumnLimit(3));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("ts-row"), filter: filter);
        CellCount(rows).Should().Be(3);
    }

    [Fact]
    public async Task TimestampRange_with_value_filter()
    {
        var filter = RowFilters.Chain(
            TsRangeFilter(1_000_000, 6_000_000),
            RowFilters.ValueRegex("v[1-3]"));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("ts-row"), filter: filter);
        CellCount(rows).Should().Be(3);
    }

    [Fact]
    public async Task TimestampRange_with_strip_value()
    {
        var filter = RowFilters.Chain(
            TsRangeFilter(1_000_000, 4_000_000),
            RowFilters.StripValueTransformer());
        var rows = await ReadAll(rows: RowSet.FromRowKeys("ts-row"), filter: filter);
        CellCount(rows).Should().Be(3);
        foreach (var fam in rows[0].Families)
            foreach (var col in fam.Columns)
                foreach (var cell in col.Cells)
                    cell.Value.Length.Should().Be(0);
    }

    #endregion

    #region Across multiple rows

    [Fact]
    public async Task TimestampRange_across_rows()
    {
        // ts-1k has 1_000_000, ts-5k has 5_000_000, ts-10k has 10_000_000
        var filter = TsRangeFilter(1_000_000, 6_000_000);
        var rows = await ReadAll(filter: filter);
        // Should include ts-1k and ts-5k, plus ts-row versions 1-5
        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().Contain("ts-1k");
        keys.Should().Contain("ts-5k");
        keys.Should().NotContain("ts-10k");
    }

    [Fact]
    public async Task TimestampRange_excludes_rows_with_no_matching_cells()
    {
        var filter = TsRangeFilter(100_000_000, 200_000_000);
        var rows = await ReadAll(filter: filter);
        rows.Should().BeEmpty();
    }

    #endregion

    #region Interleave with timestamp range

    [Fact]
    public async Task Interleave_timestamp_ranges()
    {
        var filter = RowFilters.Interleave(
            TsRangeFilter(1_000_000, 3_000_000),   // versions 1, 2
            TsRangeFilter(8_000_000, 11_000_000));  // versions 8, 9, 10
        var rows = await ReadAll(rows: RowSet.FromRowKeys("ts-row"), filter: filter);
        CellCount(rows).Should().Be(5);
    }

    #endregion

    #region Condition with timestamp range

    [Fact]
    public async Task Condition_with_timestamp_range_predicate()
    {
        // If row has cells in recent range, strip values; otherwise pass all
        var filter = RowFilters.Condition(
            predicateFilter: TsRangeFilter(10_000_000, 11_000_000),
            trueFilter: RowFilters.StripValueTransformer(),
            falseFilter: RowFilters.PassAllFilter());

        // ts-row has version 10 in range → stripped
        var tsRow = await ReadAll(rows: RowSet.FromRowKeys("ts-row"), filter: filter);
        tsRow[0].Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);

        // ts-1k has only 1_000_000 → not in range → preserved
        var ts1k = await ReadAll(rows: RowSet.FromRowKeys("ts-1k"), filter: filter);
        ts1k[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("at-1k");
    }

    #endregion
}

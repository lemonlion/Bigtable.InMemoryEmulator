using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for timestamp edge cases including microsecond precision, extremes, and ordering.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#cell
///   "Timestamps are in microseconds, with granularity of milliseconds."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class TimestampEdgeCaseTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "ts-edge";
    private const string CF = "cf";

    public TimestampEdgeCaseTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
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

    #region Version ordering

    [Fact]
    public async Task Cells_returned_in_descending_timestamp_order()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsresponse
        //   "Cells are returned in descending timestamp order."
        await Client.MutateRowAsync(TN, "tse-desc",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));
        var rows = await ReadAll(RowSet.FromRowKeys("tse-desc"));
        var ts = rows[0].Families[0].Columns[0].Cells.Select(c => c.TimestampMicros).ToList();
        ts.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Version_1000_and_1001_are_distinct()
    {
        await Client.MutateRowAsync(TN, "tse-close",
            Mutations.SetCell(CF, "c", "a", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "b", new BigtableVersion(1001)));
        var rows = await ReadAll(RowSet.FromRowKeys("tse-close"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task Same_version_overwrites()
    {
        await Client.MutateRowAsync(TN, "tse-over",
            Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "tse-over",
            Mutations.SetCell(CF, "c", "second", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("tse-over"));
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task Ten_versions_returned_in_order()
    {
        var mutations = new List<Mutation>();
        for (int v = 1; v <= 10; v++)
            mutations.Add(Mutations.SetCell(CF, "c", $"v{v}", new BigtableVersion(v * 1000)));
        await Client.MutateRowAsync(TN, "tse-10v", mutations.ToArray());
        var rows = await ReadAll(RowSet.FromRowKeys("tse-10v"));
        var cells = rows[0].Families[0].Columns[0].Cells;
        cells.Should().HaveCount(10);
        cells.Select(c => c.TimestampMicros).Should().BeInDescendingOrder();
    }

    #endregion

    #region CellsPerColumnLimit interactions

    [Fact]
    public async Task CellsPerColumnLimit_1_returns_latest()
    {
        await Client.MutateRowAsync(TN, "tse-cpc1",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)));
        var rows = await ReadAll(RowSet.FromRowKeys("tse-cpc1"), RowFilters.CellsPerColumnLimit(1));
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task CellsPerColumnLimit_2_returns_two_latest()
    {
        for (int v = 1; v <= 5; v++)
            await Client.MutateRowAsync(TN, "tse-cpc2",
                Mutations.SetCell(CF, "c", $"v{v}", new BigtableVersion(v * 1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("tse-cpc2"), RowFilters.CellsPerColumnLimit(2));
        var cells = rows[0].Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2);
        cells[0].Value.ToStringUtf8().Should().Be("v5");
        cells[1].Value.ToStringUtf8().Should().Be("v4");
    }

    [Fact]
    public async Task CellsPerColumnLimit_greater_than_count_returns_all()
    {
        await Client.MutateRowAsync(TN, "tse-cpc3",
            Mutations.SetCell(CF, "c", "a", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "b", new BigtableVersion(2000)));
        var rows = await ReadAll(RowSet.FromRowKeys("tse-cpc3"), RowFilters.CellsPerColumnLimit(10));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    #endregion

    #region TimestampRange filter

    [Fact]
    public async Task TimestampRange_filters_versions()
    {
        for (int v = 1; v <= 5; v++)
            await Client.MutateRowAsync(TN, "tse-tr1",
                Mutations.SetCell(CF, "c", $"v{v}", new BigtableVersion(v * 1000)));
        // Range [2000000, 4000000) microseconds  — versions 2000 and 3000
        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                StartTimestampMicros = 2_000_000,
                EndTimestampMicros = 4_000_000
            }
        };
        var rows = await ReadAll(RowSet.FromRowKeys("tse-tr1"), filter);
        var cells = rows[0].Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2);
        cells.All(c => c.TimestampMicros >= 2000_000 && c.TimestampMicros < 4000_000).Should().BeTrue();
    }

    [Fact]
    public async Task TimestampRange_no_match_returns_empty()
    {
        await Client.MutateRowAsync(TN, "tse-tr2",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                StartTimestampMicros = 5_000_000,
                EndTimestampMicros = 6_000_000
            }
        };
        var rows = await ReadAll(RowSet.FromRowKeys("tse-tr2"), filter);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task TimestampRange_inclusive_start_exclusive_end()
    {
        await Client.MutateRowAsync(TN, "tse-tr3",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        // Range [2000000, 3000000) should only get version 2000
        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                StartTimestampMicros = 2_000_000,
                EndTimestampMicros = 3_000_000
            }
        };
        var rows = await ReadAll(RowSet.FromRowKeys("tse-tr3"), filter);
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.TimestampMicros.Should().Be(2000_000);
    }

    #endregion

    #region Large version numbers

    [Fact]
    public async Task Large_version_number()
    {
        await Client.MutateRowAsync(TN, "tse-large",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1_000_000)));
        var rows = await ReadAll(RowSet.FromRowKeys("tse-large"));
        rows[0].Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(1_000_000_000);
    }

    [Fact]
    public async Task Versions_spread_across_large_range()
    {
        await Client.MutateRowAsync(TN, "tse-spread",
            Mutations.SetCell(CF, "c", "a", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "b", new BigtableVersion(500_000)),
            Mutations.SetCell(CF, "c", "c", new BigtableVersion(1_000_000)));
        var rows = await ReadAll(RowSet.FromRowKeys("tse-spread"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(3);
        rows[0].Families[0].Columns[0].Cells[0].TimestampMicros.Should()
            .BeGreaterThan(rows[0].Families[0].Columns[0].Cells[1].TimestampMicros);
    }

    #endregion

    #region Version interactions with multiple columns

    [Fact]
    public async Task Different_columns_have_independent_versions()
    {
        await Client.MutateRowAsync(TN, "tse-indep",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "b", "v1", new BigtableVersion(3000)));
        var rows = await ReadAll(RowSet.FromRowKeys("tse-indep"));
        var cols = rows[0].Families[0].Columns.OrderBy(c => c.Qualifier.ToStringUtf8()).ToList();
        cols[0].Cells.Should().HaveCount(2); // a has 2 versions
        cols[1].Cells.Should().HaveCount(1); // b has 1 version
    }

    [Fact]
    public async Task Same_timestamp_different_columns_are_separate()
    {
        await Client.MutateRowAsync(TN, "tse-samets",
            Mutations.SetCell(CF, "a", "av", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "bv", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("tse-samets"));
        rows[0].Families[0].Columns.Should().HaveCount(2);
    }

    [Fact]
    public async Task Version_overwrite_doesnt_affect_other_columns()
    {
        await Client.MutateRowAsync(TN, "tse-owother",
            Mutations.SetCell(CF, "a", "a-first", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "b-first", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "tse-owother",
            Mutations.SetCell(CF, "a", "a-second", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("tse-owother"));
        var cols = rows[0].Families[0].Columns.OrderBy(c => c.Qualifier.ToStringUtf8()).ToList();
        cols[0].Cells[0].Value.ToStringUtf8().Should().Be("a-second"); // a overwritten
        cols[1].Cells[0].Value.ToStringUtf8().Should().Be("b-first"); // b unchanged
    }

    #endregion

    #region CellsPerRowLimit

    [Fact]
    public async Task CellsPerRowLimit_across_columns()
    {
        await Client.MutateRowAsync(TN, "tse-rpl",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "d", "4", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "e", "5", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("tse-rpl"), RowFilters.CellsPerRowLimit(3));
        var totalCells = rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();
        totalCells.Should().Be(3);
    }

    [Fact]
    public async Task CellsPerRowLimit_counts_versions()
    {
        await Client.MutateRowAsync(TN, "tse-rpl2",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "a", "v3", new BigtableVersion(3000)),
            Mutations.SetCell(CF, "b", "bv", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("tse-rpl2"), RowFilters.CellsPerRowLimit(2));
        var totalCells = rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();
        totalCells.Should().Be(2);
    }

    #endregion

    #region CellsPerRowOffset

    [Fact]
    public async Task CellsPerRowOffset_skips_first_n_cells()
    {
        await Client.MutateRowAsync(TN, "tse-off",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "d", "4", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("tse-off"), RowFilters.CellsPerRowOffset(2));
        var totalCells = rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();
        totalCells.Should().Be(2);
    }

    [Fact]
    public async Task CellsPerRowOffset_greater_than_total_returns_empty()
    {
        await Client.MutateRowAsync(TN, "tse-off2",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("tse-off2"), RowFilters.CellsPerRowOffset(10));
        rows.Should().BeEmpty();
    }

    #endregion

    #region StripValueTransformer

    [Fact]
    public async Task StripValue_returns_empty_value()
    {
        await Client.MutateRowAsync(TN, "tse-strip",
            Mutations.SetCell(CF, "c", "hello_world", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("tse-strip"), RowFilters.StripValueTransformer());
        rows[0].Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task StripValue_preserves_structure()
    {
        await Client.MutateRowAsync(TN, "tse-strip2",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("tse-strip2"), RowFilters.StripValueTransformer());
        rows[0].Families[0].Columns.Should().HaveCount(2);
        rows[0].Families[0].Columns.SelectMany(c => c.Cells).All(c => c.Value.Length == 0).Should().BeTrue();
    }

    #endregion
}

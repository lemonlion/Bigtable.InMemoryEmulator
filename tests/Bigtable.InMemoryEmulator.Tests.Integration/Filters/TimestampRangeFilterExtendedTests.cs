using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for timestamp range filter using raw protobuf construction.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#timestamprange
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class TimestampRangeFilterExtendedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "trfe-tests";
    private const string CF = "cf";

    public TimestampRangeFilterExtendedTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "trfe-seed",
                Mutations.SetCell(CF, "col", $"v{i}", new BigtableVersion(i * 1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private RowFilter TsRange(long startMicros, long endMicros)
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

    private RowFilter TsRangeMs(long startMs, long endMs) => TsRange(startMs * 1000, endMs * 1000);

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null)
    {
        var list = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rows, filter: filter))
            list.Add(r);
        return list;
    }

    [Fact]
    public async Task TimestampRange_includes_start_excludes_end()
    {
        // [2000ms, 4000ms) should get v2 and v3
        var row = await Client.ReadRowAsync(TN, "trfe-seed", TsRangeMs(2000, 4000));
        row.Should().NotBeNull();
        var values = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Select(c => c.Value.ToStringUtf8()).ToList();
        values.Should().HaveCount(2);
        values.Should().Contain("v2");
        values.Should().Contain("v3");
    }

    [Fact]
    public async Task TimestampRange_single_version()
    {
        var row = await Client.ReadRowAsync(TN, "trfe-seed", TsRangeMs(3000, 4000));
        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().ContainSingle();
        cells[0].Value.ToStringUtf8().Should().Be("v3");
    }

    [Fact]
    public async Task TimestampRange_all_versions()
    {
        var row = await Client.ReadRowAsync(TN, "trfe-seed", TsRangeMs(1000, 6000));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(5);
    }

    [Fact]
    public async Task TimestampRange_no_matches()
    {
        var row = await Client.ReadRowAsync(TN, "trfe-seed", TsRangeMs(6000, 7000));
        row.Should().BeNull();
    }

    [Fact]
    public async Task TimestampRange_start_only()
    {
        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange { StartTimestampMicros = 4000 * 1000 }
        };
        var row = await Client.ReadRowAsync(TN, "trfe-seed", filter);
        row.Should().NotBeNull();
        var values = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Select(c => c.Value.ToStringUtf8()).ToList();
        values.Should().HaveCount(2);
        values.Should().Contain("v4");
        values.Should().Contain("v5");
    }

    [Fact]
    public async Task TimestampRange_end_only()
    {
        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange { EndTimestampMicros = 3000 * 1000 }
        };
        var row = await Client.ReadRowAsync(TN, "trfe-seed", filter);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(2);
    }

    [Fact]
    public async Task TimestampRange_exact_boundary()
    {
        var row = await Client.ReadRowAsync(TN, "trfe-seed", TsRangeMs(3000, 3001));
        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().ContainSingle();
        cells[0].Value.ToStringUtf8().Should().Be("v3");
    }

    [Fact]
    public async Task TimestampRange_chained_with_column_filter()
    {
        var rk = "trfe-chain-col";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "b", "v2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a", "v3", new BigtableVersion(2000)));

        var filter = RowFilters.Chain(RowFilters.ColumnQualifierExact("a"), TsRangeMs(2000, 3000));
        var row = await Client.ReadRowAsync(TN, rk, filter);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().ContainSingle();
    }

    [Fact]
    public async Task TimestampRange_across_multiple_columns()
    {
        var rk = "trfe-multi-col";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "x", "old-x", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "y", "old-y", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "x", "new-x", new BigtableVersion(5000)),
            Mutations.SetCell(CF, "y", "new-y", new BigtableVersion(5000)));

        var row = await Client.ReadRowAsync(TN, rk, TsRangeMs(2000, 6000));
        row.Should().NotBeNull();
        var values = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Select(c => c.Value.ToStringUtf8()).ToList();
        values.Should().HaveCount(2);
        values.Should().Contain("new-x");
        values.Should().Contain("new-y");
    }

    [Fact]
    public async Task TimestampRange_across_multiple_rows()
    {
        for (int r = 0; r < 3; r++)
            await Client.MutateRowAsync(TN, $"trfe-mr-{r}",
                Mutations.SetCell(CF, "col", "old", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "col", "new", new BigtableVersion(5000)));

        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("trfe-mr-", "trfe-ms-")),
            TsRangeMs(4000, 6000));

        rows.Should().HaveCount(3);
        foreach (var row in rows)
            row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Single()
                .Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task TimestampRange_in_interleave()
    {
        var rk = "trfe-inter";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col", "v3", new BigtableVersion(3000)),
            Mutations.SetCell(CF, "col", "v5", new BigtableVersion(5000)));

        var filter = RowFilters.Interleave(TsRangeMs(1000, 2000), TsRangeMs(5000, 6000));
        var row = await Client.ReadRowAsync(TN, rk, filter);
        row.Should().NotBeNull();
        var values = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Select(c => c.Value.ToStringUtf8()).ToList();
        values.Should().HaveCount(2);
        values.Should().Contain("v1");
        values.Should().Contain("v5");
    }

    [Fact]
    public async Task TimestampRange_with_label()
    {
        var filter = RowFilters.Chain(TsRangeMs(3000, 4000), new RowFilter { ApplyLabelTransformer = "ts-3" });
        var row = await Client.ReadRowAsync(TN, "trfe-seed", filter);
        row.Should().NotBeNull();
        var cell = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Single();
        cell.Value.ToStringUtf8().Should().Be("v3");
        cell.Labels.Should().Contain("ts-3");
    }

    [Fact]
    public async Task Server_assigned_timestamps_are_positive()
    {
        var rk = "trfe-server-ts";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "now"));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Single()
            .TimestampMicros.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task TimestampRange_empty_range_no_results()
    {
        var row = await Client.ReadRowAsync(TN, "trfe-seed", TsRangeMs(3000, 3000));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Timestamp_ordering_newest_first()
    {
        var row = await Client.ReadRowAsync(TN, "trfe-seed");
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Select(c => c.TimestampMicros).Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task TimestampRange_with_strip_filter()
    {
        var filter = RowFilters.Chain(TsRangeMs(2000, 4000), RowFilters.StripValueTransformer());
        var row = await Client.ReadRowAsync(TN, "trfe-seed", filter);
        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(2);
        cells.Should().AllSatisfy(c => c.Value.Should().BeEmpty());
    }

    [Fact]
    public async Task TimestampRange_combined_with_cells_per_column()
    {
        var rk = "trfe-ts-cpc";
        for (int i = 1; i <= 10; i++)
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, "col", $"v{i}", new BigtableVersion(i * 1000)));

        var filter = RowFilters.Chain(TsRangeMs(3000, 8000), RowFilters.CellsPerColumnLimit(2));
        var row = await Client.ReadRowAsync(TN, rk, filter);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(2);
    }
}

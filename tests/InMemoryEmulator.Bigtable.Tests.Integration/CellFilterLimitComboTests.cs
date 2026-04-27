using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for CellsPerRow, CellsPerColumn, CellsPerRowOffset combined with other filters.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CellFilterLimitComboTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "cflc-tests";
    private const string CF = "cf";

    public CellFilterLimitComboTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private async Task<int> CountCells(string key, RowFilter filter)
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = filter,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8(key) } }
        };
        int count = 0;
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
                count += c.Cells.Count;
        return count;
    }

    private async Task<List<string>> ReadValues(string key, RowFilter filter)
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = filter,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8(key) } }
        };
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                vals.Add(cell.Value.ToStringUtf8());
        return vals;
    }

    [Fact]
    public async Task CellsPerRowLimit_1_returns_first_cell_only()
    {
        await Client.MutateRowAsync(TN, "cflc-cprl1",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(1000)));

        (await CountCells("cflc-cprl1", RowFilters.CellsPerRowLimit(1))).Should().Be(1);
    }

    [Fact]
    public async Task CellsPerRowLimit_exceeds_total_returns_all()
    {
        await Client.MutateRowAsync(TN, "cflc-cprl-exc",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(1000)));

        (await CountCells("cflc-cprl-exc", RowFilters.CellsPerRowLimit(100))).Should().Be(2);
    }

    [Fact]
    public async Task CellsPerColumnLimit_1_returns_latest_version()
    {
        await Client.MutateRowAsync(TN, "cflc-cpcl1",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "cflc-cpcl1",
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)));

        var vals = await ReadValues("cflc-cpcl1", RowFilters.CellsPerColumnLimit(1));
        vals.Should().HaveCount(1);
        vals[0].Should().Be("new");
    }

    [Fact]
    public async Task CellsPerColumnLimit_with_multiple_columns()
    {
        await Client.MutateRowAsync(TN, "cflc-cpcl-mc",
            Mutations.SetCell(CF, "a", "a1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "cflc-cpcl-mc",
            Mutations.SetCell(CF, "a", "a2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, "cflc-cpcl-mc",
            Mutations.SetCell(CF, "b", "b1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "cflc-cpcl-mc",
            Mutations.SetCell(CF, "b", "b2", new BigtableVersion(2000)));

        // Limit 1 per column — should get a2 and b2 (latest each)
        (await CountCells("cflc-cpcl-mc", RowFilters.CellsPerColumnLimit(1))).Should().Be(2);
    }

    [Fact]
    public async Task CellsPerRowOffset_skips_first_n()
    {
        await Client.MutateRowAsync(TN, "cflc-cpro",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(1000)));

        (await CountCells("cflc-cpro", RowFilters.CellsPerRowOffset(1))).Should().Be(2);
    }

    [Fact]
    public async Task CellsPerRowOffset_exceeds_count_returns_empty()
    {
        await Client.MutateRowAsync(TN, "cflc-cpro-exc",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)));

        (await CountCells("cflc-cpro-exc", RowFilters.CellsPerRowOffset(10))).Should().Be(0);
    }

    [Fact]
    public async Task CellsPerRowLimit_chained_with_family_filter()
    {
        await Client.MutateRowAsync(TN, "cflc-chain-fam",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "v3", new BigtableVersion(1000)));

        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.CellsPerRowLimit(1));
        (await CountCells("cflc-chain-fam", filter)).Should().Be(1);
    }

    [Fact]
    public async Task CellsPerRowLimit_chained_with_value_range()
    {
        await Client.MutateRowAsync(TN, "cflc-chain-vr",
            Mutations.SetCell(CF, "a", "abc", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "def", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "ghi", new BigtableVersion(1000)));

        var filter = RowFilters.Chain(
            RowFilters.ValueRange(ValueRange.Closed("a", "z")),
            RowFilters.CellsPerRowLimit(2));
        (await CountCells("cflc-chain-vr", filter)).Should().Be(2);
    }

    [Fact]
    public async Task CellsPerColumnLimit_chained_with_column_range()
    {
        for (int i = 1; i <= 3; i++)
            await Client.MutateRowAsync(TN, "cflc-cpcl-cr",
                Mutations.SetCell(CF, "a", $"v{i}", new BigtableVersion(i * 1000)));

        var filter = RowFilters.Chain(
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "z")),
            RowFilters.CellsPerColumnLimit(2));
        (await CountCells("cflc-cpcl-cr", filter)).Should().Be(2);
    }

    [Fact]
    public async Task CellsPerRowOffset_chained_with_cells_per_row_limit()
    {
        await Client.MutateRowAsync(TN, "cflc-offset-limit",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "d", "v4", new BigtableVersion(1000)));

        // Skip 1, then take 2
        var filter = RowFilters.Chain(
            RowFilters.CellsPerRowOffset(1),
            RowFilters.CellsPerRowLimit(2));
        (await CountCells("cflc-offset-limit", filter)).Should().Be(2);
    }

    [Fact]
    public async Task CellsPerRowLimit_in_interleave_applied_per_branch()
    {
        await Client.MutateRowAsync(TN, "cflc-ilv-limit",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(1000)));

        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("a"), RowFilters.CellsPerRowLimit(1)),
            RowFilters.Chain(RowFilters.ColumnQualifierExact("c"), RowFilters.CellsPerRowLimit(1)));
        (await CountCells("cflc-ilv-limit", filter)).Should().Be(2);
    }

    [Fact]
    public async Task CellsPerColumnLimit_2_with_3_versions()
    {
        for (int i = 1; i <= 3; i++)
            await Client.MutateRowAsync(TN, "cflc-cpcl2",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));

        var vals = await ReadValues("cflc-cpcl2", RowFilters.CellsPerColumnLimit(2));
        vals.Should().HaveCount(2);
        vals.Should().Contain("v3");
        vals.Should().Contain("v2");
    }

    [Fact]
    public async Task CellsPerRowLimit_with_timestamp_filter()
    {
        var ts1 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var ts2 = new DateTime(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        await Client.MutateRowAsync(TN, "cflc-ts-limit",
            Mutations.SetCell(CF, "a", "early", new BigtableVersion(ts1)),
            Mutations.SetCell(CF, "b", "late", new BigtableVersion(ts2)));

        var filter = RowFilters.Chain(
            RowFilters.TimestampRange(null, ts2),
            RowFilters.CellsPerRowLimit(1));
        (await CountCells("cflc-ts-limit", filter)).Should().Be(1);
    }

    [Fact]
    public async Task CellsPerRowLimit_with_strip_value()
    {
        await Client.MutateRowAsync(TN, "cflc-strip",
            Mutations.SetCell(CF, "a", "data", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "data", new BigtableVersion(1000)));

        var filter = RowFilters.Chain(
            RowFilters.CellsPerRowLimit(1),
            RowFilters.StripValueTransformer());
        var vals = await ReadValues("cflc-strip", filter);
        vals.Should().HaveCount(1);
        vals[0].Should().BeEmpty();
    }

    [Fact]
    public async Task CellsPerRowLimit_with_label()
    {
        await Client.MutateRowAsync(TN, "cflc-label",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v", new BigtableVersion(1000)));

        var filter = RowFilters.Chain(
            RowFilters.CellsPerRowLimit(1),
            new RowFilter { ApplyLabelTransformer = "limited" });

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = filter,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("cflc-label") } }
        };
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                cell.Labels.Should().Contain("limited");
    }

    [Fact]
    public async Task CellsPerRowOffset_with_family_filter()
    {
        await Client.MutateRowAsync(TN, "cflc-off-fam",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "v3", new BigtableVersion(1000)));

        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.CellsPerRowOffset(1));
        (await CountCells("cflc-off-fam", filter)).Should().Be(1);
    }

    [Fact]
    public async Task CellsPerColumnLimit_across_families()
    {
        await Client.MutateRowAsync(TN, "cflc-cpcl-xfam",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "cflc-cpcl-xfam",
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, "cflc-cpcl-xfam",
            Mutations.SetCell("cf2", "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "cflc-cpcl-xfam",
            Mutations.SetCell("cf2", "c", "v2", new BigtableVersion(2000)));

        // 1 per column, 2 columns (one in each family)
        (await CountCells("cflc-cpcl-xfam", RowFilters.CellsPerColumnLimit(1))).Should().Be(2);
    }

    [Fact]
    public async Task CellsPerRowLimit_in_condition_true_branch()
    {
        await Client.MutateRowAsync(TN, "cflc-cond",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(1000)));

        var filter = RowFilters.Condition(
            RowFilters.ColumnQualifierExact("a"),
            RowFilters.CellsPerRowLimit(1),
            RowFilters.PassAllFilter());
        // "a" exists → true branch → limit to 1 cell
        (await CountCells("cflc-cond", filter)).Should().Be(1);
    }

    [Fact]
    public async Task CellsPerRowOffset_0_returns_all()
    {
        await Client.MutateRowAsync(TN, "cflc-off0",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(1000)));

        (await CountCells("cflc-off0", RowFilters.CellsPerRowOffset(0))).Should().Be(2);
    }

    [Fact]
    public async Task CellsPerColumnLimit_with_row_key_regex()
    {
        await Client.MutateRowAsync(TN, "cflc-rk-match",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "cflc-rk-match",
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));

        var filter = RowFilters.Chain(
            RowFilters.RowKeyRegex("cflc-rk-match"),
            RowFilters.CellsPerColumnLimit(1));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = filter,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("cflc-rk-match") } }
        };
        int count = 0;
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
                count += c.Cells.Count;
        count.Should().Be(1);
    }

    [Fact]
    public async Task CellsPerRowLimit_with_multiple_families()
    {
        await Client.MutateRowAsync(TN, "cflc-mf",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "a", "v2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v3", new BigtableVersion(1000)));

        // Limit to 2 cells total across all families
        (await CountCells("cflc-mf", RowFilters.CellsPerRowLimit(2))).Should().Be(2);
    }

    [Fact]
    public async Task CellsPerColumnLimit_with_value_regex()
    {
        await Client.MutateRowAsync(TN, "cflc-cpcl-vreg",
            Mutations.SetCell(CF, "c", "match-1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "cflc-cpcl-vreg",
            Mutations.SetCell(CF, "c", "match-2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, "cflc-cpcl-vreg",
            Mutations.SetCell(CF, "c", "other", new BigtableVersion(3000)));

        var filter = RowFilters.Chain(
            RowFilters.CellsPerColumnLimit(2),
            RowFilters.ValueRegex("match-.*"));
        var vals = await ReadValues("cflc-cpcl-vreg", filter);
        vals.Should().HaveCount(1);
        vals[0].Should().Be("match-2");
    }
}

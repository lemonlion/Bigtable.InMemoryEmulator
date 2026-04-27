using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for filter interactions with multi-version data — how different filters
/// behave when cells have multiple versions with different values.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class FilterMultiVersionTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "fmv-tests";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public FilterMultiVersionTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });

        // Create a row with 5 versions in column "c"
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "fmv-row",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));

        // Create a row with different values for different columns
        await Client.MutateRowAsync(TN, "fmv-multicol",
            Mutations.SetCell(CF, "alpha", "aaa", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "alpha", "bbb", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "beta", "xxx", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "beta", "yyy", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "beta", "zzz", new BigtableVersion(3000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task No_filter_returns_all_versions()
    {
        var row = await Client.ReadRowAsync(TN, "fmv-row");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(5);
    }

    [Fact]
    public async Task CellsPerColumnLimit_1_returns_latest()
    {
        var request = MakeRequest("fmv-row", RowFilters.CellsPerColumnLimit(1));
        var vals = await CollectValues(request);
        vals.Should().ContainSingle("v5");
    }

    [Fact]
    public async Task CellsPerColumnLimit_3_returns_latest_3()
    {
        var request = MakeRequest("fmv-row", RowFilters.CellsPerColumnLimit(3));
        var vals = await CollectValues(request);
        vals.Should().HaveCount(3);
        vals.Should().Contain("v5");
        vals.Should().Contain("v4");
        vals.Should().Contain("v3");
    }

    [Fact]
    public async Task CellsPerColumnLimit_exceeds_versions()
    {
        var request = MakeRequest("fmv-row", RowFilters.CellsPerColumnLimit(100));
        var vals = await CollectValues(request);
        vals.Should().HaveCount(5);
    }

    [Fact]
    public async Task ValueExact_across_versions()
    {
        var request = MakeRequest("fmv-row", RowFilters.ValueExact("v3"));
        var vals = await CollectValues(request);
        vals.Should().ContainSingle("v3");
    }

    [Fact]
    public async Task ValueRegex_matches_multiple_versions()
    {
        var request = MakeRequest("fmv-row", RowFilters.ValueRegex("v[45]"));
        var vals = await CollectValues(request);
        vals.Should().HaveCount(2);
        vals.Should().Contain("v4");
        vals.Should().Contain("v5");
    }

    [Fact]
    public async Task ValueRange_across_versions()
    {
        var request = MakeRequest("fmv-row", RowFilters.ValueRange(ValueRange.Closed("v2", "v4")));
        var vals = await CollectValues(request);
        vals.Should().HaveCount(3);
        vals.Should().Contain("v2");
        vals.Should().Contain("v3");
        vals.Should().Contain("v4");
    }

    [Fact]
    public async Task TimestampRange_selects_specific_versions()
    {
        // Timestamps are stored in microseconds: version 1000ms = 1s
        var start = new DateTime(1970, 1, 1, 0, 0, 2, DateTimeKind.Utc);  // 2000ms
        var end = new DateTime(1970, 1, 1, 0, 0, 4, DateTimeKind.Utc);    // 4000ms

        var request = MakeRequest("fmv-row", RowFilters.TimestampRange(start, end));
        var vals = await CollectValues(request);
        vals.Should().HaveCount(2);
        vals.Should().Contain("v2");
        vals.Should().Contain("v3");
    }

    [Fact]
    public async Task StripValue_on_multi_version()
    {
        var request = MakeRequest("fmv-row", RowFilters.StripValueTransformer());
        var cells = new List<(long ts, int len)>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                cells.Add((cell.TimestampMicros, cell.Value.Length));

        cells.Should().HaveCount(5);
        cells.Should().AllSatisfy(c => c.len.Should().Be(0));
    }

    [Fact]
    public async Task Label_on_multi_version()
    {
        var request = MakeRequest("fmv-row", new RowFilter { ApplyLabelTransformer = "ver-label" });
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                cell.Labels.Should().Contain("ver-label");
    }

    [Fact]
    public async Task Chain_limit_then_value_filter()
    {
        var request = MakeRequest("fmv-row",
            RowFilters.Chain(
                RowFilters.CellsPerColumnLimit(3),
                RowFilters.ValueExact("v4")));
        var vals = await CollectValues(request);
        vals.Should().ContainSingle("v4");
    }

    [Fact]
    public async Task Chain_value_filter_then_limit()
    {
        var request = MakeRequest("fmv-row",
            RowFilters.Chain(
                RowFilters.ValueRegex("v[2-5]"),
                RowFilters.CellsPerColumnLimit(2)));
        var vals = await CollectValues(request);
        vals.Should().HaveCount(2);
        vals.Should().Contain("v5");
        vals.Should().Contain("v4");
    }

    [Fact]
    public async Task CellsPerRowLimit_with_multicol_multi_version()
    {
        var request = MakeRequest("fmv-multicol", RowFilters.CellsPerRowLimit(3));
        var cellCount = 0;
        await foreach (var row in Client.ReadRows(request))
            cellCount += row.Families.Sum(f => f.Columns.Sum(c => c.Cells.Count));

        cellCount.Should().Be(3);
    }

    [Fact]
    public async Task CellsPerRowOffset_with_multicol()
    {
        // Total cells: alpha=2 + beta=3 = 5
        var request = MakeRequest("fmv-multicol", RowFilters.CellsPerRowOffset(3));
        var cellCount = 0;
        await foreach (var row in Client.ReadRows(request))
            cellCount += row.Families.Sum(f => f.Columns.Sum(c => c.Cells.Count));

        cellCount.Should().Be(2);
    }

    [Fact]
    public async Task CellsPerColumnLimit_per_column()
    {
        var request = MakeRequest("fmv-multicol", RowFilters.CellsPerColumnLimit(1));
        var vals = await CollectValues(request);
        vals.Should().HaveCount(2); // 1 per column: latest alpha + latest beta
    }

    [Fact]
    public async Task ColumnQualifier_filter_with_versions()
    {
        var request = MakeRequest("fmv-multicol", RowFilters.ColumnQualifierExact("beta"));
        var vals = await CollectValues(request);
        vals.Should().HaveCount(3);
    }

    [Fact]
    public async Task Condition_filter_on_multi_version()
    {
        // If latest value of "c" is "v5", return all cells, else block
        var request = MakeRequest("fmv-row",
            RowFilters.Condition(
                RowFilters.Chain(RowFilters.CellsPerColumnLimit(1), RowFilters.ValueExact("v5")),
                RowFilters.PassAllFilter(),
                RowFilters.BlockAllFilter()));
        var vals = await CollectValues(request);
        vals.Should().HaveCount(5);
    }

    [Fact]
    public async Task Condition_filter_false_branch_on_multi_version()
    {
        var request = MakeRequest("fmv-row",
            RowFilters.Condition(
                RowFilters.Chain(RowFilters.CellsPerColumnLimit(1), RowFilters.ValueExact("v1")),
                RowFilters.PassAllFilter(),
                RowFilters.BlockAllFilter()));
        var vals = await CollectValues(request);
        // v1 is not the latest → predicate false → block all
        vals.Should().BeEmpty();
    }

    [Fact]
    public async Task Interleave_different_value_filters()
    {
        var request = MakeRequest("fmv-row",
            RowFilters.Interleave(
                RowFilters.ValueExact("v1"),
                RowFilters.ValueExact("v5")));
        var vals = await CollectValues(request);
        vals.Should().HaveCount(2);
        vals.Should().Contain("v1");
        vals.Should().Contain("v5");
    }

    [Fact]
    public async Task Interleave_limit_and_value()
    {
        var request = MakeRequest("fmv-row",
            RowFilters.Interleave(
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueExact("v1")));
        var vals = await CollectValues(request);
        vals.Should().Contain("v5"); // latest
        vals.Should().Contain("v1"); // exact match
    }

    private ReadRowsRequest MakeRequest(string key, RowFilter filter) =>
        new()
        {
            TableNameAsTableName = TN,
            Filter = filter,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8(key) } }
        };

    private async Task<List<string>> CollectValues(ReadRowsRequest request)
    {
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                vals.Add(cell.Value.ToStringUtf8());
        return vals;
    }
}

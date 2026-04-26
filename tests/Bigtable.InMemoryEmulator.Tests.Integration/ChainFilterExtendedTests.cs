using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for chain (intersection) filter behavior with various combinations.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ChainFilterExtendedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "cfe-tests";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public ChainFilterExtendedTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });

        // Multi-family, multi-column, multi-version row
        await Client.MutateRowAsync(TN, "cfe-row1",
            Mutations.SetCell(CF, "name", "Alice", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "name", "Alice-v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "age", "30", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "age", "31", new BigtableVersion(2000)),
            Mutations.SetCell("cf2", "score", "95", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Chain_family_then_column()
    {
        var request = MakeRequest("cfe-row1", RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.ColumnQualifierExact("name")));
        var vals = await CollectValues(request);
        vals.Should().HaveCount(2); // 2 versions of "name"
    }

    [Fact]
    public async Task Chain_family_column_value()
    {
        var request = MakeRequest("cfe-row1", RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.ColumnQualifierExact("age"),
            RowFilters.ValueExact("31")));
        var vals = await CollectValues(request);
        vals.Should().ContainSingle("31");
    }

    [Fact]
    public async Task Chain_family_column_limit()
    {
        var request = MakeRequest("cfe-row1", RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.CellsPerColumnLimit(1)));
        var vals = await CollectValues(request);
        vals.Should().ContainSingle("Alice-v2");
    }

    [Fact]
    public async Task Chain_with_pass_all()
    {
        var request = MakeRequest("cfe-row1", RowFilters.Chain(
            RowFilters.PassAllFilter(),
            RowFilters.FamilyNameExact(CF)));
        var families = new HashSet<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families) families.Add(f.Name);
        families.Should().ContainSingle(CF);
    }

    [Fact]
    public async Task Chain_with_block_all_returns_nothing()
    {
        var request = MakeRequest("cfe-row1", RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.BlockAllFilter()));
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(0);
    }

    [Fact]
    public async Task Chain_value_regex_then_limit()
    {
        var request = MakeRequest("cfe-row1", RowFilters.Chain(
            RowFilters.ValueRegex("Alice.*"),
            RowFilters.CellsPerColumnLimit(1)));
        var vals = await CollectValues(request);
        vals.Should().ContainSingle("Alice-v2");
    }

    [Fact]
    public async Task Chain_column_range_then_value()
    {
        var request = MakeRequest("cfe-row1", RowFilters.Chain(
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "age", "name")),
            RowFilters.ValueExact("30")));
        var vals = await CollectValues(request);
        vals.Should().ContainSingle("30");
    }

    [Fact]
    public async Task Chain_family_then_strip_value()
    {
        var request = MakeRequest("cfe-row1", RowFilters.Chain(
            RowFilters.FamilyNameExact("cf2"),
            RowFilters.StripValueTransformer()));
        var cells = new List<int>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                cells.Add(cell.Value.Length);
        cells.Should().ContainSingle().Which.Should().Be(0);
    }

    [Fact]
    public async Task Chain_family_then_label()
    {
        var request = MakeRequest("cfe-row1", RowFilters.Chain(
            RowFilters.FamilyNameExact("cf2"),
            new RowFilter { ApplyLabelTransformer = "cf2-data" }));
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                cell.Labels.Should().Contain("cf2-data");
    }

    [Fact]
    public async Task Chain_three_filters()
    {
        var request = MakeRequest("cfe-row1", RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.ColumnQualifierExact("age"),
            RowFilters.CellsPerColumnLimit(1)));
        var vals = await CollectValues(request);
        vals.Should().ContainSingle("31");
    }

    [Fact]
    public async Task Chain_four_filters()
    {
        var request = MakeRequest("cfe-row1", RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.ColumnQualifierRegex(".*"),
            RowFilters.CellsPerColumnLimit(1),
            RowFilters.CellsPerRowLimit(1)));
        var vals = await CollectValues(request);
        vals.Should().ContainSingle();
    }

    [Fact]
    public async Task Chain_timestamp_then_value()
    {
        var start = new DateTime(1970, 1, 1, 0, 0, 1, DateTimeKind.Utc);
        var end = new DateTime(1970, 1, 1, 0, 0, 2, DateTimeKind.Utc);
        var request = MakeRequest("cfe-row1", RowFilters.Chain(
            RowFilters.TimestampRange(start, end),
            RowFilters.ColumnQualifierExact("name")));
        var vals = await CollectValues(request);
        vals.Should().ContainSingle("Alice"); // v1 only
    }

    [Fact]
    public async Task Chain_cells_per_row_limit_then_label()
    {
        var request = MakeRequest("cfe-row1", RowFilters.Chain(
            RowFilters.CellsPerRowLimit(2),
            new RowFilter { ApplyLabelTransformer = "limited" }));
        var labelCount = 0;
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                if (cell.Labels.Contains("limited")) labelCount++;
        labelCount.Should().Be(2);
    }

    [Fact]
    public async Task Chain_cells_per_row_offset_then_limit()
    {
        // Total: 5 cells (2 name + 2 age + 1 score). Skip 2, take 2
        var request = MakeRequest("cfe-row1", RowFilters.Chain(
            RowFilters.CellsPerRowOffset(2),
            RowFilters.CellsPerRowLimit(2)));
        var cellCount = 0;
        await foreach (var row in Client.ReadRows(request))
            cellCount += row.Families.Sum(f => f.Columns.Sum(c => c.Cells.Count));
        cellCount.Should().Be(2);
    }

    [Fact]
    public async Task Chain_interleave_then_limit()
    {
        var request = MakeRequest("cfe-row1", RowFilters.Chain(
            RowFilters.Interleave(
                RowFilters.ColumnQualifierExact("name"),
                RowFilters.ColumnQualifierExact("age")),
            RowFilters.CellsPerRowLimit(3)));
        var cellCount = 0;
        await foreach (var row in Client.ReadRows(request))
            cellCount += row.Families.Sum(f => f.Columns.Sum(c => c.Cells.Count));
        cellCount.Should().Be(3);
    }

    [Fact]
    public async Task Chain_condition_then_filter()
    {
        var request = MakeRequest("cfe-row1", RowFilters.Chain(
            RowFilters.Condition(
                RowFilters.Chain(RowFilters.ColumnQualifierExact("name"), RowFilters.ValueExact("Alice-v2")),
                RowFilters.PassAllFilter(),
                RowFilters.BlockAllFilter()),
            RowFilters.FamilyNameExact("cf2")));
        var vals = await CollectValues(request);
        vals.Should().ContainSingle("95");
    }

    [Fact]
    public async Task Empty_chain_same_as_pass_all()
    {
        // A chain with just one PassAll filter
        var request = MakeRequest("cfe-row1", RowFilters.Chain(RowFilters.PassAllFilter()));
        var cellCount = 0;
        await foreach (var row in Client.ReadRows(request))
            cellCount += row.Families.Sum(f => f.Columns.Sum(c => c.Cells.Count));
        cellCount.Should().Be(5);
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

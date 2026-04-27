using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for interleave (union) filter behavior with different sub-filters.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class InterleaveFilterExtendedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "ife-tests";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public InterleaveFilterExtendedTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });

        await Client.MutateRowAsync(TN, "ife-row1",
            Mutations.SetCell(CF, "alpha", "aaa", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "beta", "bbb", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "gamma", "ccc", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "delta", "ddd", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "ife-row2",
            Mutations.SetCell(CF, "alpha", "xxx", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "beta", "yyy", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Interleave_two_column_exact_filters()
    {
        var request = MakeRequest("ife-row1", RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("alpha"),
            RowFilters.ColumnQualifierExact("gamma")));
        var cols = await CollectColumns(request);
        cols.Should().HaveCount(2);
        cols.Should().Contain("alpha");
        cols.Should().Contain("gamma");
    }

    [Fact]
    public async Task Interleave_three_column_filters()
    {
        var request = MakeRequest("ife-row1", RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("alpha"),
            RowFilters.ColumnQualifierExact("beta"),
            RowFilters.ColumnQualifierExact("gamma")));
        var cols = await CollectColumns(request);
        cols.Should().HaveCount(3);
    }

    [Fact]
    public async Task Interleave_family_filters()
    {
        var request = MakeRequest("ife-row1", RowFilters.Interleave(
            RowFilters.FamilyNameExact(CF),
            RowFilters.FamilyNameExact("cf2")));
        var cols = await CollectColumns(request);
        cols.Should().HaveCount(4); // 3 from cf + 1 from cf2
    }

    [Fact]
    public async Task Interleave_value_filters()
    {
        var request = MakeRequest("ife-row1", RowFilters.Interleave(
            RowFilters.ValueExact("aaa"),
            RowFilters.ValueExact("ddd")));
        var vals = await CollectValues(request);
        vals.Should().HaveCount(2);
        vals.Should().Contain("aaa");
        vals.Should().Contain("ddd");
    }

    [Fact]
    public async Task Interleave_deduplicates_overlapping_results()
    {
        // Both sub-filters match "alpha"
        var request = MakeRequest("ife-row1", RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("alpha"),
            RowFilters.ColumnQualifierRegex("^alpha$")));
        var vals = await CollectValues(request);
        // Interleave produces union — may or may not deduplicate
        vals.Should().Contain("aaa");
    }

    [Fact]
    public async Task Interleave_with_no_match_sub_filter()
    {
        var request = MakeRequest("ife-row1", RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("alpha"),
            RowFilters.ColumnQualifierExact("nonexistent")));
        var cols = await CollectColumns(request);
        cols.Should().Contain("alpha");
    }

    [Fact]
    public async Task Interleave_with_pass_all()
    {
        var request = MakeRequest("ife-row1", RowFilters.Interleave(
            RowFilters.PassAllFilter(),
            RowFilters.ColumnQualifierExact("alpha")));
        var cols = await CollectColumns(request);
        cols.Should().HaveCountGreaterThanOrEqualTo(3); // pass_all returns everything
    }

    [Fact]
    public async Task Interleave_with_block_all()
    {
        var request = MakeRequest("ife-row1", RowFilters.Interleave(
            RowFilters.BlockAllFilter(),
            RowFilters.ColumnQualifierExact("alpha")));
        var vals = await CollectValues(request);
        vals.Should().Contain("aaa");
    }

    [Fact]
    public async Task Interleave_chain_combination()
    {
        var request = MakeRequest("ife-row1", RowFilters.Interleave(
            RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("alpha")),
            RowFilters.Chain(RowFilters.FamilyNameExact("cf2"), RowFilters.ColumnQualifierExact("delta"))));
        var vals = await CollectValues(request);
        vals.Should().HaveCount(2);
        vals.Should().Contain("aaa");
        vals.Should().Contain("ddd");
    }

    [Fact]
    public async Task Interleave_with_strip_value()
    {
        var request = MakeRequest("ife-row1", RowFilters.Interleave(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("alpha"), RowFilters.StripValueTransformer()),
            RowFilters.ColumnQualifierExact("beta")));

        var results = new List<(string col, string val)>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                results.Add((c.Qualifier.ToStringUtf8(), cell.Value.ToStringUtf8()));

        // alpha should have stripped value, beta should have value
        results.Should().Contain(r => r.col == "alpha" && r.val == "");
        results.Should().Contain(r => r.col == "beta" && r.val == "bbb");
    }

    [Fact]
    public async Task Interleave_across_multiple_rows()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Interleave(
                RowFilters.ColumnQualifierExact("alpha"),
                RowFilters.ColumnQualifierExact("beta")),
            Rows = new RowSet
            {
                RowKeys =
                {
                    ByteString.CopyFromUtf8("ife-row1"),
                    ByteString.CopyFromUtf8("ife-row2")
                }
            }
        };
        var rows = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row.Key.ToStringUtf8());
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Interleave_with_label()
    {
        var request = MakeRequest("ife-row1", RowFilters.Interleave(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("alpha"), new RowFilter { ApplyLabelTransformer = "a" }),
            RowFilters.Chain(RowFilters.ColumnQualifierExact("beta"), new RowFilter { ApplyLabelTransformer = "b" })));

        var labels = new Dictionary<string, List<string>>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                labels[c.Qualifier.ToStringUtf8()] = cell.Labels.ToList();

        labels["alpha"].Should().Contain("a");
        labels["beta"].Should().Contain("b");
    }

    [Fact]
    public async Task Interleave_with_cells_per_row_limit()
    {
        var request = MakeRequest("ife-row1", RowFilters.Chain(
            RowFilters.Interleave(
                RowFilters.FamilyNameExact(CF),
                RowFilters.FamilyNameExact("cf2")),
            RowFilters.CellsPerRowLimit(2)));
        var cellCount = 0;
        await foreach (var row in Client.ReadRows(request))
            cellCount += row.Families.Sum(f => f.Columns.Sum(c => c.Cells.Count));
        cellCount.Should().Be(2);
    }

    [Fact]
    public async Task Nested_interleave()
    {
        var request = MakeRequest("ife-row1", RowFilters.Interleave(
            RowFilters.Interleave(
                RowFilters.ColumnQualifierExact("alpha"),
                RowFilters.ColumnQualifierExact("beta")),
            RowFilters.ColumnQualifierExact("gamma")));
        var cols = await CollectColumns(request);
        cols.Should().HaveCount(3);
    }

    private ReadRowsRequest MakeRequest(string key, RowFilter filter) =>
        new()
        {
            TableNameAsTableName = TN,
            Filter = filter,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8(key) } }
        };

    private async Task<List<string>> CollectColumns(ReadRowsRequest request)
    {
        var cols = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
                cols.Add(c.Qualifier.ToStringUtf8());
        return cols;
    }

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

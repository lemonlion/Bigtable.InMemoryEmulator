using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsRowSetCombinedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "rs-combo";
    private const string CF = "cf";

    public ReadRowsRowSetCombinedTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        for (int i = 0; i < 20; i++)
            await Client.MutateRowAsync(TN, $"k{i:D2}", Mutations.SetCell(CF, "c", $"v{i}"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Keys_and_range_combined()
    {
        var rowSet = new RowSet();
        rowSet.RowKeys.Add(ByteString.CopyFromUtf8("k00"));
        rowSet.RowKeys.Add(ByteString.CopyFromUtf8("k19"));
        rowSet.RowRanges.Add(new RowRange
        {
            StartKeyClosed = ByteString.CopyFromUtf8("k05"),
            EndKeyOpen = ByteString.CopyFromUtf8("k08")
        });
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rowSet)) rows.Add(r);
        // k00, k05, k06, k07, k19
        rows.Select(r => r.Key.ToStringUtf8()).Should()
            .BeEquivalentTo(new[] { "k00", "k05", "k06", "k07", "k19" });
    }

    [Fact]
    public async Task Multiple_ranges_no_overlap()
    {
        var rowSet = RowSet.FromRowRanges(
            RowRange.ClosedOpen("k00", "k03"),
            RowRange.ClosedOpen("k10", "k13"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rowSet)) rows.Add(r);
        rows.Should().HaveCount(6); // k00,k01,k02,k10,k11,k12
    }

    [Fact]
    public async Task Range_with_filter()
    {
        var rowSet = RowSet.FromRowRanges(RowRange.Closed("k00", "k09"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rowSet, RowFilters.ValueRegex("v[02468]")))
            rows.Add(r);
        rows.Should().HaveCount(5); // v0, v2, v4, v6, v8
    }

    [Fact]
    public async Task Range_with_limit()
    {
        var rowSet = RowSet.FromRowRanges(RowRange.Closed("k00", "k19"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rowSet, rowsLimit: 5))
            rows.Add(r);
        rows.Should().HaveCount(5);
        rows[0].Key.ToStringUtf8().Should().Be("k00");
    }

    [Fact]
    public async Task Empty_range_returns_nothing()
    {
        var rowSet = RowSet.FromRowRanges(RowRange.Open("k05", "k05"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rowSet)) rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task All_rows_no_rowset()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN)) rows.Add(r);
        rows.Should().HaveCount(20);
    }

    [Fact]
    public async Task Prefix_scan()
    {
        var rowSet = RowSet.FromRowRanges(RowRange.ClosedOpen("k1", "k2"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rowSet)) rows.Add(r);
        rows.Should().HaveCount(10); // k10-k19
    }

    [Fact]
    public async Task Exact_keys_dedup()
    {
        var rowSet = RowSet.FromRowKeys("k05", "k05", "k05");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rowSet)) rows.Add(r);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Range_and_limit_and_filter()
    {
        var rowSet = RowSet.FromRowRanges(RowRange.Closed("k00", "k19"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rowSet,
            RowFilters.CellsPerRowLimit(1), rowsLimit: 3))
            rows.Add(r);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Keys_sorted_regardless_of_input_order()
    {
        var rowSet = RowSet.FromRowKeys("k19", "k00", "k10");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rowSet)) rows.Add(r);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeInAscendingOrder();
    }
}

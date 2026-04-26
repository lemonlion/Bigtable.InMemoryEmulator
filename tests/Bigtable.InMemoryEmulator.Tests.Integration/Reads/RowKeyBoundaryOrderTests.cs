using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class RowKeyBoundaryOrderTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "rk-bnd-ord";
    private const string CF = "cf";

    public RowKeyBoundaryOrderTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        var keys = new[] { "a", "aa", "ab", "b", "ba", "bb", "c", "z" };
        foreach (var k in keys)
            await Client.MutateRowAsync(TN, k, Mutations.SetCell(CF, "v", k));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Full_scan_sorted()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN)) rows.Add(r);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Range_a_to_b_exclusive()
    {
        var rowSet = new RowSet { RowRanges = { RowRange.ClosedOpen("a", "b") } };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeEquivalentTo(new[] { "a", "aa", "ab" });
    }

    [Fact]
    public async Task Range_a_to_b_inclusive()
    {
        var rowSet = new RowSet { RowRanges = { RowRange.Closed("a", "b") } };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeEquivalentTo(new[] { "a", "aa", "ab", "b" });
    }

    [Fact]
    public async Task Range_open_both()
    {
        var rowSet = new RowSet { RowRanges = { RowRange.Open("a", "b") } };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeEquivalentTo(new[] { "aa", "ab" });
    }

    [Fact]
    public async Task Single_key_as_range()
    {
        var rowSet = new RowSet { RowRanges = { RowRange.Closed("b", "b") } };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Should().ContainSingle().Which.Key.ToStringUtf8().Should().Be("b");
    }

    [Fact]
    public async Task Prefix_scan()
    {
        var rowSet = new RowSet { RowRanges = { RowRange.ClosedOpen("b", "c") } };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeEquivalentTo(new[] { "b", "ba", "bb" });
    }

    [Fact]
    public async Task Specific_keys_returned_sorted()
    {
        var rowSet = RowSet.FromRowKeys("z", "a", "c");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Row_key_regex_sorted()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex("[abc]")))
            rows.Add(r);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Empty_range_no_results()
    {
        var rowSet = new RowSet { RowRanges = { RowRange.Open("a", "a") } };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Range_beyond_data()
    {
        var rowSet = new RowSet { RowRanges = { RowRange.ClosedOpen("zz", "zzz") } };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Range_before_data()
    {
        var rowSet = new RowSet { RowRanges = { RowRange.ClosedOpen("0", "1") } };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Open_ended_start()
    {
        var rowSet = new RowSet { RowRanges = { new RowRange { EndKeyOpen = ByteString.CopyFromUtf8("aa") } } };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Should().ContainSingle().Which.Key.ToStringUtf8().Should().Be("a");
    }

    [Fact]
    public async Task Open_ended_end()
    {
        var rowSet = new RowSet { RowRanges = { new RowRange { StartKeyClosed = ByteString.CopyFromUtf8("z") } } };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Should().ContainSingle().Which.Key.ToStringUtf8().Should().Be("z");
    }

    [Fact]
    public async Task Limit_with_range()
    {
        var rowSet = new RowSet { RowRanges = { RowRange.ClosedOpen("a", "z") } };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet, rowsLimit: 3)) rows.Add(r);
        rows.Should().HaveCount(3);
        rows[0].Key.ToStringUtf8().Should().Be("a");
    }
}

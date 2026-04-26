using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsRangeEdgeCaseTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "rr-edge";
    private const string CF = "cf";

    public ReadRowsRangeEdgeCaseTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        foreach (var key in new[] { "a", "b", "c", "d", "e", "f", "g" })
            await Client.MutateRowAsync(TN, key, Mutations.SetCell(CF, "c", key));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Closed_range_both_endpoints()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, RowSet.FromRowRanges(RowRange.Closed("b", "e"))))
            rows.Add(r);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeEquivalentTo(new[] { "b", "c", "d", "e" });
    }

    [Fact]
    public async Task Open_range_excludes_endpoints()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, RowSet.FromRowRanges(RowRange.Open("b", "e"))))
            rows.Add(r);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeEquivalentTo(new[] { "c", "d" });
    }

    [Fact]
    public async Task ClosedOpen_range()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, RowSet.FromRowRanges(RowRange.ClosedOpen("b", "e"))))
            rows.Add(r);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeEquivalentTo(new[] { "b", "c", "d" });
    }

    [Fact]
    public async Task OpenClosed_range()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, RowSet.FromRowRanges(RowRange.OpenClosed("b", "e"))))
            rows.Add(r);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeEquivalentTo(new[] { "c", "d", "e" });
    }

    [Fact]
    public async Task Range_beyond_all_keys()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, RowSet.FromRowRanges(RowRange.Closed("x", "z"))))
            rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Range_before_all_keys()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, RowSet.FromRowRanges(RowRange.Closed("0", "9"))))
            rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Single_key_range()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, RowSet.FromRowRanges(RowRange.Closed("c", "c"))))
            rows.Add(r);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Empty_open_range()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, RowSet.FromRowRanges(RowRange.Open("c", "c"))))
            rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Range_covering_all_keys()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, RowSet.FromRowRanges(RowRange.Closed("a", "g"))))
            rows.Add(r);
        rows.Should().HaveCount(7);
    }

    [Fact]
    public async Task Range_with_limit_one()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, RowSet.FromRowRanges(RowRange.Closed("a", "g")), rowsLimit: 1))
            rows.Add(r);
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("a");
    }

    [Fact]
    public async Task Multiple_ranges()
    {
        var ranges = RowSet.FromRowRanges(
            RowRange.Closed("a", "b"),
            RowRange.Closed("f", "g"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, ranges)) rows.Add(r);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeEquivalentTo(new[] { "a", "b", "f", "g" });
    }

    [Fact]
    public async Task Overlapping_ranges()
    {
        var ranges = RowSet.FromRowRanges(
            RowRange.Closed("a", "d"),
            RowRange.Closed("c", "f"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, ranges)) rows.Add(r);
        // Should deduplicate: a,b,c,d,e,f
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeEquivalentTo(new[] { "a", "b", "c", "d", "e", "f" });
    }

    [Fact]
    public async Task Results_always_sorted()
    {
        var ranges = RowSet.FromRowRanges(
            RowRange.Closed("f", "g"),
            RowRange.Closed("a", "b"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, ranges)) rows.Add(r);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeInAscendingOrder();
    }
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ValueRangeBehaviorTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "vr-beh";
    private const string CF = "cf";

    public ValueRangeBehaviorTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        await Client.MutateRowAsync(TN, "r1", Mutations.SetCell(CF, "c", "apple"));
        await Client.MutateRowAsync(TN, "r2", Mutations.SetCell(CF, "c", "banana"));
        await Client.MutateRowAsync(TN, "r3", Mutations.SetCell(CF, "c", "cherry"));
        await Client.MutateRowAsync(TN, "r4", Mutations.SetCell(CF, "c", "date"));
        await Client.MutateRowAsync(TN, "r5", Mutations.SetCell(CF, "c", "elderberry"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Closed_range()
    {
        var range = ValueRange.Closed("banana", "date");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueRange(range)))
            rows.Add(r);
        rows.Should().HaveCount(3); // banana, cherry, date
    }

    [Fact]
    public async Task Open_range()
    {
        var range = ValueRange.Open("banana", "elderberry");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueRange(range)))
            rows.Add(r);
        rows.Should().HaveCount(2); // cherry, date
    }

    [Fact]
    public async Task ClosedOpen_range()
    {
        var range = ValueRange.ClosedOpen("banana", "date");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueRange(range)))
            rows.Add(r);
        rows.Should().HaveCount(2); // banana, cherry
    }

    [Fact]
    public async Task OpenClosed_range()
    {
        var range = ValueRange.OpenClosed("banana", "date");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueRange(range)))
            rows.Add(r);
        rows.Should().HaveCount(2); // cherry, date
    }

    [Fact]
    public async Task Single_value_range()
    {
        var range = ValueRange.Closed("cherry", "cherry");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueRange(range)))
            rows.Add(r);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task No_match_range()
    {
        var range = ValueRange.Closed("fig", "grape");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueRange(range)))
            rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Full_range()
    {
        var range = ValueRange.Closed("apple", "elderberry");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueRange(range)))
            rows.Add(r);
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task Range_with_limit()
    {
        var range = ValueRange.Closed("apple", "elderberry");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueRange(range), rowsLimit: 3))
            rows.Add(r);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Range_with_chain()
    {
        var chain = RowFilters.Chain(
            RowFilters.ValueRange(ValueRange.Closed("banana", "date")),
            RowFilters.CellsPerRowLimit(1));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: chain)) rows.Add(r);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Range_on_missing_row()
    {
        var range = ValueRange.Closed("apple", "elderberry");
        var row = await Client.ReadRowAsync(TN, "missing", RowFilters.ValueRange(range));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Empty_open_range()
    {
        var range = ValueRange.Open("cherry", "cherry");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueRange(range)))
            rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Range_sorted_results()
    {
        var range = ValueRange.Closed("apple", "elderberry");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueRange(range)))
            rows.Add(r);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeInAscendingOrder();
    }
}

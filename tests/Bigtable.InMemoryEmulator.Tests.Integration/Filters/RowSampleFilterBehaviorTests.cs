using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.RowFilter
///   "Matches all cells from a row with probability p"
/// Go emulator divergence: RowSampleFilter does not guarantee correct behavior.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.InMemoryOnly)]
public sealed class RowSampleFilterBehaviorTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "rsamp-beh";
    private const string CF = "cf";

    public RowSampleFilterBehaviorTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        for (int i = 0; i < 100; i++)
            await Client.MutateRowAsync(TN, $"row-{i:D3}", Mutations.SetCell(CF, "v", $"{i}"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Sample_1_returns_all()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowSample(1.0)))
            rows.Add(r);
        rows.Should().HaveCount(100);
    }

    [Fact]
    public async Task Sample_very_low_returns_few()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowSample(0.0001)))
            rows.Add(r);
        rows.Count.Should().BeLessThan(50);
    }

    [Fact]
    public async Task Sample_half_returns_approximately_half()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowSample(0.5)))
            rows.Add(r);
        rows.Count.Should().BeGreaterThan(10);
        rows.Count.Should().BeLessThan(90);
    }

    [Fact]
    public async Task Sample_with_chain()
    {
        var chain = RowFilters.Chain(
            RowFilters.RowSample(1.0),
            RowFilters.CellsPerRowLimit(1));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: chain)) rows.Add(r);
        rows.Should().HaveCount(100);
    }

    [Fact]
    public async Task Sample_with_limit()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowSample(1.0), rowsLimit: 10))
            rows.Add(r);
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task Sample_preserves_sort_order()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowSample(0.5)))
            rows.Add(r);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Sample_on_empty_table()
    {
        await _fixture.CreateTableAsync("rsamp-empty", new[] { CF });
        var tn = _fixture.GetTableName("rsamp-empty");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(tn, filter: RowFilters.RowSample(0.5)))
            rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Sample_on_single_row()
    {
        await _fixture.CreateTableAsync("rsamp-one", new[] { CF });
        var tn = _fixture.GetTableName("rsamp-one");
        await Client.MutateRowAsync(tn, "only", Mutations.SetCell(CF, "c", "v"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(tn, filter: RowFilters.RowSample(1.0)))
            rows.Add(r);
        rows.Should().ContainSingle();
    }
}

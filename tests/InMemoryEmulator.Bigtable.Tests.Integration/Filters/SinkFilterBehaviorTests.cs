using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class SinkFilterBehaviorTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "sink-beh";
    private const string CF = "cf";

    public SinkFilterBehaviorTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        await Client.MutateRowAsync(TN, "r1",
            Mutations.SetCell(CF, "a", "v1"),
            Mutations.SetCell(CF, "b", "v2"));
        await Client.MutateRowAsync(TN, "r2",
            Mutations.SetCell(CF, "a", "v3"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Sink_in_chain_passes_through()
    {
        // Sink in a chain — cells pass through to output
        var filter = RowFilters.Chain(
            new RowFilter { Sink = true },
            RowFilters.ColumnQualifierExact("b"));
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
        // The chain's second filter narrows to "b" only
        var cols = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("b");
    }

    [Fact]
    public async Task Sink_alone_returns_all_cells()
    {
        var filter = new RowFilter { Sink = true };
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns.SelectMany(c => c.Cells)).ToList();
        cells.Should().HaveCount(2);
    }

    [Fact]
    [Trait(TestTraits.Target, TestTraits.InMemoryOnly)] // Go emulator does not suppress output for Sink=false
    public async Task Sink_false_blocks_all()
    {
        // Sink = false means no sink output — cells are not emitted
        var filter = new RowFilter { Sink = false };
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().BeNull();
    }

    [Fact]
    public async Task Sink_in_condition_true_branch()
    {
        var filter = RowFilters.Condition(
            RowFilters.ColumnQualifierExact("a"),
            new RowFilter { Sink = true },
            RowFilters.BlockAllFilter());
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Sink_preserves_values()
    {
        var filter = new RowFilter { Sink = true };
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        var values = row!.Families.SelectMany(f => f.Columns.SelectMany(c => c.Cells))
            .Select(c => c.Value.ToStringUtf8()).ToList();
        values.Should().Contain("v1").And.Contain("v2");
    }
}

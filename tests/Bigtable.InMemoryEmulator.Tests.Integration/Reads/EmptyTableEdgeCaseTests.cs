using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class EmptyTableEdgeCaseTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "empty-tbl";
    private const string CF = "cf";

    public EmptyTableEdgeCaseTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task ReadRows_on_empty_table_returns_empty()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN)) rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadRow_on_empty_table_returns_null()
    {
        var row = await Client.ReadRowAsync(TN, "any");
        row.Should().BeNull();
    }

    [Fact]
    public async Task ReadRows_with_filter_on_empty_table()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.PassAllFilter()))
            rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadRows_with_row_key_on_empty_table()
    {
        var rowSet = RowSet.FromRowKeys("key1", "key2");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet))
            rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadRows_with_row_range_on_empty_table()
    {
        var rowSet = new RowSet { RowRanges = { RowRange.ClosedOpen("a", "z") } };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet))
            rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteFromRow_on_empty_table_is_noop()
    {
        await Client.MutateRowAsync(TN, "nope", Mutations.DeleteFromRow());
        var row = await Client.ReadRowAsync(TN, "nope");
        row.Should().BeNull();
    }

    [Fact]
    public async Task CheckAndMutate_on_empty_row()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "missing",
            RowFilters.PassAllFilter(),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "a", "1") });
        result.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, "missing");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task ReadModifyWrite_on_empty_row_creates_it()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "rmw-new",
            ReadModifyWriteRules.Append(CF, "col", "hello"));
        response.Row.Should().NotBeNull();
        var row = await Client.ReadRowAsync(TN, "rmw-new");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task SampleRowKeys_on_empty_table()
    {
        var response = _fixture.ServiceApiClient.SampleRowKeys(
            new SampleRowKeysRequest { TableName = TN.ToString() });
        var samples = new List<SampleRowKeysResponse>();
        var stream = response.GetResponseStream();
        while (await stream.MoveNextAsync())
            samples.Add(stream.Current);
        // Empty table may return 0 or 1 sample (final offset)
        samples.Should().HaveCountLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task Write_read_delete_read_cycle()
    {
        await Client.MutateRowAsync(TN, "cycle", Mutations.SetCell(CF, "c", "v"));
        var row1 = await Client.ReadRowAsync(TN, "cycle");
        row1.Should().NotBeNull();
        await Client.MutateRowAsync(TN, "cycle", Mutations.DeleteFromRow());
        var row2 = await Client.ReadRowAsync(TN, "cycle");
        row2.Should().BeNull();
    }

    [Fact]
    public async Task ReadRows_with_limit_on_empty_table()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rowsLimit: 10))
            rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task MutateRows_batch_on_empty_table()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("b1", Mutations.SetCell(CF, "c", "1")),
            Mutations.CreateEntry("b2", Mutations.SetCell(CF, "c", "2")),
        };
        var response = await Client.MutateRowsAsync(TN, entries);
        response.Should().NotBeNull();
        var row = await Client.ReadRowAsync(TN, "b1");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task ReadRows_block_all_on_empty_table()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.BlockAllFilter()))
            rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadRows_with_regex_on_empty_table()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex(".*")))
            rows.Add(r);
        rows.Should().BeEmpty();
    }
}

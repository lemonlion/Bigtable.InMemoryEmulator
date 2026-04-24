using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "readrows-tests";
    private const string Family = "cf";

    public ReadRowsIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { Family });
        var client = _fixture.Client;
        var tn = _fixture.GetTableName(Table);
        foreach (var key in new[] { "a", "b", "c", "d", "e" })
            await client.MutateRowAsync(tn, new BigtableByteString(key),
                Mutations.SetCell(Family, "col", "val-" + key, new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<Row>> ReadAll(TableName tn, RowSet? rows = null, RowFilter? filter = null, long? rowsLimit = null)
    {
        var list = new List<Row>();
        var stream = Client.ReadRows(tn, rows: rows, filter: filter, rowsLimit: rowsLimit);
        var e = stream.GetAsyncEnumerator(default);
        while (await e.MoveNextAsync()) list.Add(e.Current);
        return list;
    }

    [Fact]
    public async Task ReadRow_nonexistent_returns_null()
    {
        var row = await Client.ReadRowAsync(TN, new BigtableByteString("nonexistent"));
        row.Should().BeNull();
    }

    [Fact]
    public async Task ReadRows_returns_all_rows_in_lexicographic_order()
    {
        var rows = await ReadAll(TN);
        rows.Should().HaveCount(5);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task ReadRows_by_specific_keys()
    {
        var rows = await ReadAll(TN, RowSet.FromRowKeys("a", "c", "e"));
        rows.Should().HaveCount(3);
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("a", "c", "e");
    }

    [Fact]
    public async Task ReadRows_with_range()
    {
        var range = RowRange.ClosedOpen("b", "d");
        var rows = await ReadAll(TN, RowSet.FromRowRanges(range));
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("b", "c");
    }

    [Fact]
    public async Task ReadRows_with_rows_limit()
    {
        var rows = await ReadAll(TN, rowsLimit: 2);
        rows.Should().HaveCount(2);
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("a", "b");
    }

    [Fact]
    public async Task ReadRows_multiple_versions_ordered_desc()
    {
        var rowKey = new BigtableByteString("multi-ver");
        await Client.MutateRowAsync(TN, rowKey,
            Mutations.SetCell(Family, "col", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rowKey,
            Mutations.SetCell(Family, "col", "new", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, rowKey);
        row.Should().NotBeNull();
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2);
        cells[0].Value.ToStringUtf8().Should().Be("new");
        cells[1].Value.ToStringUtf8().Should().Be("old");
    }

    [Fact]
    public async Task ReadRows_with_row_filter()
    {
        await Client.MutateRowAsync(TN, new BigtableByteString("a"),
            Mutations.SetCell(Family, "extra", "x", new BigtableVersion(2000)));
        var filter = RowFilters.ColumnQualifierExact("col");
        var rows = await ReadAll(TN, RowSet.FromRowKeys("a"), filter);
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns.Should().ContainSingle();
    }

    [Fact]
    public async Task ReadRows_empty_table_returns_empty()
    {
        await _fixture.CreateTableAsync("empty-table", new[] { Family });
        var tn = _fixture.GetTableName("empty-table");
        var rows = await ReadAll(tn);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadRows_reversed_returns_descending_order()
    {
        // Ref: ReadRowsRequest.reversed — "Return rows in lexicographical descending order"
        // BigtableClient.ReadRows wraps the stream with RowAsyncEnumerator which enforces ascending key order,
        // so we must use the raw BigtableServiceApiClient to test reversed scans.
        var request = new ReadRowsRequest
        {
            TableName = TN.ToString(),
            Reversed = true,
        };
        var stream = _fixture.ServiceApiClient.ReadRows(request);
        var keys = new List<string>();
        await using var enumerator = stream.GetResponseStream().GetAsyncEnumerator();
        while (await enumerator.MoveNextAsync())
        {
            foreach (var chunk in enumerator.Current.Chunks)
            {
                if (chunk.RowKey is { Length: > 0 })
                    keys.Add(chunk.RowKey.ToStringUtf8());
            }
        }

        keys.Should().HaveCount(5);
        keys[0].Should().Be("e");
        keys[4].Should().Be("a");
    }
}

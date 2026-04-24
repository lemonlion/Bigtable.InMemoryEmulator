using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for ReadRows with limit interactions.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsLimitTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "rrl-test";
    private const string CF = "cf";

    public ReadRowsLimitTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        for (int i = 0; i < 50; i++)
            await Client.MutateRowAsync(TN, $"lim-{i:D3}",
                Mutations.SetCell(CF, "c", $"val{i}", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null, long? limit = null)
    {
        var list = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: rows, filter: filter, rowsLimit: limit))
            list.Add(row);
        return list;
    }

    #region Basic limits

    [Fact]
    public async Task Limit_1()
    {
        var rows = await ReadAll(limit: 1);
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("lim-000");
    }

    [Fact]
    public async Task Limit_5()
    {
        var rows = await ReadAll(limit: 5);
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task Limit_10()
    {
        var rows = await ReadAll(limit: 10);
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task Limit_50_returns_all()
    {
        var rows = await ReadAll(limit: 50);
        rows.Should().HaveCount(50);
    }

    [Fact]
    public async Task Limit_100_returns_all_50()
    {
        var rows = await ReadAll(limit: 100);
        rows.Should().HaveCount(50);
    }

    [Fact]
    public async Task No_limit_returns_all()
    {
        var rows = await ReadAll();
        rows.Should().HaveCount(50);
    }

    #endregion

    #region Limit with ranges

    [Fact]
    public async Task Limit_with_range()
    {
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("lim-010", "lim-030"));
        var rows = await ReadAll(rows: rowSet, limit: 5);
        rows.Should().HaveCount(5);
        rows[0].Key.ToStringUtf8().Should().Be("lim-010");
    }

    [Fact]
    public async Task Limit_exceeding_range()
    {
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("lim-045", "lim-050"));
        var rows = await ReadAll(rows: rowSet, limit: 100);
        rows.Should().HaveCount(5);
    }

    #endregion

    #region Limit with keys

    [Fact]
    public async Task Limit_with_specific_keys()
    {
        var rowSet = RowSet.FromRowKeys("lim-000", "lim-010", "lim-020", "lim-030", "lim-040");
        var rows = await ReadAll(rows: rowSet, limit: 3);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Limit_1_with_many_keys()
    {
        var keys = Enumerable.Range(0, 50).Select(i => (BigtableByteString)$"lim-{i:D3}").ToArray();
        var rowSet = RowSet.FromRowKeys(keys);
        var rows = await ReadAll(rows: rowSet, limit: 1);
        rows.Should().ContainSingle();
    }

    #endregion

    #region Limit with filters

    [Fact]
    public async Task Limit_with_value_filter()
    {
        var filter = RowFilters.ValueRegex("val[0-9]");
        var rows = await ReadAll(filter: filter, limit: 3);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Limit_with_row_key_regex()
    {
        var filter = RowFilters.RowKeyRegex("lim-0[0-9][0-9]");
        var rows = await ReadAll(filter: filter, limit: 5);
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task Limit_with_strip_value()
    {
        var rows = await ReadAll(
            filter: RowFilters.StripValueTransformer(),
            limit: 3);
        rows.Should().HaveCount(3);
        foreach (var row in rows)
            row.Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    #endregion

    #region Limit ordering

    [Fact]
    public async Task Limit_returns_first_n_in_order()
    {
        var rows = await ReadAll(limit: 5);
        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().BeInAscendingOrder();
        keys[0].Should().Be("lim-000");
        keys[4].Should().Be("lim-004");
    }

    [Fact]
    public async Task Limit_from_middle_of_range()
    {
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("lim-020", "lim-050"));
        var rows = await ReadAll(rows: rowSet, limit: 3);
        rows[0].Key.ToStringUtf8().Should().Be("lim-020");
        rows[2].Key.ToStringUtf8().Should().Be("lim-022");
    }

    #endregion

    #region Edge cases

    [Fact]
    public async Task Limit_on_empty_result()
    {
        var rows = await ReadAll(
            rows: RowSet.FromRowKeys("nonexistent"),
            limit: 10);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Limit_with_filter_that_removes_all()
    {
        var rows = await ReadAll(
            filter: RowFilters.BlockAllFilter(),
            limit: 10);
        rows.Should().BeEmpty();
    }

    #endregion
}

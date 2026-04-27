using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for ReadModifyWrite increment and append semantics.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readmodifywriterowrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadModifyWriteIncrementTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "rmw-inc";
    private const string CF = "cf";

    public ReadModifyWriteIncrementTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null)
    {
        var list = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: rows, filter: filter))
            list.Add(row);
        return list;
    }

    private long ReadInt64(Row row, string family, string col)
    {
        var cell = row.Families.First(f => f.Name == family)
            .Columns.First(c => c.Qualifier.ToStringUtf8() == col)
            .Cells[0];
        var bytes = cell.Value.ToByteArray();
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToInt64(bytes, 0);
    }

    private long ReadInt64Resp(ReadModifyWriteRowResponse resp, string family, string col) =>
        ReadInt64(resp.Row, family, col);

    #region Increment from zero

    [Fact]
    public async Task Increment_new_row_from_zero()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-01",
            ReadModifyWriteRules.Increment(CF, "counter", 1));
        ReadInt64Resp(resp, CF, "counter").Should().Be(1);
    }

    [Fact]
    public async Task Increment_new_row_by_10()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-02",
            ReadModifyWriteRules.Increment(CF, "counter", 10));
        ReadInt64Resp(resp, CF, "counter").Should().Be(10);
    }

    [Fact]
    public async Task Increment_new_row_by_negative()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-03",
            ReadModifyWriteRules.Increment(CF, "counter", -5));
        ReadInt64Resp(resp, CF, "counter").Should().Be(-5);
    }

    #endregion

    #region Sequential increments

    [Fact]
    public async Task Sequential_increments_accumulate()
    {
        for (int i = 0; i < 10; i++)
            await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-04",
                ReadModifyWriteRules.Increment(CF, "counter", 1));

        var rows = await ReadAll(rows: RowSet.FromRowKeys("rmw-inc-04"));
        ReadInt64(rows[0], CF, "counter").Should().Be(10);
    }

    [Fact]
    public async Task Sequential_increments_with_varying_amounts()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-05",
            ReadModifyWriteRules.Increment(CF, "counter", 10));
        await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-05",
            ReadModifyWriteRules.Increment(CF, "counter", 20));
        await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-05",
            ReadModifyWriteRules.Increment(CF, "counter", -5));

        var rows = await ReadAll(rows: RowSet.FromRowKeys("rmw-inc-05"));
        ReadInt64(rows[0], CF, "counter").Should().Be(25);
    }

    [Fact]
    public async Task Increment_response_returns_running_total()
    {
        var r1 = await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-06",
            ReadModifyWriteRules.Increment(CF, "counter", 5));
        ReadInt64Resp(r1, CF, "counter").Should().Be(5);

        var r2 = await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-06",
            ReadModifyWriteRules.Increment(CF, "counter", 3));
        ReadInt64Resp(r2, CF, "counter").Should().Be(8);

        var r3 = await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-06",
            ReadModifyWriteRules.Increment(CF, "counter", -2));
        ReadInt64Resp(r3, CF, "counter").Should().Be(6);
    }

    #endregion

    #region Multiple columns

    [Fact]
    public async Task Increment_multiple_columns()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-07",
            ReadModifyWriteRules.Increment(CF, "a", 1),
            ReadModifyWriteRules.Increment(CF, "b", 2),
            ReadModifyWriteRules.Increment(CF, "c", 3));
        ReadInt64Resp(resp, CF, "a").Should().Be(1);
        ReadInt64Resp(resp, CF, "b").Should().Be(2);
        ReadInt64Resp(resp, CF, "c").Should().Be(3);
    }

    [Fact]
    public async Task Increment_multiple_columns_sequential()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-08",
            ReadModifyWriteRules.Increment(CF, "x", 10),
            ReadModifyWriteRules.Increment(CF, "y", 20));
        var r2 = await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-08",
            ReadModifyWriteRules.Increment(CF, "x", 5),
            ReadModifyWriteRules.Increment(CF, "y", -10));
        ReadInt64Resp(r2, CF, "x").Should().Be(15);
        ReadInt64Resp(r2, CF, "y").Should().Be(10);
    }

    #endregion

    #region Cross-family

    [Fact]
    public async Task Increment_cross_family()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-09",
            ReadModifyWriteRules.Increment(CF, "count", 1),
            ReadModifyWriteRules.Increment("cf2", "count", 100));
        ReadInt64Resp(resp, CF, "count").Should().Be(1);
        ReadInt64Resp(resp, "cf2", "count").Should().Be(100);
    }

    #endregion

    #region Large values

    [Fact]
    public async Task Increment_large_value()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-10",
            ReadModifyWriteRules.Increment(CF, "big", 1_000_000_000L));
        ReadInt64Resp(resp, CF, "big").Should().Be(1_000_000_000L);
    }

    [Fact]
    public async Task Increment_max_long()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-11",
            ReadModifyWriteRules.Increment(CF, "max", long.MaxValue));
        ReadInt64Resp(resp, CF, "max").Should().Be(long.MaxValue);
    }

    #endregion

    #region Append

    [Fact]
    public async Task Append_new_row()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-app-01",
            ReadModifyWriteRules.Append(CF, "data", "hello"));
        resp.Row.Families.First(f => f.Name == CF)
            .Columns.First(c => c.Qualifier.ToStringUtf8() == "data")
            .Cells[0].Value.ToStringUtf8().Should().Be("hello");
    }

    [Fact]
    public async Task Append_sequential()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmw-app-02",
            ReadModifyWriteRules.Append(CF, "data", "hello"));
        var r2 = await Client.ReadModifyWriteRowAsync(TN, "rmw-app-02",
            ReadModifyWriteRules.Append(CF, "data", " world"));
        r2.Row.Families.First(f => f.Name == CF)
            .Columns.First(c => c.Qualifier.ToStringUtf8() == "data")
            .Cells[0].Value.ToStringUtf8().Should().Be("hello world");
    }

    [Fact]
    public async Task Append_multiple_times()
    {
        for (int i = 0; i < 5; i++)
            await Client.ReadModifyWriteRowAsync(TN, "rmw-app-03",
                ReadModifyWriteRules.Append(CF, "log", $"[{i}]"));

        var rows = await ReadAll(rows: RowSet.FromRowKeys("rmw-app-03"));
        rows[0].Families.First(f => f.Name == CF)
            .Columns.First(c => c.Qualifier.ToStringUtf8() == "log")
            .Cells[0].Value.ToStringUtf8().Should().Be("[0][1][2][3][4]");
    }

    [Fact]
    public async Task Append_empty_string()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmw-app-04",
            ReadModifyWriteRules.Append(CF, "data", "start"));
        var r2 = await Client.ReadModifyWriteRowAsync(TN, "rmw-app-04",
            ReadModifyWriteRules.Append(CF, "data", ""));
        r2.Row.Families.First(f => f.Name == CF)
            .Columns.First(c => c.Qualifier.ToStringUtf8() == "data")
            .Cells[0].Value.ToStringUtf8().Should().Be("start");
    }

    [Fact]
    public async Task Append_binary_data()
    {
        var bytes1 = new byte[] { 0x01, 0x02 };
        var bytes2 = new byte[] { 0x03, 0x04 };
        await Client.ReadModifyWriteRowAsync(TN, "rmw-app-05",
            ReadModifyWriteRules.Append(CF, "bin", ByteString.CopyFrom(bytes1)));
        var r2 = await Client.ReadModifyWriteRowAsync(TN, "rmw-app-05",
            ReadModifyWriteRules.Append(CF, "bin", ByteString.CopyFrom(bytes2)));
        r2.Row.Families.First(f => f.Name == CF)
            .Columns.First(c => c.Qualifier.ToStringUtf8() == "bin")
            .Cells[0].Value.ToByteArray().Should().BeEquivalentTo(new byte[] { 0x01, 0x02, 0x03, 0x04 });
    }

    #endregion

    #region Mixed increment and append

    [Fact]
    public async Task Mixed_increment_and_append_same_request()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-mix-01",
            ReadModifyWriteRules.Increment(CF, "count", 1),
            ReadModifyWriteRules.Append(CF, "log", "entry1"));
        ReadInt64Resp(resp, CF, "count").Should().Be(1);
        resp.Row.Families.First(f => f.Name == CF)
            .Columns.First(c => c.Qualifier.ToStringUtf8() == "log")
            .Cells[0].Value.ToStringUtf8().Should().Be("entry1");
    }

    [Fact]
    public async Task Mixed_increment_and_append_cross_family()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-mix-02",
            ReadModifyWriteRules.Increment(CF, "views", 1),
            ReadModifyWriteRules.Append("cf2", "trail", "A"));
        ReadInt64Resp(resp, CF, "views").Should().Be(1);
        resp.Row.Families.First(f => f.Name == "cf2")
            .Columns.First(c => c.Qualifier.ToStringUtf8() == "trail")
            .Cells[0].Value.ToStringUtf8().Should().Be("A");
    }

    #endregion
}

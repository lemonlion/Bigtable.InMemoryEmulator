using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Stress tests for ReadModifyWriteRow — append, increment, combinations.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readmodifywriterowrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadModifyWriteStressTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "rmw-stress";
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public ReadModifyWriteStressTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<Row>> ReadAll(RowSet? rows = null)
    {
        var list = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: rows))
            list.Add(row);
        return list;
    }

    private static long ReadInt64(ByteString value)
    {
        var bytes = value.ToByteArray();
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToInt64(bytes, 0);
    }

    #region Increment basics

    [Fact]
    public async Task Increment_new_cell_starts_at_zero()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-new",
            ReadModifyWriteRules.Increment(CF, "counter", 5));
        var val = ReadInt64(resp.Row.Families.First(f => f.Name == CF).Columns[0].Cells[0].Value);
        val.Should().Be(5);
    }

    [Fact]
    public async Task Increment_accumulates()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-acc",
            ReadModifyWriteRules.Increment(CF, "counter", 3));
        await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-acc",
            ReadModifyWriteRules.Increment(CF, "counter", 7));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-acc",
            ReadModifyWriteRules.Increment(CF, "counter", 0));
        ReadInt64(resp.Row.Families.First(f => f.Name == CF).Columns[0].Cells[0].Value).Should().Be(10);
    }

    [Fact]
    public async Task Increment_negative()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-neg",
            ReadModifyWriteRules.Increment(CF, "counter", 10));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-neg",
            ReadModifyWriteRules.Increment(CF, "counter", -3));
        ReadInt64(resp.Row.Families.First(f => f.Name == CF).Columns[0].Cells[0].Value).Should().Be(7);
    }

    [Fact]
    public async Task Increment_by_zero()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-zero",
            ReadModifyWriteRules.Increment(CF, "counter", 42));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-zero",
            ReadModifyWriteRules.Increment(CF, "counter", 0));
        ReadInt64(resp.Row.Families.First(f => f.Name == CF).Columns[0].Cells[0].Value).Should().Be(42);
    }

    [Fact]
    public async Task Increment_large_value()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-large",
            ReadModifyWriteRules.Increment(CF, "counter", long.MaxValue / 2));
        ReadInt64(resp.Row.Families.First(f => f.Name == CF).Columns[0].Cells[0].Value)
            .Should().Be(long.MaxValue / 2);
    }

    [Fact]
    public async Task Increment_multiple_columns()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-multi",
            ReadModifyWriteRules.Increment(CF, "a", 1),
            ReadModifyWriteRules.Increment(CF, "b", 2),
            ReadModifyWriteRules.Increment(CF, "c", 3));
        var cols = resp.Row.Families.First(f => f.Name == CF).Columns
            .ToDictionary(c => c.Qualifier.ToStringUtf8(), c => ReadInt64(c.Cells[0].Value));
        cols["a"].Should().Be(1);
        cols["b"].Should().Be(2);
        cols["c"].Should().Be(3);
    }

    [Fact]
    public async Task Increment_multiple_families()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-mf",
            ReadModifyWriteRules.Increment(CF, "counter", 10),
            ReadModifyWriteRules.Increment(CF2, "counter", 20));
        var cfVal = ReadInt64(resp.Row.Families.First(f => f.Name == CF).Columns[0].Cells[0].Value);
        var cf2Val = ReadInt64(resp.Row.Families.First(f => f.Name == CF2).Columns[0].Cells[0].Value);
        cfVal.Should().Be(10);
        cf2Val.Should().Be(20);
    }

    #endregion

    #region Append basics

    [Fact]
    public async Task Append_new_cell_creates_value()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-app-new",
            ReadModifyWriteRules.Append(CF, "msg", "hello"));
        resp.Row.Families.First(f => f.Name == CF).Columns[0].Cells[0].Value.ToStringUtf8()
            .Should().Be("hello");
    }

    [Fact]
    public async Task Append_concatenates()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmw-app-cat",
            ReadModifyWriteRules.Append(CF, "msg", "hello"));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-app-cat",
            ReadModifyWriteRules.Append(CF, "msg", " world"));
        resp.Row.Families.First(f => f.Name == CF).Columns[0].Cells[0].Value.ToStringUtf8()
            .Should().Be("hello world");
    }

    [Fact]
    public async Task Append_empty_is_noop()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmw-app-empty",
            ReadModifyWriteRules.Append(CF, "msg", "base"));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-app-empty",
            ReadModifyWriteRules.Append(CF, "msg", ByteString.Empty));
        resp.Row.Families.First(f => f.Name == CF).Columns[0].Cells[0].Value.ToStringUtf8()
            .Should().Be("base");
    }

    [Fact]
    public async Task Append_binary_data()
    {
        var bytes1 = new byte[] { 0x01, 0x02 };
        var bytes2 = new byte[] { 0x03, 0x04 };
        await Client.ReadModifyWriteRowAsync(TN, "rmw-app-bin",
            ReadModifyWriteRules.Append(CF, "data", ByteString.CopyFrom(bytes1)));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-app-bin",
            ReadModifyWriteRules.Append(CF, "data", ByteString.CopyFrom(bytes2)));
        resp.Row.Families.First(f => f.Name == CF).Columns[0].Cells[0].Value.ToByteArray()
            .Should().Equal(0x01, 0x02, 0x03, 0x04);
    }

    [Fact]
    public async Task Append_multiple_columns()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-app-mc",
            ReadModifyWriteRules.Append(CF, "a", "x"),
            ReadModifyWriteRules.Append(CF, "b", "y"),
            ReadModifyWriteRules.Append(CF, "c", "z"));
        var cols = resp.Row.Families.First(f => f.Name == CF).Columns
            .ToDictionary(c => c.Qualifier.ToStringUtf8(), c => c.Cells[0].Value.ToStringUtf8());
        cols["a"].Should().Be("x");
        cols["b"].Should().Be("y");
        cols["c"].Should().Be("z");
    }

    [Fact]
    public async Task Append_multiple_families()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-app-mf",
            ReadModifyWriteRules.Append(CF, "msg", "hello"),
            ReadModifyWriteRules.Append(CF2, "msg", "world"));
        resp.Row.Families.First(f => f.Name == CF).Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("hello");
        resp.Row.Families.First(f => f.Name == CF2).Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("world");
    }

    #endregion

    #region Mixed increment and append

    [Fact]
    public async Task Increment_and_append_same_call()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-mix",
            ReadModifyWriteRules.Increment(CF, "counter", 42),
            ReadModifyWriteRules.Append(CF, "msg", "hello"));
        var cols = resp.Row.Families.First(f => f.Name == CF).Columns
            .ToDictionary(c => c.Qualifier.ToStringUtf8(), c => c.Cells[0].Value);
        ReadInt64(cols["counter"]).Should().Be(42);
        cols["msg"].ToStringUtf8().Should().Be("hello");
    }

    [Fact]
    public async Task Increment_and_append_different_families()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-mix-mf",
            ReadModifyWriteRules.Increment(CF, "counter", 10),
            ReadModifyWriteRules.Append(CF2, "log", "entry1"));
        var cfCounter = ReadInt64(resp.Row.Families.First(f => f.Name == CF).Columns[0].Cells[0].Value);
        var cf2Log = resp.Row.Families.First(f => f.Name == CF2).Columns[0].Cells[0].Value.ToStringUtf8();
        cfCounter.Should().Be(10);
        cf2Log.Should().Be("entry1");
    }

    #endregion

    #region Response verification

    [Fact]
    public async Task Response_contains_modified_row()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-resp",
            ReadModifyWriteRules.Increment(CF, "counter", 7));
        resp.Row.Should().NotBeNull();
        resp.Row.Key.ToStringUtf8().Should().Be("rmw-resp");
        resp.Row.Families.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Response_has_single_cell_per_modified_column()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmw-resp-sc",
            ReadModifyWriteRules.Increment(CF, "counter", 1));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-resp-sc",
            ReadModifyWriteRules.Increment(CF, "counter", 1));
        // Response should show the final value in a single cell
        resp.Row.Families.First(f => f.Name == CF).Columns[0].Cells.Should().ContainSingle();
    }

    [Fact]
    public async Task Response_timestamp_is_server_assigned()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-resp-ts",
            ReadModifyWriteRules.Increment(CF, "counter", 1));
        var ts = resp.Row.Families.First(f => f.Name == CF).Columns[0].Cells[0].TimestampMicros;
        ts.Should().BeGreaterThan(0);
    }

    #endregion

    #region Consistency with reads

    [Fact]
    public async Task Increment_then_read_is_consistent()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmw-rd-cons",
            ReadModifyWriteRules.Increment(CF, "counter", 99));
        var rows = await ReadAll(RowSet.FromRowKeys("rmw-rd-cons"));
        rows.Should().ContainSingle();
        var val = ReadInt64(rows[0].Families.First(f => f.Name == CF).Columns[0].Cells[0].Value);
        val.Should().Be(99);
    }

    [Fact]
    public async Task Append_then_read_is_consistent()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmw-rd-app",
            ReadModifyWriteRules.Append(CF, "msg", "hello"));
        var rows = await ReadAll(RowSet.FromRowKeys("rmw-rd-app"));
        rows[0].Families.First(f => f.Name == CF).Columns[0].Cells[0].Value.ToStringUtf8()
            .Should().Be("hello");
    }

    [Fact]
    public async Task ReadModifyWrite_on_row_with_existing_non_rmw_data()
    {
        // Pre-write some data
        await Client.MutateRowAsync(TN, "rmw-pre",
            Mutations.SetCell(CF, "name", "Alice", new BigtableVersion(1000)));
        // RMW on different column
        await Client.ReadModifyWriteRowAsync(TN, "rmw-pre",
            ReadModifyWriteRules.Increment(CF, "counter", 1));
        // Both should coexist
        var rows = await ReadAll(RowSet.FromRowKeys("rmw-pre"));
        var cols = rows[0].Families.First(f => f.Name == CF).Columns
            .Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("name");
        cols.Should().Contain("counter");
    }

    #endregion

    #region Multiple rules same column

    [Fact]
    public async Task Two_appends_same_column_same_call()
    {
        // Ref: Multiple rules to the same column are applied in order
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-2app",
            ReadModifyWriteRules.Append(CF, "msg", "hello"),
            ReadModifyWriteRules.Append(CF, "msg", " world"));
        resp.Row.Families.First(f => f.Name == CF).Columns[0].Cells[0].Value.ToStringUtf8()
            .Should().Be("hello world");
    }

    [Fact]
    public async Task Two_increments_same_column_same_call()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-2inc",
            ReadModifyWriteRules.Increment(CF, "counter", 3),
            ReadModifyWriteRules.Increment(CF, "counter", 7));
        ReadInt64(resp.Row.Families.First(f => f.Name == CF).Columns[0].Cells[0].Value).Should().Be(10);
    }

    #endregion
}

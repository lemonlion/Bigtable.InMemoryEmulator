using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Stress tests for ReadModifyWrite with complex patterns.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readmodifywriterowrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadModifyWriteAdvancedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "rmw-adv";
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public ReadModifyWriteAdvancedTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
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

    private static long ReadInt64(ByteString value)
    {
        var bytes = value.ToByteArray();
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToInt64(bytes, 0);
    }

    #region Increment patterns

    [Fact]
    public async Task Increment_creates_new_cell()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwa-inc-new",
            ReadModifyWriteRules.Increment(CF, "counter", 1));
        ReadInt64(resp.Row.Families[0].Columns[0].Cells[0].Value).Should().Be(1);
    }

    [Fact]
    public async Task Increment_accumulates_5_times()
    {
        for (int i = 0; i < 5; i++)
            await Client.ReadModifyWriteRowAsync(TN, "rmwa-inc-5",
                ReadModifyWriteRules.Increment(CF, "counter", 10));
        var rows = await ReadAll(RowSet.FromRowKeys("rmwa-inc-5"));
        ReadInt64(rows[0].Families[0].Columns[0].Cells[0].Value).Should().Be(50);
    }

    [Fact]
    public async Task Increment_accumulates_100_times()
    {
        for (int i = 0; i < 100; i++)
            await Client.ReadModifyWriteRowAsync(TN, "rmwa-inc-100",
                ReadModifyWriteRules.Increment(CF, "counter", 1));
        var rows = await ReadAll(RowSet.FromRowKeys("rmwa-inc-100"));
        ReadInt64(rows[0].Families[0].Columns[0].Cells[0].Value).Should().Be(100);
    }

    [Fact]
    public async Task Increment_negative_value()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-inc-neg",
            ReadModifyWriteRules.Increment(CF, "counter", 100));
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-inc-neg",
            ReadModifyWriteRules.Increment(CF, "counter", -30));
        var rows = await ReadAll(RowSet.FromRowKeys("rmwa-inc-neg"));
        ReadInt64(rows[0].Families[0].Columns[0].Cells[0].Value).Should().Be(70);
    }

    [Fact]
    public async Task Increment_to_zero()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-inc-zero",
            ReadModifyWriteRules.Increment(CF, "counter", 50));
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-inc-zero",
            ReadModifyWriteRules.Increment(CF, "counter", -50));
        var rows = await ReadAll(RowSet.FromRowKeys("rmwa-inc-zero"));
        ReadInt64(rows[0].Families[0].Columns[0].Cells[0].Value).Should().Be(0);
    }

    [Fact]
    public async Task Increment_past_negative()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-inc-past",
            ReadModifyWriteRules.Increment(CF, "counter", -10));
        var rows = await ReadAll(RowSet.FromRowKeys("rmwa-inc-past"));
        ReadInt64(rows[0].Families[0].Columns[0].Cells[0].Value).Should().Be(-10);
    }

    [Fact]
    public async Task Increment_large_value()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-inc-lg",
            ReadModifyWriteRules.Increment(CF, "counter", long.MaxValue / 2));
        var rows = await ReadAll(RowSet.FromRowKeys("rmwa-inc-lg"));
        ReadInt64(rows[0].Families[0].Columns[0].Cells[0].Value).Should().Be(long.MaxValue / 2);
    }

    [Fact]
    public async Task Increment_multiple_columns()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-inc-mc",
            ReadModifyWriteRules.Increment(CF, "a", 1),
            ReadModifyWriteRules.Increment(CF, "b", 2),
            ReadModifyWriteRules.Increment(CF, "c", 3));
        var rows = await ReadAll(RowSet.FromRowKeys("rmwa-inc-mc"));
        var cols = rows[0].Families[0].Columns.OrderBy(c => c.Qualifier.ToStringUtf8()).ToList();
        ReadInt64(cols[0].Cells[0].Value).Should().Be(1); // a
        ReadInt64(cols[1].Cells[0].Value).Should().Be(2); // b
        ReadInt64(cols[2].Cells[0].Value).Should().Be(3); // c
    }

    [Fact]
    public async Task Increment_cross_family()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-inc-xf",
            ReadModifyWriteRules.Increment(CF, "counter", 10),
            ReadModifyWriteRules.Increment(CF2, "counter", 20));
        var rows = await ReadAll(RowSet.FromRowKeys("rmwa-inc-xf"));
        rows[0].Families.Should().HaveCount(2);
    }

    #endregion

    #region Append patterns

    [Fact]
    public async Task Append_creates_new_cell()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwa-app-new",
            ReadModifyWriteRules.Append(CF, "log", "hello"));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("hello");
    }

    [Fact]
    public async Task Append_concatenates()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-app-cat",
            ReadModifyWriteRules.Append(CF, "log", "hello"));
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-app-cat",
            ReadModifyWriteRules.Append(CF, "log", " world"));
        var rows = await ReadAll(RowSet.FromRowKeys("rmwa-app-cat"));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("hello world");
    }

    [Fact]
    public async Task Append_10_times()
    {
        for (int i = 0; i < 10; i++)
            await Client.ReadModifyWriteRowAsync(TN, "rmwa-app-10",
                ReadModifyWriteRules.Append(CF, "log", $"[{i}]"));
        var rows = await ReadAll(RowSet.FromRowKeys("rmwa-app-10"));
        var expected = string.Concat(Enumerable.Range(0, 10).Select(i => $"[{i}]"));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be(expected);
    }

    [Fact]
    public async Task Append_binary_data()
    {
        var b1 = new byte[] { 0x01, 0x02 };
        var b2 = new byte[] { 0x03, 0x04 };
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-app-bin",
            ReadModifyWriteRules.Append(CF, "data", ByteString.CopyFrom(b1)));
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-app-bin",
            ReadModifyWriteRules.Append(CF, "data", ByteString.CopyFrom(b2)));
        var rows = await ReadAll(RowSet.FromRowKeys("rmwa-app-bin"));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToByteArray()
            .Should().Equal(0x01, 0x02, 0x03, 0x04);
    }

    [Fact]
    public async Task Append_empty_string()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-app-empty",
            ReadModifyWriteRules.Append(CF, "log", "hello"));
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-app-empty",
            ReadModifyWriteRules.Append(CF, "log", ""));
        var rows = await ReadAll(RowSet.FromRowKeys("rmwa-app-empty"));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("hello");
    }

    [Fact]
    public async Task Append_multiple_columns()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-app-mco",
            ReadModifyWriteRules.Append(CF, "a", "A"),
            ReadModifyWriteRules.Append(CF, "b", "B"),
            ReadModifyWriteRules.Append(CF, "c", "C"));
        var rows = await ReadAll(RowSet.FromRowKeys("rmwa-app-mco"));
        rows[0].Families[0].Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task Append_cross_family()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-app-xf",
            ReadModifyWriteRules.Append(CF, "log", "cf1"),
            ReadModifyWriteRules.Append(CF2, "log", "cf2"));
        var rows = await ReadAll(RowSet.FromRowKeys("rmwa-app-xf"));
        rows[0].Families.Should().HaveCount(2);
    }

    #endregion

    #region Mixed increment and append

    [Fact]
    public async Task Mixed_increment_and_append_same_row()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-mix",
            ReadModifyWriteRules.Increment(CF, "counter", 42),
            ReadModifyWriteRules.Append(CF, "log", "event1"));
        var rows = await ReadAll(RowSet.FromRowKeys("rmwa-mix"));
        var cols = rows[0].Families[0].Columns.OrderBy(c => c.Qualifier.ToStringUtf8()).ToList();
        ReadInt64(cols.First(c => c.Qualifier.ToStringUtf8() == "counter").Cells[0].Value).Should().Be(42);
        cols.First(c => c.Qualifier.ToStringUtf8() == "log").Cells[0].Value.ToStringUtf8().Should().Be("event1");
    }

    [Fact]
    public async Task Mixed_cross_family()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-mix-xf",
            ReadModifyWriteRules.Increment(CF, "counter", 10),
            ReadModifyWriteRules.Append(CF2, "log", "started"));
        var rows = await ReadAll(RowSet.FromRowKeys("rmwa-mix-xf"));
        rows[0].Families.Should().HaveCount(2);
    }

    #endregion

    #region Response verification

    [Fact]
    public async Task Increment_response_contains_new_value()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-resp-inc",
            ReadModifyWriteRules.Increment(CF, "counter", 10));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwa-resp-inc",
            ReadModifyWriteRules.Increment(CF, "counter", 5));
        ReadInt64(resp.Row.Families[0].Columns[0].Cells[0].Value).Should().Be(15);
    }

    [Fact]
    public async Task Append_response_contains_full_value()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-resp-app",
            ReadModifyWriteRules.Append(CF, "log", "first"));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwa-resp-app",
            ReadModifyWriteRules.Append(CF, "log", "-second"));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("first-second");
    }

    [Fact]
    public async Task Response_row_key_matches()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwa-resp-key",
            ReadModifyWriteRules.Increment(CF, "counter", 1));
        resp.Row.Key.ToStringUtf8().Should().Be("rmwa-resp-key");
    }

    [Fact]
    public async Task Response_has_correct_family()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwa-resp-fam",
            ReadModifyWriteRules.Increment(CF, "counter", 1));
        resp.Row.Families.Should().ContainSingle().Which.Name.Should().Be("cf");
    }

    [Fact]
    public async Task Multi_rule_response_contains_all_columns()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwa-resp-multi",
            ReadModifyWriteRules.Increment(CF, "a", 1),
            ReadModifyWriteRules.Increment(CF, "b", 2),
            ReadModifyWriteRules.Append(CF, "c", "x"));
        resp.Row.Families[0].Columns.Should().HaveCount(3);
    }

    #endregion

    #region Consistency after RMW

    [Fact]
    public async Task Increment_then_read_consistent()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-cons-inc",
            ReadModifyWriteRules.Increment(CF, "counter", 42));
        var rows = await ReadAll(RowSet.FromRowKeys("rmwa-cons-inc"));
        ReadInt64(rows[0].Families[0].Columns[0].Cells[0].Value).Should().Be(42);
    }

    [Fact]
    public async Task Append_then_read_consistent()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-cons-app",
            ReadModifyWriteRules.Append(CF, "log", "hello"));
        var rows = await ReadAll(RowSet.FromRowKeys("rmwa-cons-app"));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("hello");
    }

    [Fact]
    public async Task RMW_then_mutate_then_read()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-cons-mix",
            ReadModifyWriteRules.Increment(CF, "counter", 10));
        await Client.MutateRowAsync(TN, "rmwa-cons-mix",
            Mutations.SetCell(CF, "other", "val", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("rmwa-cons-mix"));
        rows[0].Families[0].Columns.Should().HaveCount(2);
        var counter = rows[0].Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "counter");
        ReadInt64(counter.Cells[0].Value).Should().Be(10);
    }

    [Fact]
    public async Task Mutate_then_RMW_on_same_column()
    {
        // Write an initial int64 value
        var initBytes = new byte[8];
        if (BitConverter.IsLittleEndian) Array.Reverse(initBytes);
        await Client.MutateRowAsync(TN, "rmwa-cons-same",
            Mutations.SetCell(CF, "counter",
                ByteString.CopyFrom(new byte[8]), new BigtableVersion(1000)));
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-cons-same",
            ReadModifyWriteRules.Increment(CF, "counter", 5));
        var rows = await ReadAll(RowSet.FromRowKeys("rmwa-cons-same"));
        ReadInt64(rows[0].Families[0].Columns[0].Cells[0].Value).Should().Be(5);
    }

    #endregion

    #region Edge cases

    [Fact]
    public async Task RMW_on_nonexistent_row_creates_it()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-edge-create",
            ReadModifyWriteRules.Increment(CF, "counter", 1));
        var rows = await ReadAll(RowSet.FromRowKeys("rmwa-edge-create"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task RMW_preserves_other_columns()
    {
        await Client.MutateRowAsync(TN, "rmwa-edge-preserve",
            Mutations.SetCell(CF, "name", "test", new BigtableVersion(1000)));
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-edge-preserve",
            ReadModifyWriteRules.Increment(CF, "counter", 1));
        var rows = await ReadAll(RowSet.FromRowKeys("rmwa-edge-preserve"));
        rows[0].Families[0].Columns.Should().HaveCount(2);
    }

    [Fact]
    public async Task RMW_preserves_other_families()
    {
        await Client.MutateRowAsync(TN, "rmwa-edge-fam",
            Mutations.SetCell(CF2, "data", "keep", new BigtableVersion(1000)));
        await Client.ReadModifyWriteRowAsync(TN, "rmwa-edge-fam",
            ReadModifyWriteRules.Increment(CF, "counter", 1));
        var rows = await ReadAll(RowSet.FromRowKeys("rmwa-edge-fam"));
        rows[0].Families.Should().HaveCount(2);
    }

    #endregion
}

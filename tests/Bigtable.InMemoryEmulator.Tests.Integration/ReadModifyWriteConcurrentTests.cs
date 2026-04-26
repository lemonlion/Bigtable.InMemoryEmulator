using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for ReadModifyWriteRow concurrent/sequential scenarios, atomicity checks,
/// and multi-rule combinations.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readmodifywriterowrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadModifyWriteConcurrentTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "rmwc-tests";
    private const string CF = "cf";

    public ReadModifyWriteConcurrentTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Sequential_increments_accumulate()
    {
        var rk = new BigtableByteString("rmwc-seq");
        for (int i = 0; i < 10; i++)
        {
            await Client.ReadModifyWriteRowAsync(TN, rk,
                ReadModifyWriteRules.Increment(CF, "counter", 1));
        }

        var row = await Client.ReadRowAsync(TN, rk);
        var val = BitConverter.ToInt64(row!.Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(10);
    }

    [Fact]
    public async Task Sequential_appends_concatenate()
    {
        var rk = new BigtableByteString("rmwc-append");
        await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF, "data", "hello"));
        await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF, "data", " world"));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("hello world");
    }

    [Fact]
    public async Task Increment_on_nonexistent_row_creates_it()
    {
        var rk = new BigtableByteString("rmwc-noncreate");
        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "counter", 5));

        resp.Row.Should().NotBeNull();
        var val = BitConverter.ToInt64(resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(5);
    }

    [Fact]
    public async Task Append_on_nonexistent_row_creates_it()
    {
        var rk = new BigtableByteString("rmwc-appcreate");
        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF, "data", "initial"));

        resp.Row.Should().NotBeNull();
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("initial");
    }

    [Fact]
    public async Task Increment_negative_value_decrements()
    {
        var rk = new BigtableByteString("rmwc-neg");
        await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "counter", 10));
        await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "counter", -3));

        var row = await Client.ReadRowAsync(TN, rk);
        var val = BitConverter.ToInt64(row!.Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(7);
    }

    [Fact]
    public async Task Increment_by_zero_is_noop_value()
    {
        var rk = new BigtableByteString("rmwc-zero");
        await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "counter", 5));
        await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "counter", 0));

        var row = await Client.ReadRowAsync(TN, rk);
        var val = BitConverter.ToInt64(row!.Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(5);
    }

    [Fact]
    public async Task Multi_rule_increment_and_append_same_request()
    {
        var rk = new BigtableByteString("rmwc-multi");
        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "count", 1),
            ReadModifyWriteRules.Append(CF, "log", "entry1"));

        var columns = resp.Row.Families[0].Columns.ToDictionary(c => c.Qualifier.ToStringUtf8());
        columns.Should().ContainKey("count");
        columns.Should().ContainKey("log");
    }

    [Fact]
    public async Task Multi_rule_two_columns_independent()
    {
        var rk = new BigtableByteString("rmwc-2col");
        await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "a", 1),
            ReadModifyWriteRules.Increment(CF, "b", 2));

        var row = await Client.ReadRowAsync(TN, rk);
        var cols = row!.Families[0].Columns.ToDictionary(c => c.Qualifier.ToStringUtf8());
        var aVal = BitConverter.ToInt64(cols["a"].Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        var bVal = BitConverter.ToInt64(cols["b"].Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        aVal.Should().Be(1);
        bVal.Should().Be(2);
    }

    [Fact]
    public async Task Append_empty_string_creates_empty_value()
    {
        var rk = new BigtableByteString("rmwc-appempty");
        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF, "data", ""));

        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().BeEmpty();
    }

    [Fact]
    public async Task Append_binary_data()
    {
        var rk = new BigtableByteString("rmwc-appbin");
        var bytes = ByteString.CopyFrom(0x01, 0x02, 0x03);
        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF, "data", bytes));

        resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray().Should().BeEquivalentTo(new byte[] { 0x01, 0x02, 0x03 });
    }

    [Fact]
    public async Task Append_binary_data_accumulates()
    {
        var rk = new BigtableByteString("rmwc-appbinacc");
        await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF, "data", ByteString.CopyFrom(0x01, 0x02)));
        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF, "data", ByteString.CopyFrom(0x03, 0x04)));

        resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray()
            .Should().BeEquivalentTo(new byte[] { 0x01, 0x02, 0x03, 0x04 });
    }

    [Fact]
    public async Task Increment_large_value()
    {
        var rk = new BigtableByteString("rmwc-large");
        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "counter", long.MaxValue / 2));

        var val = BitConverter.ToInt64(resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(long.MaxValue / 2);
    }

    [Fact]
    public async Task Response_contains_row_key()
    {
        var rk = new BigtableByteString("rmwc-respkey");
        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "c", 1));

        resp.Row.Key.ToStringUtf8().Should().Be("rmwc-respkey");
    }

    [Fact]
    public async Task Response_contains_updated_value()
    {
        var rk = new BigtableByteString("rmwc-respval");
        await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "c", 5));

        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "c", 3));

        var val = BitConverter.ToInt64(resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(8);
    }

    [Fact]
    public async Task Append_to_existing_non_append_value()
    {
        var rk = new BigtableByteString("rmwc-appexist");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "data", "base", new BigtableVersion(1000)));

        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF, "data", "-extra"));

        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("base-extra");
    }

    [Fact]
    public async Task Multi_rule_across_two_families()
    {
        var rk = new BigtableByteString("rmwc-2fam");
        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "count", 1),
            ReadModifyWriteRules.Append("cf2", "log", "entry"));

        resp.Row.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Increment_then_read_consistency()
    {
        var rk = new BigtableByteString("rmwc-readcons");
        await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "counter", 42));

        var row = await Client.ReadRowAsync(TN, rk);
        var val = BitConverter.ToInt64(row!.Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(42);
    }

    [Fact]
    public async Task Multiple_sequential_appends()
    {
        var rk = new BigtableByteString("rmwc-seqapp");
        for (int i = 0; i < 5; i++)
        {
            await Client.ReadModifyWriteRowAsync(TN, rk,
                ReadModifyWriteRules.Append(CF, "data", $"{i}"));
        }

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("01234");
    }

    [Fact]
    public async Task Concurrent_increments_all_counted()
    {
        var rk = new BigtableByteString("rmwc-conc");
        var tasks = Enumerable.Range(0, 10).Select(_ =>
            Client.ReadModifyWriteRowAsync(TN, rk,
                ReadModifyWriteRules.Increment(CF, "counter", 1)));

        await Task.WhenAll(tasks);

        var row = await Client.ReadRowAsync(TN, rk);
        var val = BitConverter.ToInt64(row!.Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(10);
    }

    [Fact]
    public async Task Concurrent_appends_all_appended()
    {
        var rk = new BigtableByteString("rmwc-concapp");
        var tasks = Enumerable.Range(0, 5).Select(i =>
            Client.ReadModifyWriteRowAsync(TN, rk,
                ReadModifyWriteRules.Append(CF, "data", "x")));

        await Task.WhenAll(tasks);

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().HaveLength(5);
    }

    [Fact]
    public async Task Append_large_value()
    {
        var rk = new BigtableByteString("rmwc-applarge");
        var largeStr = new string('x', 10000);
        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF, "data", largeStr));

        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().HaveLength(10000);
    }

    [Fact]
    public async Task Increment_creates_big_endian_8_byte_value()
    {
        var rk = new BigtableByteString("rmwc-endian");
        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "counter", 1));

        var bytes = resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray();
        bytes.Should().HaveCount(8); // 64-bit big-endian
    }

    [Fact]
    public async Task Multiple_increments_same_column_same_request()
    {
        var rk = new BigtableByteString("rmwc-samecol");
        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "counter", 3),
            ReadModifyWriteRules.Increment(CF, "counter", 7));

        var val = BitConverter.ToInt64(resp.Row.Families[0].Columns
            .First(c => c.Qualifier.ToStringUtf8() == "counter")
            .Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(10);
    }

    [Fact]
    public async Task Multiple_appends_same_column_same_request()
    {
        var rk = new BigtableByteString("rmwc-samecolapp");
        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF, "data", "hello"),
            ReadModifyWriteRules.Append(CF, "data", " world"));

        resp.Row.Families[0].Columns
            .First(c => c.Qualifier.ToStringUtf8() == "data")
            .Cells[0].Value.ToStringUtf8().Should().Be("hello world");
    }
}

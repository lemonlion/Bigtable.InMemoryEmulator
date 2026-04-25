using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for ReadModifyWriteRow with multiple rules, edge cases, and
/// complex increment/append interactions.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readmodifywriterowrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadModifyWriteMultiRuleTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "rmw-multi-tests";
    private const string CF = "cf";

    public ReadModifyWriteMultiRuleTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readmodifywriterowrequest
    //   "Rules are applied in order."
    [Fact]
    public async Task Two_increment_rules_same_column_applied_in_order()
    {
        var rk = new BigtableByteString("rmw-m-2inc");

        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "counter", 10),
            ReadModifyWriteRules.Increment(CF, "counter", 5));

        var row = resp.Row;
        // The final value should be the combined result (10 + 5 = 15)
        var cell = row.Families.SelectMany(f => f.Columns)
            .First(c => c.Qualifier.ToStringUtf8() == "counter")
            .Cells[0];
        var val = BitConverter.ToInt64(cell.Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(15);
    }

    [Fact]
    public async Task Two_append_rules_same_column()
    {
        var rk = new BigtableByteString("rmw-m-2app");

        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF, "data", "hello"),
            ReadModifyWriteRules.Append(CF, "data", " world"));

        var val = resp.Row.Families.SelectMany(f => f.Columns)
            .First(c => c.Qualifier.ToStringUtf8() == "data")
            .Cells[0].Value.ToStringUtf8();
        val.Should().Be("hello world");
    }

    [Fact]
    public async Task Increment_and_append_different_columns()
    {
        var rk = new BigtableByteString("rmw-m-mixed");

        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "count", 42),
            ReadModifyWriteRules.Append(CF, "log", "entry1"));

        var cols = resp.Row.Families.SelectMany(f => f.Columns).ToList();
        cols.Should().HaveCount(2);
    }

    [Fact]
    public async Task Three_increments_accumulate()
    {
        var rk = new BigtableByteString("rmw-m-3inc");

        await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "c", 10));
        await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "c", 20));
        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "c", 12));

        var val = BitConverter.ToInt64(
            resp.Row.Families.SelectMany(f => f.Columns)
                .First(c => c.Qualifier.ToStringUtf8() == "c")
                .Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(42);
    }

    [Fact]
    public async Task Three_appends_concatenate()
    {
        var rk = new BigtableByteString("rmw-m-3app");

        await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF, "msg", "a"));
        await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF, "msg", "b"));
        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF, "msg", "c"));

        var val = resp.Row.Families.SelectMany(f => f.Columns)
            .First(c => c.Qualifier.ToStringUtf8() == "msg")
            .Cells[0].Value.ToStringUtf8();
        val.Should().Be("abc");
    }

    [Fact]
    public async Task Increment_negative_value()
    {
        var rk = new BigtableByteString("rmw-m-neg");

        await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "c", 100));
        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "c", -30));

        var val = BitConverter.ToInt64(
            resp.Row.Families.SelectMany(f => f.Columns)
                .First(c => c.Qualifier.ToStringUtf8() == "c")
                .Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(70);
    }

    [Fact]
    public async Task Increment_on_nonexistent_row_starts_from_zero()
    {
        var rk = new BigtableByteString("rmw-m-new");

        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "c", 7));

        var val = BitConverter.ToInt64(
            resp.Row.Families.SelectMany(f => f.Columns)
                .First(c => c.Qualifier.ToStringUtf8() == "c")
                .Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(7);
    }

    [Fact]
    public async Task Append_on_nonexistent_row_creates_row()
    {
        var rk = new BigtableByteString("rmw-m-newapp");

        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF, "data", "first"));

        var val = resp.Row.Families.SelectMany(f => f.Columns)
            .First(c => c.Qualifier.ToStringUtf8() == "data")
            .Cells[0].Value.ToStringUtf8();
        val.Should().Be("first");
    }

    [Fact]
    public async Task Append_empty_bytes_is_noop_value()
    {
        var rk = new BigtableByteString("rmw-m-emptyapp");

        await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF, "data", "hello"));
        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF, "data", ""));

        var val = resp.Row.Families.SelectMany(f => f.Columns)
            .First(c => c.Qualifier.ToStringUtf8() == "data")
            .Cells[0].Value.ToStringUtf8();
        val.Should().Be("hello");
    }

    [Fact]
    public async Task Increment_zero_returns_current_value()
    {
        var rk = new BigtableByteString("rmw-m-zero");

        await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "c", 50));
        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "c", 0));

        var val = BitConverter.ToInt64(
            resp.Row.Families.SelectMany(f => f.Columns)
                .First(c => c.Qualifier.ToStringUtf8() == "c")
                .Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(50);
    }

    [Fact]
    public async Task Multiple_rules_across_families()
    {
        var rk = new BigtableByteString("rmw-m-xfam");

        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "count", 1),
            ReadModifyWriteRules.Append("cf2", "log", "event"));

        resp.Row.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Response_contains_modified_row()
    {
        var rk = new BigtableByteString("rmw-m-resp");

        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF, "data", "test"));

        resp.Row.Key.ToStringUtf8().Should().Be("rmw-m-resp");
        resp.Row.Families.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Increment_large_positive_value()
    {
        var rk = new BigtableByteString("rmw-m-large");

        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "c", long.MaxValue / 2));

        var val = BitConverter.ToInt64(
            resp.Row.Families.SelectMany(f => f.Columns)
                .First(c => c.Qualifier.ToStringUtf8() == "c")
                .Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(long.MaxValue / 2);
    }

    [Fact]
    public async Task Increment_large_negative_value()
    {
        var rk = new BigtableByteString("rmw-m-lneg");

        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "c", long.MinValue / 2));

        var val = BitConverter.ToInt64(
            resp.Row.Families.SelectMany(f => f.Columns)
                .First(c => c.Qualifier.ToStringUtf8() == "c")
                .Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(long.MinValue / 2);
    }

    [Fact]
    public async Task Append_binary_data()
    {
        var rk = new BigtableByteString("rmw-m-bin");
        var bytes = new byte[] { 0x00, 0xFF, 0x01, 0xFE };

        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF, ByteString.CopyFromUtf8("bin"), ByteString.CopyFrom(bytes)));

        resp.Row.Families.SelectMany(f => f.Columns)
            .First(c => c.Qualifier.ToStringUtf8() == "bin")
            .Cells[0].Value.ToByteArray()
            .Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public async Task Append_then_read_preserves_value()
    {
        var rk = new BigtableByteString("rmw-m-read");

        await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF, "data", "stored"));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.SelectMany(f => f.Columns)
            .First(c => c.Qualifier.ToStringUtf8() == "data")
            .Cells[0].Value.ToStringUtf8().Should().Be("stored");
    }

    [Fact]
    public async Task Increment_then_read_preserves_value()
    {
        var rk = new BigtableByteString("rmw-m-incread");

        await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "c", 99));

        var row = await Client.ReadRowAsync(TN, rk);
        var val = BitConverter.ToInt64(
            row!.Families.SelectMany(f => f.Columns)
                .First(c => c.Qualifier.ToStringUtf8() == "c")
                .Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(99);
    }

    [Fact]
    public async Task Five_rules_in_single_request()
    {
        var rk = new BigtableByteString("rmw-m-5rules");

        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "a", 1),
            ReadModifyWriteRules.Increment(CF, "b", 2),
            ReadModifyWriteRules.Append(CF, "c", "x"),
            ReadModifyWriteRules.Append(CF, "d", "y"),
            ReadModifyWriteRules.Increment("cf2", "e", 3));

        var allCols = resp.Row.Families.SelectMany(f => f.Columns)
            .Select(c => c.Qualifier.ToStringUtf8()).ToList();
        allCols.Should().Contain("a");
        allCols.Should().Contain("b");
        allCols.Should().Contain("c");
        allCols.Should().Contain("d");
        allCols.Should().Contain("e");
    }
}

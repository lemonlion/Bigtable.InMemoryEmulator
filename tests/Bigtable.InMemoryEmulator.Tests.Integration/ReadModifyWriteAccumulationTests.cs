using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for ReadModifyWriteRow edge cases: accumulation, binary data, negative increments.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readmodifywriterowrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadModifyWriteAccumulationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "rmw-accum";

    public ReadModifyWriteAccumulationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task Increment_on_nonexistent_row_creates_row()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readmodifywriterowrequest
        //   "If the specified row does not exist, one will be created"
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-a1",
            ReadModifyWriteRules.Increment(CF, "counter", 5));
        var cell = resp.Row.Families[0].Columns[0].Cells[0];
        cell.Value.Span.ToArray().Should().BeEquivalentTo(
            BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(5L)));
    }

    [Fact]
    public async Task Triple_increment_accumulates()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmw-a2",
            ReadModifyWriteRules.Increment(CF, "counter", 10));
        await Client.ReadModifyWriteRowAsync(TN, "rmw-a2",
            ReadModifyWriteRules.Increment(CF, "counter", 7));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-a2",
            ReadModifyWriteRules.Increment(CF, "counter", 3));
        var val = System.Net.IPAddress.NetworkToHostOrder(
            BitConverter.ToInt64(resp.Row.Families[0].Columns[0].Cells[0].Value.Span));
        val.Should().Be(20);
    }

    [Fact]
    public async Task Append_three_times_concatenates()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmw-a3",
            ReadModifyWriteRules.Append(CF, "data", "one"));
        await Client.ReadModifyWriteRowAsync(TN, "rmw-a3",
            ReadModifyWriteRules.Append(CF, "data", "-two"));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-a3",
            ReadModifyWriteRules.Append(CF, "data", "-three"));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8()
            .Should().Be("one-two-three");
    }

    [Fact]
    public async Task Increment_negative_to_zero()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmw-a4",
            ReadModifyWriteRules.Increment(CF, "counter", 50));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-a4",
            ReadModifyWriteRules.Increment(CF, "counter", -50));
        var val = System.Net.IPAddress.NetworkToHostOrder(
            BitConverter.ToInt64(resp.Row.Families[0].Columns[0].Cells[0].Value.Span));
        val.Should().Be(0);
    }

    [Fact]
    public async Task Increment_negative_past_zero()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmw-a5",
            ReadModifyWriteRules.Increment(CF, "counter", 10));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-a5",
            ReadModifyWriteRules.Increment(CF, "counter", -20));
        var val = System.Net.IPAddress.NetworkToHostOrder(
            BitConverter.ToInt64(resp.Row.Families[0].Columns[0].Cells[0].Value.Span));
        val.Should().Be(-10);
    }

    [Fact]
    public async Task Append_binary_data_concatenates()
    {
        var data1 = ByteString.CopyFrom(new byte[] { 0x01, 0x02 });
        var data2 = ByteString.CopyFrom(new byte[] { 0x03, 0x04 });
        await Client.ReadModifyWriteRowAsync(TN, "rmw-a6",
            ReadModifyWriteRules.Append(CF, ByteString.CopyFromUtf8("bin"), data1));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-a6",
            ReadModifyWriteRules.Append(CF, ByteString.CopyFromUtf8("bin"), data2));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray()
            .Should().BeEquivalentTo(new byte[] { 0x01, 0x02, 0x03, 0x04 });
    }

    [Fact]
    public async Task Multiple_rules_different_columns_atomic()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readmodifywriterowrequest
        //   "rules: Required. Rules specifying atomically the various cell values to be modified."
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-a7",
            ReadModifyWriteRules.Append(CF, "name", "Alice"),
            ReadModifyWriteRules.Increment(CF, "score", 100));
        var cols = resp.Row.Families[0].Columns.OrderBy(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().HaveCount(2);
    }

    [Fact]
    public async Task Response_row_key_matches_request()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-a8",
            ReadModifyWriteRules.Increment(CF, "c", 1));
        resp.Row.Key.ToStringUtf8().Should().Be("rmw-a8");
    }

    [Fact]
    public async Task Response_timestamp_is_server_assigned()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readmodifywriterowresponse
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-a9",
            ReadModifyWriteRules.Increment(CF, "ts-check", 1));
        resp.Row.Families[0].Columns[0].Cells[0].TimestampMicros.Should().BeGreaterThan(0);
        (resp.Row.Families[0].Columns[0].Cells[0].TimestampMicros % 1000).Should().Be(0);
    }

    [Fact]
    public async Task Increment_large_value()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-a10",
            ReadModifyWriteRules.Increment(CF, "big", long.MaxValue / 2));
        var val = System.Net.IPAddress.NetworkToHostOrder(
            BitConverter.ToInt64(resp.Row.Families[0].Columns[0].Cells[0].Value.Span));
        val.Should().Be(long.MaxValue / 2);
    }

    [Fact]
    public async Task Append_empty_on_existing_is_noop()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmw-a11",
            ReadModifyWriteRules.Append(CF, "data", "base"));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-a11",
            ReadModifyWriteRules.Append(CF, "data", ""));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("base");
    }

    [Fact]
    public async Task Append_on_nonexistent_row_creates_row()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-a12",
            ReadModifyWriteRules.Append(CF, "data", "hello"));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("hello");
    }
}

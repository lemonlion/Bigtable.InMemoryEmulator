using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Advanced ReadModifyWriteRow integration tests — multiple rules, edge cases,
/// interaction with existing data.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readmodifywriterowrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadModifyWriteAdvancedIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "rmw-adv-tests";
    private const string CF = "cf";

    public ReadModifyWriteAdvancedIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task ReadModifyWrite_multiple_increment_rules()
    {
        // Multiple rules in a single call — each targets a different column
        var rk = new BigtableByteString("rmw-multi");
        var response = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "counter1", 10),
            ReadModifyWriteRules.Increment(CF, "counter2", 20));
        response.Should().NotBeNull();
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        var cols = row!.Families[0].Columns.ToDictionary(
            c => c.Qualifier.ToStringUtf8(),
            c => System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(c.Cells[0].Value.ToByteArray()));
        cols["counter1"].Should().Be(10);
        cols["counter2"].Should().Be(20);
    }

    [Fact]
    public async Task ReadModifyWrite_increment_and_append_same_call()
    {
        // Ref: Rules are applied in order
        var rk = new BigtableByteString("rmw-inc-app");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "text", "hello", new BigtableVersion(1000)));
        await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "count", 1),
            ReadModifyWriteRules.Append(CF, "text", " world"));
        var row = await Client.ReadRowAsync(TN, rk);
        var cols = row!.Families[0].Columns.ToDictionary(c => c.Qualifier.ToStringUtf8());
        cols["text"].Cells[0].Value.ToStringUtf8().Should().Be("hello world");
        var countVal = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(cols["count"].Cells[0].Value.ToByteArray());
        countVal.Should().Be(1);
    }

    [Fact]
    public async Task ReadModifyWrite_increment_on_nonexistent_cell_starts_at_zero()
    {
        // Ref: "If the targeted cell is unset, it will be treated as containing a zero."
        var rk = new BigtableByteString("rmw-inc-new");
        var response = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "counter", 42));
        var row = await Client.ReadRowAsync(TN, rk);
        var value = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(
            row!.Families[0].Columns[0].Cells[0].Value.ToByteArray());
        value.Should().Be(42);
    }

    [Fact]
    public async Task ReadModifyWrite_append_on_nonexistent_cell_creates_new()
    {
        // Ref: "If the targeted cell is unset, it will be treated as containing the empty string."
        var rk = new BigtableByteString("rmw-app-new");
        await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF, "text", "initial"));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("initial");
    }

    [Fact]
    public async Task ReadModifyWrite_multiple_appends()
    {
        var rk = new BigtableByteString("rmw-multi-app");
        await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF, "log", "a"));
        await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF, "log", "b"));
        await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF, "log", "c"));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("abc");
    }

    [Fact]
    public async Task ReadModifyWrite_negative_increment()
    {
        var rk = new BigtableByteString("rmw-neg");
        await Client.ReadModifyWriteRowAsync(TN, rk, ReadModifyWriteRules.Increment(CF, "x", 100));
        await Client.ReadModifyWriteRowAsync(TN, rk, ReadModifyWriteRules.Increment(CF, "x", -30));
        var row = await Client.ReadRowAsync(TN, rk);
        var value = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(
            row!.Families[0].Columns[0].Cells[0].Value.ToByteArray());
        value.Should().Be(70);
    }

    [Fact]
    public async Task ReadModifyWrite_returns_modified_row()
    {
        // Ref: Response contains the row after applying the mutations
        var rk = new BigtableByteString("rmw-resp");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "existing", "keep", new BigtableVersion(1000)));
        var response = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF, "counter", 5));
        response.Should().NotBeNull();
        response.Row.Should().NotBeNull();
        response.Row.Key.ToStringUtf8().Should().Be("rmw-resp");
    }

    [Fact]
    public async Task ReadModifyWrite_append_binary_data()
    {
        var rk = new BigtableByteString("rmw-bin");
        var initial = new byte[] { 0x01, 0x02 };
        var append = new byte[] { 0x03, 0x04 };
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "bin", ByteString.CopyFrom(initial), new BigtableVersion(1000)));
        await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF, "bin", ByteString.CopyFrom(append)));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells[0].Value.ToByteArray()
            .Should().Equal(0x01, 0x02, 0x03, 0x04);
    }
}

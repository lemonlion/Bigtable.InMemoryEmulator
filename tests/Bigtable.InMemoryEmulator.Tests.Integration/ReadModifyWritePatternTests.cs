using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for ReadModifyWrite Append and Increment operations with various patterns.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readmodifywriterowrequest
///   "Modifies a row atomically on the server side."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadModifyWritePatternTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "rmw-pat";

    public ReadModifyWritePatternTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task Append_to_nonexistent_cell_creates_it()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rp-r1",
            ReadModifyWriteRules.Append(CF, "c", "hello"));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("hello");
    }

    [Fact]
    public async Task Append_to_existing_cell_concatenates()
    {
        await Client.MutateRowAsync(TN, "rp-r2",
            Mutations.SetCell(CF, "c", "hello", new BigtableVersion(1000)));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rp-r2",
            ReadModifyWriteRules.Append(CF, "c", " world"));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("hello world");
    }

    [Fact]
    public async Task Multiple_appends_same_column()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rp-r3",
            ReadModifyWriteRules.Append(CF, "c", "a"),
            ReadModifyWriteRules.Append(CF, "c", "b"),
            ReadModifyWriteRules.Append(CF, "c", "c"));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("abc");
    }

    [Fact]
    public async Task Increment_on_nonexistent_cell_starts_from_zero()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rp-r4",
            ReadModifyWriteRules.Increment(CF, "c", 42));
        var val = BitConverter.ToInt64(resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(42);
    }

    [Fact]
    public async Task Increment_adds_to_existing()
    {
        // Seed with big-endian 8-byte int
        var bytes = BitConverter.GetBytes((long)100).Reverse().ToArray();
        await Client.MutateRowAsync(TN, "rp-r5",
            Mutations.SetCell(CF, "c", ByteString.CopyFrom(bytes), new BigtableVersion(1000)));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rp-r5",
            ReadModifyWriteRules.Increment(CF, "c", 50));
        var val = BitConverter.ToInt64(resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(150);
    }

    [Fact]
    public async Task Increment_negative()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rp-r6",
            ReadModifyWriteRules.Increment(CF, "c", 100));
        resp = await Client.ReadModifyWriteRowAsync(TN, "rp-r6",
            ReadModifyWriteRules.Increment(CF, "c", -60));
        var val = BitConverter.ToInt64(resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(40);
    }

    [Fact]
    public async Task Append_and_increment_different_columns()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rp-r7",
            ReadModifyWriteRules.Append(CF, "name", "test"),
            ReadModifyWriteRules.Increment(CF, "count", 1));
        var cols = resp.Row.Families[0].Columns.OrderBy(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().HaveCount(2);
    }

    [Fact]
    public async Task Multiple_increments_same_column()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rp-r8",
            ReadModifyWriteRules.Increment(CF, "c", 10),
            ReadModifyWriteRules.Increment(CF, "c", 20),
            ReadModifyWriteRules.Increment(CF, "c", 30));
        var val = BitConverter.ToInt64(resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(60);
    }

    [Fact]
    public async Task Append_binary_data()
    {
        var initial = ByteString.CopyFrom(new byte[] { 0x01, 0x02 });
        await Client.MutateRowAsync(TN, "rp-r9",
            Mutations.SetCell(CF, "c", initial, new BigtableVersion(1000)));
        var appendData = ByteString.CopyFrom(new byte[] { 0x03, 0x04 });
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rp-r9",
            ReadModifyWriteRules.Append(CF, "c", appendData));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray()
            .Should().BeEquivalentTo(new byte[] { 0x01, 0x02, 0x03, 0x04 });
    }

    [Fact]
    public async Task Append_empty_string_is_noop()
    {
        await Client.MutateRowAsync(TN, "rp-r10",
            Mutations.SetCell(CF, "c", "original", new BigtableVersion(1000)));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rp-r10",
            ReadModifyWriteRules.Append(CF, "c", ""));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("original");
    }

    [Fact]
    public async Task Increment_returns_updated_row()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rp-r11",
            ReadModifyWriteRules.Increment(CF, "counter", 5));
        resp.Row.Key.ToStringUtf8().Should().Be("rp-r11");
        resp.Row.Families.Should().ContainSingle();
    }

    [Fact]
    public async Task Successive_readmodifywrites_accumulate()
    {
        for (int i = 0; i < 5; i++)
            await Client.ReadModifyWriteRowAsync(TN, "rp-r12",
                ReadModifyWriteRules.Increment(CF, "c", 1));
        var row = await Client.ReadRowAsync(TN, "rp-r12");
        var val = BitConverter.ToInt64(row!.Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(5);
    }
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for empty values, large values, and various value patterns.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
///   "value: The value to be written into the specified cell."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ValuePatternTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "val-pat";

    public ValuePatternTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task Empty_value()
    {
        await Client.MutateRowAsync(TN, "vp-r1",
            Mutations.SetCell(CF, "c", "", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "vp-r1");
        row!.Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task Unicode_value()
    {
        await Client.MutateRowAsync(TN, "vp-r2",
            Mutations.SetCell(CF, "c", "你好世界 🌍", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "vp-r2");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("你好世界 🌍");
    }

    [Fact]
    public async Task Binary_value_with_nulls()
    {
        var binVal = ByteString.CopyFrom(new byte[] { 0x00, 0x00, 0x00 });
        await Client.MutateRowAsync(TN, "vp-r3",
            Mutations.SetCell(CF, "c", binVal, new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "vp-r3");
        row!.Families[0].Columns[0].Cells[0].Value.ToByteArray()
            .Should().BeEquivalentTo(new byte[] { 0x00, 0x00, 0x00 });
    }

    [Fact]
    public async Task Moderate_binary_value()
    {
        // 10KB binary value
        var data = new byte[10240];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(i % 256);
        var binVal = ByteString.CopyFrom(data);
        await Client.MutateRowAsync(TN, "vp-r4",
            Mutations.SetCell(CF, "c", binVal, new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "vp-r4");
        row!.Families[0].Columns[0].Cells[0].Value.Length.Should().Be(10240);
    }

    [Fact]
    public async Task Single_byte_value()
    {
        var binVal = ByteString.CopyFrom(new byte[] { 0x42 });
        await Client.MutateRowAsync(TN, "vp-r5",
            Mutations.SetCell(CF, "c", binVal, new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "vp-r5");
        row!.Families[0].Columns[0].Cells[0].Value.ToByteArray()
            .Should().BeEquivalentTo(new byte[] { 0x42 });
    }

    [Fact]
    public async Task Long_string_value()
    {
        var longStr = new string('x', 5000);
        await Client.MutateRowAsync(TN, "vp-r6",
            Mutations.SetCell(CF, "c", longStr, new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "vp-r6");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be(longStr);
    }

    [Fact]
    public async Task Newlines_in_value()
    {
        await Client.MutateRowAsync(TN, "vp-r7",
            Mutations.SetCell(CF, "c", "line1\nline2\r\nline3", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "vp-r7");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("line1\nline2\r\nline3");
    }

    [Fact]
    public async Task All_byte_values_roundtrip()
    {
        // All 256 byte values
        var data = new byte[256];
        for (int i = 0; i < 256; i++)
            data[i] = (byte)i;
        var binVal = ByteString.CopyFrom(data);
        await Client.MutateRowAsync(TN, "vp-r8",
            Mutations.SetCell(CF, "c", binVal, new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "vp-r8");
        row!.Families[0].Columns[0].Cells[0].Value.ToByteArray()
            .Should().BeEquivalentTo(data);
    }

    [Fact]
    public async Task Json_string_value()
    {
        var json = """{"key":"value","number":42,"nested":{"a":true}}""";
        await Client.MutateRowAsync(TN, "vp-r9",
            Mutations.SetCell(CF, "c", json, new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "vp-r9");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be(json);
    }

    [Fact]
    public async Task Whitespace_only_value()
    {
        await Client.MutateRowAsync(TN, "vp-r10",
            Mutations.SetCell(CF, "c", "   \t\n  ", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "vp-r10");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("   \t\n  ");
    }
}

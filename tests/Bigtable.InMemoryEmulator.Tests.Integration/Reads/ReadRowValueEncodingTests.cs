using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowValueEncodingTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "val-enc";
    private const string CF = "cf";

    public ReadRowValueEncodingTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Empty_string_value()
    {
        await Client.MutateRowAsync(TN, "r1", Mutations.SetCell(CF, "c", ""));
        var row = await Client.ReadRowAsync(TN, "r1");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().BeEmpty();
    }

    [Fact]
    public async Task Utf8_value()
    {
        await Client.MutateRowAsync(TN, "r2", Mutations.SetCell(CF, "c", "héllo wörld"));
        var row = await Client.ReadRowAsync(TN, "r2");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("héllo wörld");
    }

    [Fact]
    public async Task Binary_value_with_nulls()
    {
        var val = ByteString.CopyFrom(new byte[] { 0x00, 0x00, 0x41, 0x00 });
        await Client.MutateRowAsync(TN, "r3",
            Mutations.SetCell(CF, "c", val, new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "r3");
        row!.Families[0].Columns[0].Cells[0].Value.ToByteArray()
            .Should().BeEquivalentTo(new byte[] { 0x00, 0x00, 0x41, 0x00 });
    }

    [Fact]
    public async Task Int64_big_endian_value()
    {
        var val = BitConverter.GetBytes(12345L).Reverse().ToArray();
        await Client.MutateRowAsync(TN, "r4",
            Mutations.SetCell(CF, "c", ByteString.CopyFrom(val), new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "r4");
        var readVal = BitConverter.ToInt64(row!.Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray());
        readVal.Should().Be(12345L);
    }

    [Fact]
    public async Task Large_value()
    {
        var val = new string('x', 5000);
        await Client.MutateRowAsync(TN, "r5", Mutations.SetCell(CF, "c", val));
        var row = await Client.ReadRowAsync(TN, "r5");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().HaveLength(5000);
    }

    [Fact]
    public async Task Newlines_in_value()
    {
        await Client.MutateRowAsync(TN, "r6", Mutations.SetCell(CF, "c", "line1\nline2\nline3"));
        var row = await Client.ReadRowAsync(TN, "r6");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("line1\nline2\nline3");
    }

    [Fact]
    public async Task Tabs_in_value()
    {
        await Client.MutateRowAsync(TN, "r7", Mutations.SetCell(CF, "c", "col1\tcol2\tcol3"));
        var row = await Client.ReadRowAsync(TN, "r7");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("col1\tcol2\tcol3");
    }

    [Fact]
    public async Task Json_value()
    {
        var json = """{"key":"value","num":42}""";
        await Client.MutateRowAsync(TN, "r8", Mutations.SetCell(CF, "c", json));
        var row = await Client.ReadRowAsync(TN, "r8");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be(json);
    }

    [Fact]
    public async Task Value_exact_on_binary()
    {
        var val = ByteString.CopyFrom(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        await Client.MutateRowAsync(TN, "r9",
            Mutations.SetCell(CF, "c", val, new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "r9",
            RowFilters.ValueExact(val));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Multiple_encodings_same_row()
    {
        await Client.MutateRowAsync(TN, "r10",
            Mutations.SetCell(CF, "text", "hello"),
            Mutations.SetCell(CF, "binary", ByteString.CopyFrom(new byte[] { 0xFF }), new BigtableVersion(1000)),
            Mutations.SetCell(CF, "number", ByteString.CopyFrom(BitConverter.GetBytes(42L).Reverse().ToArray()), new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "r10");
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(3);
    }
}

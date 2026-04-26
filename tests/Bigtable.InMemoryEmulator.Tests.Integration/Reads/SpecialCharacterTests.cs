using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class SpecialCharacterTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "special-char";
    private const string CF = "cf";

    public SpecialCharacterTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task RowKey_with_hash()
    {
        var rk = "row#123";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "val"));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Key.ToStringUtf8().Should().Be(rk);
    }

    [Fact]
    public async Task RowKey_with_slashes()
    {
        var rk = "a/b/c/d";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "val"));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Key.ToStringUtf8().Should().Be(rk);
    }

    [Fact]
    public async Task RowKey_with_spaces()
    {
        var rk = "row with spaces";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "val"));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task RowKey_with_unicode()
    {
        var rk = "日本語テスト";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "val"));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Key.ToStringUtf8().Should().Be(rk);
    }

    [Fact]
    public async Task ColumnQualifier_with_dots()
    {
        var rk = "sc-dots";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a.b.c", "val"));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.SelectMany(f => f.Columns).Single().Qualifier.ToStringUtf8().Should().Be("a.b.c");
    }

    [Fact]
    public async Task ColumnQualifier_with_colons()
    {
        var rk = "sc-colons";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "ns:key", "val"));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.SelectMany(f => f.Columns).Single().Qualifier.ToStringUtf8().Should().Be("ns:key");
    }

    [Fact]
    public async Task Value_with_newlines()
    {
        var rk = "sc-newline";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "line1\nline2\nline3"));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("line1\nline2\nline3");
    }

    [Fact]
    public async Task Value_empty_string()
    {
        var rk = "sc-empty-val";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", ""));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().BeEmpty();
    }

    [Fact]
    public async Task RowKey_with_null_bytes()
    {
        var rk = ByteString.CopyFrom(new byte[] { 0x01, 0x00, 0x02 });
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "val"));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Key.ToByteArray().Should().BeEquivalentTo(new byte[] { 0x01, 0x00, 0x02 });
    }

    [Fact]
    public async Task ColumnQualifier_binary()
    {
        var rk = "sc-bin-cq";
        var cq = ByteString.CopyFrom(new byte[] { 0xFF, 0x00, 0xAB });
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, cq, ByteString.CopyFromUtf8("val")));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.SelectMany(f => f.Columns).Single()
            .Qualifier.ToByteArray().Should().BeEquivalentTo(new byte[] { 0xFF, 0x00, 0xAB });
    }

    [Fact]
    public async Task RowKey_long_string()
    {
        var rk = new string('x', 4096);
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "val"));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Key.ToStringUtf8().Should().HaveLength(4096);
    }

    [Fact]
    public async Task Value_large_binary()
    {
        var rk = "sc-large-bin";
        var data = ByteString.CopyFrom(Enumerable.Range(0, 1024).Select(i => (byte)(i % 256)).ToArray());
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", data));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.Length.Should().Be(1024);
    }

    [Fact]
    public async Task RowKey_with_equals_and_ampersand()
    {
        var rk = "key=value&other=123";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "val"));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Key.ToStringUtf8().Should().Be(rk);
    }

    [Fact]
    public async Task Multiple_special_char_columns_in_same_row()
    {
        var rk = "sc-multi";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col.1", "a"),
            Mutations.SetCell(CF, "col:2", "b"),
            Mutations.SetCell(CF, "col/3", "c"),
            Mutations.SetCell(CF, "col#4", "d"));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(4);
    }

    [Fact]
    public async Task RowKey_single_byte()
    {
        var rk = "x";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "val"));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task ColumnQualifier_empty()
    {
        var rk = "sc-empty-cq";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "", "val"));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Single()
            .Qualifier.ToStringUtf8().Should().BeEmpty();
    }
}

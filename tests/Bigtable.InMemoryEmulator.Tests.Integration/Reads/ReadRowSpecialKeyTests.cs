using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowSpecialKeyTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "rr-skey";
    private const string CF = "cf";

    public ReadRowSpecialKeyTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Single_byte_key()
    {
        await Client.MutateRowAsync(TN, "a", Mutations.SetCell(CF, "c", "v"));
        var row = await Client.ReadRowAsync(TN, "a");
        row!.Key.ToStringUtf8().Should().Be("a");
    }

    [Fact]
    public async Task Long_key()
    {
        var key = new string('k', 4096);
        await Client.MutateRowAsync(TN, key, Mutations.SetCell(CF, "c", "v"));
        var row = await Client.ReadRowAsync(TN, key);
        row!.Key.ToStringUtf8().Should().HaveLength(4096);
    }

    [Fact]
    public async Task Key_with_special_chars()
    {
        var key = "row/with/slashes";
        await Client.MutateRowAsync(TN, key, Mutations.SetCell(CF, "c", "v"));
        var row = await Client.ReadRowAsync(TN, key);
        row!.Key.ToStringUtf8().Should().Be(key);
    }

    [Fact]
    public async Task Key_with_hash()
    {
        var key = "user#123#profile";
        await Client.MutateRowAsync(TN, key, Mutations.SetCell(CF, "c", "v"));
        var row = await Client.ReadRowAsync(TN, key);
        row!.Key.ToStringUtf8().Should().Be(key);
    }

    [Fact]
    public async Task Key_with_dots()
    {
        var key = "com.example.entity.123";
        await Client.MutateRowAsync(TN, key, Mutations.SetCell(CF, "c", "v"));
        var row = await Client.ReadRowAsync(TN, key);
        row!.Key.ToStringUtf8().Should().Be(key);
    }

    [Fact]
    public async Task Binary_key()
    {
        var key = ByteString.CopyFrom(new byte[] { 0x00, 0xFF, 0x80 });
        await Client.MutateRowAsync(TN, key, new[] { Mutations.SetCell(CF, "c", "v") });
        var row = await Client.ReadRowAsync(TN, key);
        row!.Key.ToByteArray().Should().BeEquivalentTo(new byte[] { 0x00, 0xFF, 0x80 });
    }

    [Fact]
    public async Task Key_with_spaces()
    {
        var key = "row with spaces";
        await Client.MutateRowAsync(TN, key, Mutations.SetCell(CF, "c", "v"));
        var row = await Client.ReadRowAsync(TN, key);
        row!.Key.ToStringUtf8().Should().Be(key);
    }

    [Fact]
    public async Task Key_with_unicode()
    {
        var key = "用户-123";
        await Client.MutateRowAsync(TN, key, Mutations.SetCell(CF, "c", "v"));
        var row = await Client.ReadRowAsync(TN, key);
        row!.Key.ToStringUtf8().Should().Be(key);
    }

    [Fact]
    public async Task Numeric_only_key()
    {
        var key = "1234567890";
        await Client.MutateRowAsync(TN, key, Mutations.SetCell(CF, "c", "v"));
        var row = await Client.ReadRowAsync(TN, key);
        row!.Key.ToStringUtf8().Should().Be(key);
    }

    [Fact]
    public async Task Key_lexicographic_ordering()
    {
        await Client.MutateRowAsync(TN, "b", Mutations.SetCell(CF, "c", "v"));
        await Client.MutateRowAsync(TN, "a", Mutations.SetCell(CF, "c", "v"));
        await Client.MutateRowAsync(TN, "c", Mutations.SetCell(CF, "c", "v"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN,
            RowSet.FromRowRanges(RowRange.Closed("a", "c"))))
            rows.Add(r);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Key_with_dashes()
    {
        var key = "2024-01-15-event-uuid-abc123";
        await Client.MutateRowAsync(TN, key, Mutations.SetCell(CF, "c", "v"));
        var row = await Client.ReadRowAsync(TN, key);
        row!.Key.ToStringUtf8().Should().Be(key);
    }

    [Fact]
    public async Task Key_with_underscores()
    {
        var key = "table_region_shard_001";
        await Client.MutateRowAsync(TN, key, Mutations.SetCell(CF, "c", "v"));
        var row = await Client.ReadRowAsync(TN, key);
        row!.Key.ToStringUtf8().Should().Be(key);
    }
}

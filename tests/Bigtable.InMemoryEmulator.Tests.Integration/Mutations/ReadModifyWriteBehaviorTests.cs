using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadModifyWriteBehaviorTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "rmw-beh";
    private const string CF = "cf";

    public ReadModifyWriteBehaviorTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Append_to_empty_cell()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "r1",
            ReadModifyWriteRules.Append(CF, "c", "hello"));
        response.Row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .First().Value.ToStringUtf8().Should().Be("hello");
    }

    [Fact]
    public async Task Append_to_existing_cell()
    {
        await Client.MutateRowAsync(TN, "r2", Mutations.SetCell(CF, "c", "hello", new BigtableVersion(1000)));
        var response = await Client.ReadModifyWriteRowAsync(TN, "r2",
            ReadModifyWriteRules.Append(CF, "c", " world"));
        response.Row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .First().Value.ToStringUtf8().Should().Be("hello world");
    }

    [Fact]
    public async Task Increment_from_zero()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "r3",
            ReadModifyWriteRules.Increment(CF, "c", 10));
        var val = response.Row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).First().Value;
        System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(val.Span).Should().Be(10);
    }

    [Fact]
    public async Task Increment_existing_value()
    {
        var bytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes, 100);
        await Client.MutateRowAsync(TN, "r4", Mutations.SetCell(CF, "c", ByteString.CopyFrom(bytes), new BigtableVersion(1000)));
        var response = await Client.ReadModifyWriteRowAsync(TN, "r4",
            ReadModifyWriteRules.Increment(CF, "c", 50));
        var val = response.Row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).First().Value;
        System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(val.Span).Should().Be(150);
    }

    [Fact]
    public async Task Multiple_rules_same_column()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "r5",
            ReadModifyWriteRules.Append(CF, "c", "a"),
            ReadModifyWriteRules.Append(CF, "c", "b"));
        response.Row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .First().Value.ToStringUtf8().Should().Be("ab");
    }

    [Fact]
    public async Task Multiple_rules_different_columns()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "r6",
            ReadModifyWriteRules.Append(CF, "a", "hello"),
            ReadModifyWriteRules.Append(CF, "b", "world"));
        var cols = response.Row.Families.SelectMany(f => f.Columns).ToList();
        cols.Should().HaveCount(2);
    }

    [Fact]
    public async Task Increment_negative()
    {
        var bytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes, 100);
        await Client.MutateRowAsync(TN, "r7", Mutations.SetCell(CF, "c", ByteString.CopyFrom(bytes), new BigtableVersion(1000)));
        var response = await Client.ReadModifyWriteRowAsync(TN, "r7",
            ReadModifyWriteRules.Increment(CF, "c", -30));
        var val = response.Row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).First().Value;
        System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(val.Span).Should().Be(70);
    }

    [Fact]
    public async Task Append_binary_data()
    {
        var data = new byte[] { 0x01, 0x02 };
        await Client.ReadModifyWriteRowAsync(TN, "r8",
            ReadModifyWriteRules.Append(CF, "c", ByteString.CopyFrom(data)));
        var response = await Client.ReadModifyWriteRowAsync(TN, "r8",
            ReadModifyWriteRules.Append(CF, "c", ByteString.CopyFrom(new byte[] { 0x03 })));
        var val = response.Row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).First().Value;
        val.ToByteArray().Should().BeEquivalentTo(new byte[] { 0x01, 0x02, 0x03 });
    }

    [Fact]
    public async Task Response_contains_row_key()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "r9",
            ReadModifyWriteRules.Append(CF, "c", "v"));
        response.Row.Key.ToStringUtf8().Should().Be("r9");
    }

    [Fact]
    public async Task Response_contains_family()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "r10",
            ReadModifyWriteRules.Append(CF, "c", "v"));
        response.Row.Families.Should().ContainSingle().Which.Name.Should().Be(CF);
    }

    [Fact]
    public async Task Sequential_appends()
    {
        for (int i = 0; i < 5; i++)
            await Client.ReadModifyWriteRowAsync(TN, "r11",
                ReadModifyWriteRules.Append(CF, "c", $"{i}"));
        var row = await Client.ReadRowAsync(TN, "r11");
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .First().Value.ToStringUtf8().Should().Be("01234");
    }

    [Fact]
    public async Task Sequential_increments()
    {
        for (int i = 0; i < 5; i++)
            await Client.ReadModifyWriteRowAsync(TN, "r12",
                ReadModifyWriteRules.Increment(CF, "c", 10));
        var row = await Client.ReadRowAsync(TN, "r12");
        var val = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).First().Value;
        System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(val.Span).Should().Be(50);
    }

    [Fact]
    public async Task Append_empty_string()
    {
        await Client.MutateRowAsync(TN, "r13", Mutations.SetCell(CF, "c", "hello", new BigtableVersion(1000)));
        var response = await Client.ReadModifyWriteRowAsync(TN, "r13",
            ReadModifyWriteRules.Append(CF, "c", ""));
        response.Row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .First().Value.ToStringUtf8().Should().Be("hello");
    }

    [Fact]
    public async Task Mixed_append_and_increment()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "r14",
            ReadModifyWriteRules.Append(CF, "text", "hello"),
            ReadModifyWriteRules.Increment(CF, "num", 42));
        var cols = response.Row.Families.SelectMany(f => f.Columns).ToList();
        cols.Should().HaveCount(2);
    }

    [Fact]
    public async Task Increment_by_zero()
    {
        var bytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes, 100);
        await Client.MutateRowAsync(TN, "r15", Mutations.SetCell(CF, "c", ByteString.CopyFrom(bytes), new BigtableVersion(1000)));
        var response = await Client.ReadModifyWriteRowAsync(TN, "r15",
            ReadModifyWriteRules.Increment(CF, "c", 0));
        var val = response.Row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).First().Value;
        System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(val.Span).Should().Be(100);
    }
}

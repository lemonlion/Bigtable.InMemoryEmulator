using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadModifyWriteMultiColumnTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "rmw-mc";
    private const string CF = "cf";

    public ReadModifyWriteMultiColumnTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Append_to_two_columns()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "r1",
            ReadModifyWriteRules.Append(CF, "a", "hello"),
            ReadModifyWriteRules.Append(CF, "b", "world"));
        response.Row.Families.SelectMany(f => f.Columns).Should().HaveCount(2);
    }

    [Fact]
    public async Task Increment_two_columns()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "r2",
            ReadModifyWriteRules.Increment(CF, "x", 10),
            ReadModifyWriteRules.Increment(CF, "y", 20));
        var cols = response.Row.Families.SelectMany(f => f.Columns).ToList();
        cols.Should().HaveCount(2);
    }

    [Fact]
    public async Task Mixed_append_increment_different_columns()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "r3",
            ReadModifyWriteRules.Append(CF, "text", "hi"),
            ReadModifyWriteRules.Increment(CF, "count", 1));
        var cols = response.Row.Families.SelectMany(f => f.Columns).ToList();
        cols.Should().HaveCount(2);
    }

    [Fact]
    public async Task Three_appends()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "r4",
            ReadModifyWriteRules.Append(CF, "a", "1"),
            ReadModifyWriteRules.Append(CF, "b", "2"),
            ReadModifyWriteRules.Append(CF, "c", "3"));
        response.Row.Families.SelectMany(f => f.Columns).Should().HaveCount(3);
    }

    [Fact]
    public async Task RMW_then_read()
    {
        await Client.ReadModifyWriteRowAsync(TN, "r5",
            ReadModifyWriteRules.Append(CF, "data", "test"));
        var row = await Client.ReadRowAsync(TN, "r5");
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .First().Value.ToStringUtf8().Should().Be("test");
    }

    [Fact]
    public async Task RMW_on_row_with_existing_data()
    {
        await Client.MutateRowAsync(TN, "r6",
            Mutations.SetCell(CF, "existing", "old", new BigtableVersion(1000)));
        await Client.ReadModifyWriteRowAsync(TN, "r6",
            ReadModifyWriteRules.Append(CF, "new", "data"));
        var row = await Client.ReadRowAsync(TN, "r6");
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(2);
    }

    [Fact]
    public async Task Double_append_same_column()
    {
        await Client.ReadModifyWriteRowAsync(TN, "r7",
            ReadModifyWriteRules.Append(CF, "col", "a"),
            ReadModifyWriteRules.Append(CF, "col", "b"));
        var row = await Client.ReadRowAsync(TN, "r7");
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .First().Value.ToStringUtf8().Should().Be("ab");
    }

    [Fact]
    public async Task Double_increment_same_column()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "r8",
            ReadModifyWriteRules.Increment(CF, "col", 5),
            ReadModifyWriteRules.Increment(CF, "col", 3));
        var val = response.Row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).First().Value;
        System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(val.Span).Should().Be(8);
    }

    [Fact]
    public async Task Five_columns_at_once()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "r9",
            ReadModifyWriteRules.Append(CF, "c1", "a"),
            ReadModifyWriteRules.Append(CF, "c2", "b"),
            ReadModifyWriteRules.Append(CF, "c3", "c"),
            ReadModifyWriteRules.Append(CF, "c4", "d"),
            ReadModifyWriteRules.Append(CF, "c5", "e"));
        response.Row.Families.SelectMany(f => f.Columns).Should().HaveCount(5);
    }

    [Fact]
    public async Task Response_row_key_matches()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "r10",
            ReadModifyWriteRules.Append(CF, "c", "v"));
        response.Row.Key.ToStringUtf8().Should().Be("r10");
    }

    [Fact]
    public async Task Sequential_RMW_on_same_row()
    {
        await Client.ReadModifyWriteRowAsync(TN, "r11",
            ReadModifyWriteRules.Append(CF, "log", "step1,"));
        await Client.ReadModifyWriteRowAsync(TN, "r11",
            ReadModifyWriteRules.Append(CF, "log", "step2,"));
        await Client.ReadModifyWriteRowAsync(TN, "r11",
            ReadModifyWriteRules.Append(CF, "log", "step3"));
        var row = await Client.ReadRowAsync(TN, "r11");
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .First().Value.ToStringUtf8().Should().Be("step1,step2,step3");
    }

    [Fact]
    public async Task Increment_then_read_as_int()
    {
        await Client.ReadModifyWriteRowAsync(TN, "r12",
            ReadModifyWriteRules.Increment(CF, "counter", 42));
        var row = await Client.ReadRowAsync(TN, "r12");
        var val = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).First().Value;
        System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(val.Span).Should().Be(42);
    }
}

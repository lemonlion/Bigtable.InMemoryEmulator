using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadModifyWriteScenarioTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "rmws-tests";
    private const string CF = "cf";

    public ReadModifyWriteScenarioTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Append_to_empty_cell()
    {
        var result = await Client.ReadModifyWriteRowAsync(TN, "rmws-ae",
            ReadModifyWriteRules.Append(CF, "col", "hello"));
        result.Row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("hello");
    }

    [Fact]
    public async Task Append_concatenates()
    {
        await Client.MutateRowAsync(TN, "rmws-cat", Mutations.SetCell(CF, "col", "hello"));
        var result = await Client.ReadModifyWriteRowAsync(TN, "rmws-cat",
            ReadModifyWriteRules.Append(CF, "col", "-world"));
        result.Row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("hello-world");
    }

    [Fact]
    public async Task Increment_from_zero()
    {
        var result = await Client.ReadModifyWriteRowAsync(TN, "rmws-iz",
            ReadModifyWriteRules.Increment(CF, "n", 5));
        var val = BitConverter.ToInt64(result.Row.Families.SelectMany(f => f.Columns)
            .SelectMany(c => c.Cells).Single().Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(5);
    }

    [Fact]
    public async Task Increment_adds_to_existing()
    {
        var initial = ByteString.CopyFrom(BitConverter.GetBytes((long)10).Reverse().ToArray());
        await Client.MutateRowAsync(TN, "rmws-ia", Mutations.SetCell(CF, "n", initial));
        var result = await Client.ReadModifyWriteRowAsync(TN, "rmws-ia",
            ReadModifyWriteRules.Increment(CF, "n", 7));
        var val = BitConverter.ToInt64(result.Row.Families.SelectMany(f => f.Columns)
            .SelectMany(c => c.Cells).Single().Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(17);
    }

    [Fact]
    public async Task Increment_negative()
    {
        var result = await Client.ReadModifyWriteRowAsync(TN, "rmws-in",
            ReadModifyWriteRules.Increment(CF, "n", -3));
        var val = BitConverter.ToInt64(result.Row.Families.SelectMany(f => f.Columns)
            .SelectMany(c => c.Cells).Single().Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(-3);
    }

    [Fact]
    public async Task Multiple_rules()
    {
        var result = await Client.ReadModifyWriteRowAsync(TN, "rmws-mr",
            ReadModifyWriteRules.Append(CF, "name", "foo"),
            ReadModifyWriteRules.Increment(CF, "count", 1));
        result.Row.Families.SelectMany(f => f.Columns).Should().HaveCount(2);
    }

    [Fact]
    public async Task Triple_append()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmws-ta", ReadModifyWriteRules.Append(CF, "log", "a"));
        await Client.ReadModifyWriteRowAsync(TN, "rmws-ta", ReadModifyWriteRules.Append(CF, "log", "b"));
        var result = await Client.ReadModifyWriteRowAsync(TN, "rmws-ta", ReadModifyWriteRules.Append(CF, "log", "c"));
        result.Row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("abc");
    }

    [Fact]
    public async Task Triple_increment()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmws-ti", ReadModifyWriteRules.Increment(CF, "n", 1));
        await Client.ReadModifyWriteRowAsync(TN, "rmws-ti", ReadModifyWriteRules.Increment(CF, "n", 2));
        var result = await Client.ReadModifyWriteRowAsync(TN, "rmws-ti", ReadModifyWriteRules.Increment(CF, "n", 3));
        var val = BitConverter.ToInt64(result.Row.Families.SelectMany(f => f.Columns)
            .SelectMany(c => c.Cells).Single().Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(6);
    }

    [Fact]
    public async Task Creates_row()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmws-cr", ReadModifyWriteRules.Append(CF, "col", "new"));
        var row = await Client.ReadRowAsync(TN, "rmws-cr");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Response_has_correct_key()
    {
        var result = await Client.ReadModifyWriteRowAsync(TN, "rmws-rk",
            ReadModifyWriteRules.Append(CF, "col", "val"));
        result.Row.Key.ToStringUtf8().Should().Be("rmws-rk");
    }

    [Fact]
    public async Task Preserves_other_columns()
    {
        await Client.MutateRowAsync(TN, "rmws-po", Mutations.SetCell(CF, "existing", "keep"), Mutations.SetCell(CF, "target", "old"));
        await Client.ReadModifyWriteRowAsync(TN, "rmws-po", ReadModifyWriteRules.Append(CF, "target", "-new"));
        var row = await Client.ReadRowAsync(TN, "rmws-po");
        row!.Families.SelectMany(f => f.Columns).First(c => c.Qualifier.ToStringUtf8() == "existing")
            .Cells[0].Value.ToStringUtf8().Should().Be("keep");
    }

    [Fact]
    public async Task Binary_append()
    {
        var data = ByteString.CopyFrom(new byte[] { 0x01, 0x02 });
        await Client.MutateRowAsync(TN, "rmws-ba", Mutations.SetCell(CF, "bin", data));
        var append = ByteString.CopyFrom(new byte[] { 0x03, 0x04 });
        var result = await Client.ReadModifyWriteRowAsync(TN, "rmws-ba", ReadModifyWriteRules.Append(CF, "bin", append));
        result.Row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToByteArray().Should().BeEquivalentTo(new byte[] { 0x01, 0x02, 0x03, 0x04 });
    }

    [Fact]
    public async Task Multiple_columns()
    {
        var result = await Client.ReadModifyWriteRowAsync(TN, "rmws-mc",
            ReadModifyWriteRules.Append(CF, "a", "aa"),
            ReadModifyWriteRules.Append(CF, "b", "bb"),
            ReadModifyWriteRules.Append(CF, "c", "cc"));
        result.Row.Families.SelectMany(f => f.Columns).Should().HaveCount(3);
    }

    [Fact]
    public async Task Increment_zero()
    {
        var result = await Client.ReadModifyWriteRowAsync(TN, "rmws-z",
            ReadModifyWriteRules.Increment(CF, "n", 0));
        var val = BitConverter.ToInt64(result.Row.Families.SelectMany(f => f.Columns)
            .SelectMany(c => c.Cells).Single().Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(0);
    }

    [Fact]
    public async Task Append_empty_string()
    {
        await Client.MutateRowAsync(TN, "rmws-es", Mutations.SetCell(CF, "col", "hello"));
        var result = await Client.ReadModifyWriteRowAsync(TN, "rmws-es",
            ReadModifyWriteRules.Append(CF, "col", ""));
        result.Row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("hello");
    }
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for ReadModifyWriteRow — append and increment rules across
/// multiple columns, families, and combining multiple rules.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readmodifywriterowrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadModifyWriteComboTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "rmw-combo";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public ReadModifyWriteComboTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Append_to_empty_cell()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "rmw-app-empty",
            ReadModifyWriteRules.Append(CF, "c", "hello"));

        var val = response.Row.Families
            .First(f => f.Name == CF).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "c")
            .Cells[0].Value.ToStringUtf8();
        val.Should().Be("hello");
    }

    [Fact]
    public async Task Append_to_existing_value()
    {
        await Client.MutateRowAsync(TN, "rmw-app-existing",
            Mutations.SetCell(CF, "c", "hello", new BigtableVersion(1000)));

        var response = await Client.ReadModifyWriteRowAsync(TN, "rmw-app-existing",
            ReadModifyWriteRules.Append(CF, "c", " world"));

        var val = response.Row.Families
            .First(f => f.Name == CF).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "c")
            .Cells[0].Value.ToStringUtf8();
        val.Should().Contain("hello");
        val.Should().Contain("world");
    }

    [Fact]
    public async Task Increment_from_zero()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-zero",
            ReadModifyWriteRules.Increment(CF, "counter", 10));

        var bytes = response.Row.Families
            .First(f => f.Name == CF).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "counter")
            .Cells[0].Value.ToByteArray();
        var val = BitConverter.ToInt64(bytes.Reverse().ToArray(), 0);
        val.Should().Be(10);
    }

    [Fact]
    public async Task Increment_accumulates()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-acc",
            ReadModifyWriteRules.Increment(CF, "counter", 10));
        await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-acc",
            ReadModifyWriteRules.Increment(CF, "counter", 20));
        var response = await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-acc",
            ReadModifyWriteRules.Increment(CF, "counter", 12));

        var bytes = response.Row.Families
            .First(f => f.Name == CF).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "counter")
            .Cells[0].Value.ToByteArray();
        var val = BitConverter.ToInt64(bytes.Reverse().ToArray(), 0);
        val.Should().Be(42);
    }

    [Fact]
    public async Task Increment_negative()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-neg",
            ReadModifyWriteRules.Increment(CF, "counter", 100));
        var response = await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-neg",
            ReadModifyWriteRules.Increment(CF, "counter", -30));

        var bytes = response.Row.Families
            .First(f => f.Name == CF).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "counter")
            .Cells[0].Value.ToByteArray();
        var val = BitConverter.ToInt64(bytes.Reverse().ToArray(), 0);
        val.Should().Be(70);
    }

    [Fact]
    public async Task Multiple_append_rules_same_request()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmw-multi-app",
            ReadModifyWriteRules.Append(CF, "a", "hello"),
            ReadModifyWriteRules.Append(CF, "b", "world"));

        var row = await Client.ReadRowAsync(TN, "rmw-multi-app");
        var cols = row!.Families[0].Columns.ToDictionary(
            c => c.Qualifier.ToStringUtf8(),
            c => c.Cells[0].Value.ToStringUtf8());
        cols["a"].Should().Be("hello");
        cols["b"].Should().Be("world");
    }

    [Fact]
    public async Task Multiple_increment_rules_same_request()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "rmw-multi-inc",
            ReadModifyWriteRules.Increment(CF, "x", 10),
            ReadModifyWriteRules.Increment(CF, "y", 20));

        var xBytes = response.Row.Families
            .First(f => f.Name == CF).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "x")
            .Cells[0].Value.ToByteArray();
        var yBytes = response.Row.Families
            .First(f => f.Name == CF).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "y")
            .Cells[0].Value.ToByteArray();

        BitConverter.ToInt64(xBytes.Reverse().ToArray(), 0).Should().Be(10);
        BitConverter.ToInt64(yBytes.Reverse().ToArray(), 0).Should().Be(20);
    }

    [Fact]
    public async Task Mixed_append_and_increment_rules()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "rmw-mixed",
            ReadModifyWriteRules.Append(CF, "name", "test"),
            ReadModifyWriteRules.Increment(CF, "counter", 5));

        var nameVal = response.Row.Families
            .First(f => f.Name == CF).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "name")
            .Cells[0].Value.ToStringUtf8();
        nameVal.Should().Be("test");

        var counterBytes = response.Row.Families
            .First(f => f.Name == CF).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "counter")
            .Cells[0].Value.ToByteArray();
        BitConverter.ToInt64(counterBytes.Reverse().ToArray(), 0).Should().Be(5);
    }

    [Fact]
    public async Task Append_across_families()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "rmw-xfam",
            ReadModifyWriteRules.Append(CF, "a", "cf-val"),
            ReadModifyWriteRules.Append("cf2", "b", "cf2-val"));

        response.Row.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Append_binary_data()
    {
        var binData = ByteString.CopyFrom(new byte[] { 0xDE, 0xAD });
        var response = await Client.ReadModifyWriteRowAsync(TN, "rmw-bin-app",
            ReadModifyWriteRules.Append(CF, "c", binData));

        var result = response.Row.Families
            .First(f => f.Name == CF).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "c")
            .Cells[0].Value.ToByteArray();
        result.Should().BeEquivalentTo(new byte[] { 0xDE, 0xAD });
    }

    [Fact]
    public async Task Append_empty_string()
    {
        await Client.MutateRowAsync(TN, "rmw-app-empty-str",
            Mutations.SetCell(CF, "c", "original", new BigtableVersion(1000)));

        var response = await Client.ReadModifyWriteRowAsync(TN, "rmw-app-empty-str",
            ReadModifyWriteRules.Append(CF, "c", ""));

        var val = response.Row.Families
            .First(f => f.Name == CF).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "c")
            .Cells[0].Value.ToStringUtf8();
        val.Should().Be("original");
    }

    [Fact]
    public async Task Response_contains_updated_row()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "rmw-resp",
            ReadModifyWriteRules.Append(CF, "c", "val"));

        response.Row.Should().NotBeNull();
        response.Row.Key.ToStringUtf8().Should().Be("rmw-resp");
        response.Row.Families.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Increment_creates_server_timestamp()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-ts",
            ReadModifyWriteRules.Increment(CF, "c", 1));

        var ts = response.Row.Families
            .First(f => f.Name == CF).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "c")
            .Cells[0].TimestampMicros;
        ts.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Append_twice_to_same_column_same_request()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "rmw-dbl-app",
            ReadModifyWriteRules.Append(CF, "c", "first"),
            ReadModifyWriteRules.Append(CF, "c", "second"));

        var val = response.Row.Families
            .First(f => f.Name == CF).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "c")
            .Cells[0].Value.ToStringUtf8();
        val.Should().Be("firstsecond");
    }

    [Fact]
    public async Task Increment_preserves_existing_data_in_other_columns()
    {
        await Client.MutateRowAsync(TN, "rmw-preserve",
            Mutations.SetCell(CF, "name", "test", new BigtableVersion(1000)));

        await Client.ReadModifyWriteRowAsync(TN, "rmw-preserve",
            ReadModifyWriteRules.Increment(CF, "counter", 1));

        var row = await Client.ReadRowAsync(TN, "rmw-preserve");
        var nameCol = row!.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "name");
        nameCol.Cells[0].Value.ToStringUtf8().Should().Be("test");
    }

    [Fact]
    public async Task Five_sequential_appends()
    {
        for (int i = 0; i < 5; i++)
            await Client.ReadModifyWriteRowAsync(TN, "rmw-5seq",
                ReadModifyWriteRules.Append(CF, "log", $"[{i}]"));

        var row = await Client.ReadRowAsync(TN, "rmw-5seq");
        var val = row!.Families[0].Columns
            .First(c => c.Qualifier.ToStringUtf8() == "log")
            .Cells[0].Value.ToStringUtf8();
        val.Should().Be("[0][1][2][3][4]");
    }

    [Fact]
    public async Task Increment_large_value()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "rmw-large-inc",
            ReadModifyWriteRules.Increment(CF, "c", long.MaxValue / 2));

        var bytes = response.Row.Families
            .First(f => f.Name == CF).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "c")
            .Cells[0].Value.ToByteArray();
        var val = BitConverter.ToInt64(bytes.Reverse().ToArray(), 0);
        val.Should().Be(long.MaxValue / 2);
    }
}

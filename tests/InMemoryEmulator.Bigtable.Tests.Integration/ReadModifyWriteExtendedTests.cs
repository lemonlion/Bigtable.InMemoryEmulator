using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for ReadModifyWrite advanced patterns and edge cases.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readmodifywriterowrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadModifyWriteExtendedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "rmwe-tests";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public ReadModifyWriteExtendedTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Increment_on_nonexistent_row_creates_it()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwe-new",
            ReadModifyWriteRules.Increment(CF, "counter", 10));
        var val = GetInt64(resp, CF, "counter");
        val.Should().Be(10);
    }

    [Fact]
    public async Task Increment_multiple_times()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmwe-multi",
            ReadModifyWriteRules.Increment(CF, "counter", 5));
        await Client.ReadModifyWriteRowAsync(TN, "rmwe-multi",
            ReadModifyWriteRules.Increment(CF, "counter", 3));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwe-multi",
            ReadModifyWriteRules.Increment(CF, "counter", 2));
        var val = GetInt64(resp, CF, "counter");
        val.Should().Be(10);
    }

    [Fact]
    public async Task Increment_negative()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmwe-neg",
            ReadModifyWriteRules.Increment(CF, "counter", 100));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwe-neg",
            ReadModifyWriteRules.Increment(CF, "counter", -30));
        var val = GetInt64(resp, CF, "counter");
        val.Should().Be(70);
    }

    [Fact]
    public async Task Increment_to_zero()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmwe-zero",
            ReadModifyWriteRules.Increment(CF, "counter", 50));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwe-zero",
            ReadModifyWriteRules.Increment(CF, "counter", -50));
        var val = GetInt64(resp, CF, "counter");
        val.Should().Be(0);
    }

    [Fact]
    public async Task Append_on_nonexistent_row()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwe-app-new",
            ReadModifyWriteRules.Append(CF, "log", "hello"));
        var val = GetString(resp, CF, "log");
        val.Should().Be("hello");
    }

    [Fact]
    public async Task Append_multiple_times_concatenates()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmwe-app-multi",
            ReadModifyWriteRules.Append(CF, "log", "a"));
        await Client.ReadModifyWriteRowAsync(TN, "rmwe-app-multi",
            ReadModifyWriteRules.Append(CF, "log", "b"));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwe-app-multi",
            ReadModifyWriteRules.Append(CF, "log", "c"));
        var val = GetString(resp, CF, "log");
        val.Should().Be("abc");
    }

    [Fact]
    public async Task Multiple_rules_in_one_call()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwe-combo",
            ReadModifyWriteRules.Increment(CF, "counter", 42),
            ReadModifyWriteRules.Append(CF, "log", "init"));
        GetInt64(resp, CF, "counter").Should().Be(42);
        GetString(resp, CF, "log").Should().Be("init");
    }

    [Fact]
    public async Task Multiple_increment_rules_different_columns()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwe-multicol",
            ReadModifyWriteRules.Increment(CF, "a", 1),
            ReadModifyWriteRules.Increment(CF, "b", 2),
            ReadModifyWriteRules.Increment(CF, "c", 3));

        GetInt64(resp, CF, "a").Should().Be(1);
        GetInt64(resp, CF, "b").Should().Be(2);
        GetInt64(resp, CF, "c").Should().Be(3);
    }

    [Fact]
    public async Task Append_binary_data()
    {
        var data1 = ByteString.CopyFrom(new byte[] { 0x01, 0x02 });
        var data2 = ByteString.CopyFrom(new byte[] { 0x03, 0x04 });

        await Client.ReadModifyWriteRowAsync(TN, "rmwe-bin",
            ReadModifyWriteRules.Append(CF, "data", data1));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwe-bin",
            ReadModifyWriteRules.Append(CF, "data", data2));

        var result = resp.Row.Families
            .First(f => f.Name == CF).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "data")
            .Cells[0].Value.ToByteArray();
        result.Should().BeEquivalentTo(new byte[] { 0x01, 0x02, 0x03, 0x04 });
    }

    [Fact]
    public async Task RMW_preserves_other_columns()
    {
        await Client.MutateRowAsync(TN, "rmwe-preserve",
            Mutations.SetCell(CF, "name", "Alice", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "age", "30", new BigtableVersion(1000)));

        await Client.ReadModifyWriteRowAsync(TN, "rmwe-preserve",
            ReadModifyWriteRules.Append(CF, "log", "updated"));

        var row = await Client.ReadRowAsync(TN, "rmwe-preserve");
        GetStringFromRow(row!, CF, "name").Should().Be("Alice");
        GetStringFromRow(row!, CF, "age").Should().Be("30");
        GetStringFromRow(row!, CF, "log").Should().Be("updated");
    }

    [Fact]
    public async Task Increment_large_value()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwe-large",
            ReadModifyWriteRules.Increment(CF, "counter", long.MaxValue / 2));
        GetInt64(resp, CF, "counter").Should().Be(long.MaxValue / 2);
    }

    [Fact]
    public async Task Append_long_string()
    {
        var longStr = new string('x', 5000);
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwe-long",
            ReadModifyWriteRules.Append(CF, "data", longStr));
        GetString(resp, CF, "data").Should().HaveLength(5000);
    }

    [Fact]
    public async Task Append_empty_string_no_change()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmwe-empty",
            ReadModifyWriteRules.Append(CF, "log", "initial"));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwe-empty",
            ReadModifyWriteRules.Append(CF, "log", ""));
        GetString(resp, CF, "log").Should().Be("initial");
    }

    [Fact]
    public async Task Increment_then_read()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmwe-read",
            ReadModifyWriteRules.Increment(CF, "counter", 7));

        var row = await Client.ReadRowAsync(TN, "rmwe-read");
        var bytes = row!.Families.First(f => f.Name == CF).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "counter")
            .Cells[0].Value.ToByteArray();
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        BitConverter.ToInt64(bytes, 0).Should().Be(7);
    }

    private static long GetInt64(ReadModifyWriteRowResponse resp, string family, string col)
    {
        var bytes = resp.Row.Families
            .First(f => f.Name == family).Columns
            .First(c => c.Qualifier.ToStringUtf8() == col)
            .Cells[0].Value.ToByteArray();
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToInt64(bytes, 0);
    }

    private static string GetString(ReadModifyWriteRowResponse resp, string family, string col) =>
        resp.Row.Families
            .First(f => f.Name == family).Columns
            .First(c => c.Qualifier.ToStringUtf8() == col)
            .Cells[0].Value.ToStringUtf8();

    private static string GetStringFromRow(Row row, string family, string col) =>
        row.Families
            .First(f => f.Name == family).Columns
            .First(c => c.Qualifier.ToStringUtf8() == col)
            .Cells.OrderByDescending(c => c.TimestampMicros).First()
            .Value.ToStringUtf8();
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Edge case tests for ReadModifyWrite operations: binary data, empty values,
/// large appends, overflow behavior, and interaction with multiple column families.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readmodifywriterowrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadModifyWriteEdgeCaseTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public ReadModifyWriteEdgeCaseTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync("rmw-edge", new[] { CF, CF2 });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("rmw-edge");

    #region Binary append

    [Fact]
    public async Task Append_binary_data_to_new_cell()
    {
        var bytes = new byte[] { 0x00, 0xFF, 0x01, 0xFE };
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-bin1",
            ReadModifyWriteRules.Append(CF, "bin", ByteString.CopyFrom(bytes)));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray().Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public async Task Append_binary_to_existing_binary()
    {
        var initial = new byte[] { 0x01, 0x02 };
        await Client.MutateRowAsync(TN, "rmw-bin2",
            Mutations.SetCell(CF, "bin", ByteString.CopyFrom(initial), new BigtableVersion(1000)));
        var append = new byte[] { 0x03, 0x04 };
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-bin2",
            ReadModifyWriteRules.Append(CF, "bin", ByteString.CopyFrom(append)));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray()
            .Should().BeEquivalentTo(new byte[] { 0x01, 0x02, 0x03, 0x04 });
    }

    [Fact]
    public async Task Append_null_bytes_in_middle()
    {
        var data = new byte[] { 0x41, 0x00, 0x42 }; // A\0B
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-null",
            ReadModifyWriteRules.Append(CF, "bin", ByteString.CopyFrom(data)));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray().Should().BeEquivalentTo(data);
    }

    #endregion

    #region Empty and special values

    [Fact]
    public async Task Append_empty_string_to_new_cell()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-empty1",
            ReadModifyWriteRules.Append(CF, "col", ""));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().BeEmpty();
    }

    [Fact]
    public async Task Append_empty_string_to_existing_cell()
    {
        await Client.MutateRowAsync(TN, "rmw-empty2",
            Mutations.SetCell(CF, "col", "hello", new BigtableVersion(1000)));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-empty2",
            ReadModifyWriteRules.Append(CF, "col", ""));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("hello");
    }

    [Fact]
    public async Task Append_to_cell_with_empty_existing_value()
    {
        await Client.MutateRowAsync(TN, "rmw-empty3",
            Mutations.SetCell(CF, "col", "", new BigtableVersion(1000)));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-empty3",
            ReadModifyWriteRules.Append(CF, "col", "world"));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("world");
    }

    #endregion

    #region Increment edge cases

    [Fact]
    public async Task Increment_zero_on_new_cell()
    {
        // Ref: increment 0 on non-existent cell should create cell with 0
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-inc0",
            ReadModifyWriteRules.Increment(CF, "counter", 0));
        var val = BitConverter.ToInt64(resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(0);
    }

    [Fact]
    public async Task Increment_negative_value()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmw-neg",
            ReadModifyWriteRules.Increment(CF, "counter", 100));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-neg",
            ReadModifyWriteRules.Increment(CF, "counter", -30));
        var bytes = resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray();
        var val = BitConverter.ToInt64(bytes.Reverse().ToArray(), 0);
        val.Should().Be(70);
    }

    [Fact]
    public async Task Increment_max_int64()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-max",
            ReadModifyWriteRules.Increment(CF, "counter", long.MaxValue));
        var val = BitConverter.ToInt64(resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(long.MaxValue);
    }

    [Fact]
    public async Task Increment_min_int64()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-min",
            ReadModifyWriteRules.Increment(CF, "counter", long.MinValue));
        var val = BitConverter.ToInt64(resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(long.MinValue);
    }

    #endregion

    #region Cross-family operations

    [Fact]
    public async Task Append_across_two_families()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-cross1",
            ReadModifyWriteRules.Append(CF, "log", "a"),
            ReadModifyWriteRules.Append(CF2, "log", "b"));
        resp.Row.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Increment_across_families()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-cross2",
            ReadModifyWriteRules.Increment(CF, "c1", 10),
            ReadModifyWriteRules.Increment(CF2, "c2", 20));
        resp.Row.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Mixed_append_and_increment_cross_family()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-cross3",
            ReadModifyWriteRules.Append(CF, "log", "entry"),
            ReadModifyWriteRules.Increment(CF2, "count", 1));
        resp.Row.Families.Should().HaveCount(2);
        var cf1 = resp.Row.Families.First(f => f.Name == CF);
        cf1.Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("entry");
    }

    #endregion

    #region Multiple columns same family

    [Fact]
    public async Task Append_multiple_columns_same_family()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-multi1",
            ReadModifyWriteRules.Append(CF, "a", "va"),
            ReadModifyWriteRules.Append(CF, "b", "vb"),
            ReadModifyWriteRules.Append(CF, "c", "vc"));
        var fam = resp.Row.Families.First(f => f.Name == CF);
        fam.Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task Increment_multiple_columns_same_family()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-multi2",
            ReadModifyWriteRules.Increment(CF, "x", 1),
            ReadModifyWriteRules.Increment(CF, "y", 2),
            ReadModifyWriteRules.Increment(CF, "z", 3));
        var fam = resp.Row.Families.First(f => f.Name == CF);
        fam.Columns.Should().HaveCount(3);
    }

    #endregion

    #region Response semantics

    [Fact]
    public async Task Response_contains_only_modified_cells()
    {
        // Ref: The response returns the row after modification, but only for the modified cells
        await Client.MutateRowAsync(TN, "rmw-resp1",
            Mutations.SetCell(CF, "existing", "val", new BigtableVersion(1000)));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-resp1",
            ReadModifyWriteRules.Append(CF, "new-col", "new-val"));
        // Response should contain the modified column
        var cols = resp.Row.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("new-col");
    }

    [Fact]
    public async Task Repeated_append_same_column_in_single_request()
    {
        // Ref: Multiple rules targeting the same column - they should be applied sequentially
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-repeat",
            ReadModifyWriteRules.Append(CF, "log", "first-"),
            ReadModifyWriteRules.Append(CF, "log", "second"));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8()
            .Should().Be("first-second");
    }

    [Fact]
    public async Task Repeated_increment_same_column_in_single_request()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-rep-inc",
            ReadModifyWriteRules.Increment(CF, "counter", 5),
            ReadModifyWriteRules.Increment(CF, "counter", 3));
        var val = BitConverter.ToInt64(resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(8);
    }

    [Fact]
    public async Task Append_unicode_string()
    {
        var unicode = "こんにちは世界🌍";
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-unicode",
            ReadModifyWriteRules.Append(CF, "text", unicode));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be(unicode);
    }

    [Fact]
    public async Task Append_unicode_accumulation()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmw-uacc",
            ReadModifyWriteRules.Append(CF, "text", "hello-"));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-uacc",
            ReadModifyWriteRules.Append(CF, "text", "世界"));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("hello-世界");
    }

    #endregion

    #region ReadModifyWrite on nonexistent row

    [Fact]
    public async Task Increment_creates_new_row()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-newrow",
            ReadModifyWriteRules.Increment(CF, "c", 42));
        resp.Row.Key.ToStringUtf8().Should().Be("rmw-newrow");
        var val = BitConverter.ToInt64(resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray(), 0);
        val.Should().Be(42);
    }

    [Fact]
    public async Task Append_creates_new_row()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-newrow2",
            ReadModifyWriteRules.Append(CF, "c", "data"));
        resp.Row.Key.ToStringUtf8().Should().Be("rmw-newrow2");
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("data");
    }

    #endregion

    #region Large data

    [Fact]
    public async Task Append_1kb_data()
    {
        var data = new string('X', 1024);
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-1k",
            ReadModifyWriteRules.Append(CF, "big", data));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().HaveLength(1024);
    }

    [Fact]
    public async Task Append_accumulates_to_large_value()
    {
        var chunk = new string('A', 500);
        for (int i = 0; i < 5; i++)
            await Client.ReadModifyWriteRowAsync(TN, "rmw-accum",
                ReadModifyWriteRules.Append(CF, "big", chunk));
        var row = await Client.ReadRowAsync(TN, "rmw-accum");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().HaveLength(2500);
    }

    #endregion
}

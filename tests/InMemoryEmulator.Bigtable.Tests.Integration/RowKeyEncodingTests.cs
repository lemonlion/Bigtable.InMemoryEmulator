using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for row key encoding — binary keys, special characters, ordering,
/// and key size edge cases.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
///   "Row keys are sorted lexicographically by raw byte values."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class RowKeyEncodingTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "rk-encoding";
    private const string CF = "cf";

    public RowKeyEncodingTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task WriteRow(string key) =>
        await Client.MutateRowAsync(TN, new BigtableByteString(key),
            Mutations.SetCell(CF, "col", "val", new BigtableVersion(1000)));

    private async Task WriteRow(byte[] key) =>
        await Client.MutateRowAsync(TN, new BigtableByteString(key),
            Mutations.SetCell(CF, "col", "val", new BigtableVersion(1000)));

    [Fact]
    public async Task Single_character_key()
    {
        await WriteRow("a");
        var row = await Client.ReadRowAsync(TN, new BigtableByteString("a"));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Numeric_key()
    {
        await WriteRow("12345");
        var row = await Client.ReadRowAsync(TN, new BigtableByteString("12345"));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Key_with_hash_separator()
    {
        await WriteRow("user#123#profile");
        var row = await Client.ReadRowAsync(TN, new BigtableByteString("user#123#profile"));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Key_with_slashes()
    {
        await WriteRow("path/to/resource");
        var row = await Client.ReadRowAsync(TN, new BigtableByteString("path/to/resource"));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Key_with_special_characters()
    {
        await WriteRow("key!@#$%^&*()");
        var row = await Client.ReadRowAsync(TN, new BigtableByteString("key!@#$%^&*()"));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Key_with_unicode()
    {
        await WriteRow("日本語キー");
        var row = await Client.ReadRowAsync(TN, new BigtableByteString("日本語キー"));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Key_with_null_bytes()
    {
        var key = new byte[] { 0x61, 0x00, 0x62 }; // "a\0b"
        await WriteRow(key);
        var row = await Client.ReadRowAsync(TN, new BigtableByteString(key));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Key_with_high_bytes()
    {
        var key = new byte[] { 0xFF, 0xFE, 0xFD };
        await WriteRow(key);
        var row = await Client.ReadRowAsync(TN, new BigtableByteString(key));
        row.Should().NotBeNull();
    }

    // Ref: "Row keys are sorted lexicographically by raw byte values."
    [Fact]
    public async Task Keys_sorted_lexicographically()
    {
        var keys = new[] { "c", "a", "b" };
        foreach (var k in keys)
            await WriteRow($"rke-sort-{k}");

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges = { new RowRange
                {
                    StartKeyClosed = ByteString.CopyFromUtf8("rke-sort-"),
                    EndKeyOpen = ByteString.CopyFromUtf8("rke-sort-~")
                }}
            }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        var readKeys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        readKeys.Should().BeInAscendingOrder();
        readKeys.Should().BeEquivalentTo(new[] { "rke-sort-a", "rke-sort-b", "rke-sort-c" });
    }

    [Fact]
    public async Task Numeric_keys_sorted_as_strings_not_numbers()
    {
        var nums = new[] { "1", "10", "2", "20", "3" };
        foreach (var n in nums)
            await WriteRow($"rke-num-{n}");

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges = { new RowRange
                {
                    StartKeyClosed = ByteString.CopyFromUtf8("rke-num-"),
                    EndKeyOpen = ByteString.CopyFromUtf8("rke-num-~")
                }}
            }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        var readKeys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        // String order: 1, 10, 2, 20, 3 (not numeric order)
        readKeys.Should().BeEquivalentTo(new[] { "rke-num-1", "rke-num-10", "rke-num-2", "rke-num-20", "rke-num-3" });
        readKeys.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Key_with_spaces()
    {
        await WriteRow("key with spaces");
        var row = await Client.ReadRowAsync(TN, new BigtableByteString("key with spaces"));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Key_with_tabs_and_newlines()
    {
        await WriteRow("key\twith\nnewline");
        var row = await Client.ReadRowAsync(TN, new BigtableByteString("key\twith\nnewline"));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Long_key_1KB()
    {
        var key = new string('x', 1024);
        await WriteRow(key);
        var row = await Client.ReadRowAsync(TN, new BigtableByteString(key));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Key_with_reversed_timestamp_pattern()
    {
        // Common pattern: reversed timestamp for recent-first ordering
        var ts = long.MaxValue - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var key = $"user#reversed#{ts}";
        await WriteRow(key);
        var row = await Client.ReadRowAsync(TN, new BigtableByteString(key));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Binary_key_ordering()
    {
        // 0x00 < 0x01 < 0xFF in byte ordering
        var keys = new[]
        {
            new byte[] { 0x72, 0x6B, 0x65, 0x2D, 0xFF }, // "rke-" + 0xFF
            new byte[] { 0x72, 0x6B, 0x65, 0x2D, 0x00 }, // "rke-" + 0x00
            new byte[] { 0x72, 0x6B, 0x65, 0x2D, 0x7F }, // "rke-" + 0x7F
        };
        foreach (var k in keys)
            await WriteRow(k);

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowKeys =
                {
                    ByteString.CopyFrom(keys[0]),
                    ByteString.CopyFrom(keys[1]),
                    ByteString.CopyFrom(keys[2])
                }
            }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        // Should be sorted: 0x00, 0x7F, 0xFF
        rows.Should().HaveCount(3);
        rows[0].Key.ToByteArray().Last().Should().Be(0x00);
        rows[1].Key.ToByteArray().Last().Should().Be(0x7F);
        rows[2].Key.ToByteArray().Last().Should().Be(0xFF);
    }

    [Fact]
    public async Task Key_prefix_scan()
    {
        for (int i = 0; i < 5; i++)
            await WriteRow($"rke-prefix-{i}");
        await WriteRow("rke-other");

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges = { new RowRange
                {
                    StartKeyClosed = ByteString.CopyFromUtf8("rke-prefix-"),
                    EndKeyOpen = ByteString.CopyFromUtf8("rke-prefix-~")
                }}
            }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task Row_key_regex_filter_on_binary_key()
    {
        await WriteRow("rke-regex-abc");
        await WriteRow("rke-regex-def");
        await WriteRow("rke-regex-xyz");

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.RowKeyRegex("rke-regex-[ad].*")
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows.Select(r => r.Key.ToStringUtf8()).Should().BeEquivalentTo(
            new[] { "rke-regex-abc", "rke-regex-def" });
    }
}

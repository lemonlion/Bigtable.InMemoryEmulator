using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for row key binary sort order.
///
/// Ref: https://cloud.google.com/bigtable/docs/schema-design#row-keys
///   "Rows are sorted in ascending lexicographic binary order."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class RowKeySortOrderTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "rkso-test";
    private const string CF = "cf";

    public RowKeySortOrderTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<string>> ReadAllKeys(RowSet? rows = null)
    {
        var list = new List<string>();
        await foreach (var row in Client.ReadRows(TN, rows: rows))
            list.Add(row.Key.ToStringUtf8());
        return list;
    }

    private async Task WriteRow(string key)
    {
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
    }

    #region Basic string sort order

    [Fact]
    public async Task Alphabetical_keys_sorted()
    {
        await WriteRow("charlie");
        await WriteRow("alpha");
        await WriteRow("bravo");
        var keys = await ReadAllKeys();
        keys.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Numeric_string_sort_is_lexicographic()
    {
        // "10" < "2" lexicographically because '1' < '2'
        await WriteRow("2");
        await WriteRow("10");
        await WriteRow("1");
        var keys = await ReadAllKeys();
        // Lexicographic: "1", "10", "2"
        keys.Should().ContainInConsecutiveOrder("1", "10", "2");
    }

    [Fact]
    public async Task Padded_numeric_sort_is_natural()
    {
        await WriteRow("num-003");
        await WriteRow("num-001");
        await WriteRow("num-002");
        var keys = await ReadAllKeys(RowSet.FromRowKeys("num-001", "num-002", "num-003"));
        keys.Should().ContainInConsecutiveOrder("num-001", "num-002", "num-003");
    }

    #endregion

    #region Case sensitivity

    [Fact]
    public async Task Uppercase_sorts_before_lowercase()
    {
        // In ASCII/UTF-8: 'A'=0x41 < 'a'=0x61
        await WriteRow("apple");
        await WriteRow("Apple");
        await WriteRow("APPLE");
        var keys = await ReadAllKeys(RowSet.FromRowKeys("APPLE", "Apple", "apple"));
        keys.Should().ContainInConsecutiveOrder("APPLE", "Apple", "apple");
    }

    #endregion

    #region Special characters

    [Fact]
    public async Task Hash_separator_sort()
    {
        // '#' = 0x23, which is before letters
        await WriteRow("user#001");
        await WriteRow("user#002");
        await WriteRow("user-001"); // '-' = 0x2D, after '#'
        var keys = await ReadAllKeys(RowSet.FromRowKeys("user#001", "user#002", "user-001"));
        keys.Should().ContainInConsecutiveOrder("user#001", "user#002", "user-001");
    }

    [Fact]
    public async Task Short_key_sorts_before_longer()
    {
        // Empty row key is not allowed in Bigtable, but very short keys work
        await WriteRow("b");
        await WriteRow("a");
        await WriteRow("aa");
        var keys = await ReadAllKeys(RowSet.FromRowKeys("a", "aa", "b"));
        keys.Should().ContainInConsecutiveOrder("a", "aa", "b");
    }

    #endregion

    #region Prefix ordering

    [Fact]
    public async Task Shorter_prefix_sorts_before_longer()
    {
        await WriteRow("ab");
        await WriteRow("abc");
        await WriteRow("a");
        var keys = await ReadAllKeys(RowSet.FromRowKeys("a", "ab", "abc"));
        keys.Should().ContainInConsecutiveOrder("a", "ab", "abc");
    }

    [Fact]
    public async Task Hierarchical_keys_sorted_correctly()
    {
        var keysToWrite = new[]
        {
            "org#A#dept#1",
            "org#A#dept#2",
            "org#B#dept#1",
            "org#A",
            "org#B",
        };
        foreach (var k in keysToWrite)
            await WriteRow(k);
        var keys = await ReadAllKeys(RowSet.FromRowKeys(
            keysToWrite.Select(k => (BigtableByteString)k).ToArray()));
        keys.Should().ContainInConsecutiveOrder(
            "org#A", "org#A#dept#1", "org#A#dept#2", "org#B", "org#B#dept#1");
    }

    #endregion

    #region Binary keys

    [Fact]
    public async Task Binary_key_byte_ordering()
    {
        var key1 = ByteString.CopyFrom(new byte[] { 0x00, 0x01 });
        var key2 = ByteString.CopyFrom(new byte[] { 0x00, 0x02 });
        var key3 = ByteString.CopyFrom(new byte[] { 0x01, 0x00 });

        await Client.MutateRowAsync(TN, key3,
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, key1,
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, key2,
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var rowSet = new RowSet();
        rowSet.RowKeys.Add(key1);
        rowSet.RowKeys.Add(key2);
        rowSet.RowKeys.Add(key3);
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: rowSet))
            rows.Add(row);

        rows[0].Key.ToByteArray().Should().BeEquivalentTo(key1.ToByteArray());
        rows[1].Key.ToByteArray().Should().BeEquivalentTo(key2.ToByteArray());
        rows[2].Key.ToByteArray().Should().BeEquivalentTo(key3.ToByteArray());
    }

    #endregion

    #region Range scan ordering

    [Fact]
    public async Task Range_scan_returns_sorted()
    {
        var keysToWrite = new[] { "zz", "aa", "mm", "dd", "qq" };
        foreach (var k in keysToWrite)
            await WriteRow(k);
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("aa", "zz~"));
        var keys = await ReadAllKeys(rows: rowSet);
        keys.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Full_scan_always_sorted()
    {
        // Write in reverse order
        for (int i = 9; i >= 0; i--)
            await WriteRow($"sort-{i}");
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex("sort-.*")))
            rows.Add(row);
        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().BeInAscendingOrder();
    }

    #endregion
}

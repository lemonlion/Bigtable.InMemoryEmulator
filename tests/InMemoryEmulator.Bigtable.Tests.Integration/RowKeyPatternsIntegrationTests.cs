using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using System.Text;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Row key pattern integration tests — binary keys, composite keys, prefix scans,
/// delimiter patterns, Unicode keys, and boundary conditions.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
///   "Row keys are sorted in ascending lexicographic order by raw byte value."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class RowKeyPatternsIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "rowkey-tests";
    private const string CF = "cf";

    public RowKeyPatternsIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        await SeedData();
    }
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task SeedData()
    {
        // Composite keys: entity#id pattern
        var keys = new[]
        {
            "user#001", "user#002", "user#003", "user#010", "user#100",
            "order#001", "order#002", "order#003",
            "product#001", "product#002",
            // Keys with various characters
            "a", "b", "c", "aa", "ab", "ba", "bb",
            // Numeric-like keys
            "1", "2", "10", "20", "100",
        };
        foreach (var key in keys)
        {
            await Client.MutateRowAsync(TN, key,
                Mutations.SetCell(CF, "c", $"val-{key}", new BigtableVersion(1000)));
        }
    }

    #region Composite key patterns

    [Fact]
    public async Task Prefix_scan_user_keys()
    {
        var rows = await ReadRangeAsync("user#", "user$");
        rows.Should().HaveCount(5);
        rows.Select(r => r.Key.ToStringUtf8()).Should().OnlyContain(k => k.StartsWith("user#"));
    }

    [Fact]
    public async Task Prefix_scan_order_keys()
    {
        var rows = await ReadRangeAsync("order#", "order$");
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Prefix_scan_product_keys()
    {
        var rows = await ReadRangeAsync("product#", "product$");
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Prefix_scan_nonexistent_prefix_returns_empty()
    {
        var rows = await ReadRangeAsync("zzz#", "zzz$");
        rows.Should().BeEmpty();
    }

    #endregion

    #region Lexicographic ordering

    [Fact]
    public async Task Keys_are_in_lexicographic_byte_order()
    {
        var rows = await ReadAllAsync();
        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Numeric_keys_sort_lexicographically_not_numerically()
    {
        // "1" < "10" < "100" < "2" < "20" in lexicographic order
        var rows = await ReadRangeAsync("1", "3"); // Range [1, 3)
        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().Contain("1");
        keys.Should().Contain("10");
        keys.Should().Contain("100");
        keys.Should().Contain("2");
        keys.Should().Contain("20");
        keys.Should().BeInAscendingOrder(); // lex order, not numeric
    }

    [Fact]
    public async Task Short_keys_sort_before_longer_keys_with_same_prefix()
    {
        // "a" < "aa" < "ab" in lexicographic order
        var rows = await ReadRangeAsync("a", "ac");
        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().Equal("a", "aa", "ab");
    }

    #endregion

    #region Single character and short keys

    [Fact]
    public async Task Single_char_keys_ordered_correctly()
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN,
            RowSet.FromRowKeys("a", "b", "c")))
        {
            rows.Add(row);
        }
        rows.Should().HaveCount(3);
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task RowKeyRegex_matches_pattern()
    {
        var filter = RowFilters.RowKeyRegex("user#.*");
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, filter: filter))
        {
            rows.Add(row);
        }
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task RowKeyRegex_exact_match()
    {
        var filter = RowFilters.RowKeyRegex("user#001");
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, filter: filter))
        {
            rows.Add(row);
        }
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("user#001");
    }

    #endregion

    #region Binary row keys

    [Fact]
    public async Task Binary_row_key_roundtrips()
    {
        var binaryKey = new byte[] { 0x00, 0x01, 0x7F, 0x80, 0xFF };
        var rk = new BigtableByteString(binaryKey);
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "binary", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Key.ToByteArray().Should().Equal(binaryKey);
    }

    [Fact]
    public async Task Binary_key_with_null_bytes()
    {
        var keyWithNulls = new byte[] { 0x41, 0x00, 0x42, 0x00, 0x43 };
        var rk = new BigtableByteString(keyWithNulls);
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "nullbytes", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Key.ToByteArray().Should().Equal(keyWithNulls);
    }

    [Fact]
    public async Task Binary_keys_sort_by_unsigned_byte_value()
    {
        // 0x7F (127) should sort before 0x80 (128) in unsigned byte order
        var key1 = new byte[] { 0x7F };
        var key2 = new byte[] { 0x80 };
        await Client.MutateRowAsync(TN, new BigtableByteString(key1),
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(5000)));
        await Client.MutateRowAsync(TN, new BigtableByteString(key2),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(5000)));

        // Read both — 0x7F should come before 0x80
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN,
            RowSet.FromRowKeys(new BigtableByteString(key1), new BigtableByteString(key2))))
        {
            rows.Add(row);
        }
        rows.Should().HaveCount(2);
        rows[0].Key.ToByteArray()[0].Should().Be(0x7F);
        rows[1].Key.ToByteArray()[0].Should().Be(0x80);
    }

    #endregion

    #region Unicode keys

    [Fact]
    public async Task Unicode_key_roundtrips()
    {
        var unicodeKey = "日本語キー";
        var rk = new BigtableByteString(unicodeKey);
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "unicode", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Key.ToStringUtf8().Should().Be(unicodeKey);
    }

    [Fact]
    public async Task Emoji_key_roundtrips()
    {
        var emojiKey = "🔑key";
        await Client.MutateRowAsync(TN, emojiKey,
            Mutations.SetCell(CF, "c", "emoji", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, emojiKey);
        row.Should().NotBeNull();
        row!.Key.ToStringUtf8().Should().Be(emojiKey);
    }

    #endregion

    #region Key ranges with specific rows

    [Fact]
    public async Task Range_returns_rows_in_order()
    {
        var rows = await ReadRangeAsync("a", "c");
        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().BeInAscendingOrder();
        keys.Should().Contain("a");
        keys.Should().Contain("b");
        keys.Should().NotContain("c"); // exclusive end
    }

    [Fact]
    public async Task Range_exclusive_end_excludes_exact_match()
    {
        var rows = await ReadRangeAsync("user#001", "user#002");
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("user#001");
    }

    [Fact]
    public async Task ReadRow_nonexistent_key_returns_null()
    {
        var row = await Client.ReadRowAsync(TN, "definitely_not_exists_xyz");
        row.Should().BeNull();
    }

    [Fact]
    public async Task ReadRows_specific_keys_some_missing()
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN,
            RowSet.FromRowKeys("user#001", "nonexistent", "user#002")))
        {
            rows.Add(row);
        }
        rows.Should().HaveCount(2);
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("user#001", "user#002");
    }

    #endregion

    #region Large key sets

    [Fact]
    public async Task Read_many_specific_keys()
    {
        // Read all user keys by specific keys
        var keys = new[] { "user#001", "user#002", "user#003", "user#010", "user#100" }
            .Select(k => new BigtableByteString(k)).ToArray();
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys(keys)))
        {
            rows.Add(row);
        }
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task RowSet_combining_ranges_and_keys()
    {
        var rowSet = RowSet.FromRowRanges(RowRange.ClosedOpen("user#", "user$"));
        rowSet.RowKeys.Add(ByteString.CopyFromUtf8("order#001"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
        {
            rows.Add(row);
        }
        // 5 user rows + 1 order row = 6
        rows.Should().HaveCount(6);
    }

    #endregion

    #region Helpers

    private async Task<List<Row>> ReadRangeAsync(string startInclusive, string endExclusive)
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN,
            RowSet.FromRowRanges(RowRange.ClosedOpen(startInclusive, endExclusive))))
        {
            rows.Add(row);
        }
        return rows;
    }

    private async Task<List<Row>> ReadAllAsync()
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN))
        {
            rows.Add(row);
        }
        return rows;
    }

    #endregion
}

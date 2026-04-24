using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for row key encoding patterns — binary keys, UTF-8, special characters,
/// composite keys, and lexicographic sorting.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
/// "Row keys are sorted lexicographically by raw byte value."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class RowKeyEncodingStressTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "rowkey-encoding";
    private const string CF = "cf";

    public RowKeyEncodingStressTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task Write(string key) =>
        await Client.MutateRowAsync(TN, key, Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

    private async Task WriteBinary(byte[] key) =>
        await Client.MutateRowAsync(TN, new BigtableByteString(key),
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

    private async Task<List<Row>> ReadAll(RowSet? rows = null)
    {
        var list = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: rows))
            list.Add(row);
        return list;
    }

    #region Single character keys

    [Fact]
    public async Task Key_lowercase_letter()
    {
        await Write("rk-a");
        var rows = await ReadAll(RowSet.FromRowKeys("rk-a"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Key_uppercase_letter()
    {
        await Write("rk-A");
        var rows = await ReadAll(RowSet.FromRowKeys("rk-A"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Key_digit()
    {
        await Write("rk-0");
        var rows = await ReadAll(RowSet.FromRowKeys("rk-0"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Key_single_byte()
    {
        await Write("X");
        var rows = await ReadAll(RowSet.FromRowKeys("X"));
        rows.Should().ContainSingle();
    }

    #endregion

    #region Special character keys

    [Fact]
    public async Task Key_with_hyphens()
    {
        await Write("rk-with-hyphens");
        var rows = await ReadAll(RowSet.FromRowKeys("rk-with-hyphens"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Key_with_underscores()
    {
        await Write("rk_with_underscores");
        var rows = await ReadAll(RowSet.FromRowKeys("rk_with_underscores"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Key_with_dots()
    {
        await Write("rk.with.dots");
        var rows = await ReadAll(RowSet.FromRowKeys("rk.with.dots"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Key_with_hash()
    {
        await Write("rk#hash");
        var rows = await ReadAll(RowSet.FromRowKeys("rk#hash"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Key_with_colon_separator()
    {
        await Write("user:1234:profile");
        var rows = await ReadAll(RowSet.FromRowKeys("user:1234:profile"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Key_with_slash_separator()
    {
        await Write("org/team/user");
        var rows = await ReadAll(RowSet.FromRowKeys("org/team/user"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Key_with_pipe()
    {
        await Write("part1|part2");
        var rows = await ReadAll(RowSet.FromRowKeys("part1|part2"));
        rows.Should().ContainSingle();
    }

    #endregion

    #region Binary (non-UTF8) keys

    [Fact]
    public async Task Binary_key_with_null_byte()
    {
        var key = new byte[] { 0x01, 0x00, 0x02 };
        await WriteBinary(key);
        var rows = await ReadAll(RowSet.FromRowKeys(new BigtableByteString(key)));
        rows.Should().ContainSingle();
        rows[0].Key.ToByteArray().Should().Equal(key);
    }

    [Fact]
    public async Task Binary_key_all_zeros()
    {
        var key = new byte[] { 0x00, 0x00, 0x00 };
        await WriteBinary(key);
        var rows = await ReadAll(RowSet.FromRowKeys(new BigtableByteString(key)));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Binary_key_all_0xFF()
    {
        var key = new byte[] { 0xFF, 0xFF, 0xFF };
        await WriteBinary(key);
        var rows = await ReadAll(RowSet.FromRowKeys(new BigtableByteString(key)));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Binary_key_ascending_bytes()
    {
        var key = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 };
        await WriteBinary(key);
        var rows = await ReadAll(RowSet.FromRowKeys(new BigtableByteString(key)));
        rows.Should().ContainSingle();
        rows[0].Key.ToByteArray().Should().Equal(key);
    }

    [Fact]
    public async Task Binary_key_8_bytes_like_long()
    {
        var key = BitConverter.GetBytes(long.MaxValue);
        if (BitConverter.IsLittleEndian) Array.Reverse(key); // Big-endian for proper sorting
        await WriteBinary(key);
        var rows = await ReadAll(RowSet.FromRowKeys(new BigtableByteString(key)));
        rows.Should().ContainSingle();
    }

    #endregion

    #region UTF-8 keys

    [Fact]
    public async Task Unicode_key_accented_characters()
    {
        await Write("café");
        var rows = await ReadAll(RowSet.FromRowKeys("café"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Unicode_key_chinese()
    {
        await Write("中文键");
        var rows = await ReadAll(RowSet.FromRowKeys("中文键"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Unicode_key_emoji()
    {
        await Write("emoji-🎉");
        var rows = await ReadAll(RowSet.FromRowKeys("emoji-🎉"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Unicode_key_mixed_scripts()
    {
        await Write("abc-αβγ-中文");
        var rows = await ReadAll(RowSet.FromRowKeys("abc-αβγ-中文"));
        rows.Should().ContainSingle();
    }

    #endregion

    #region Composite / structured keys

    [Fact]
    public async Task Composite_key_reverse_timestamp()
    {
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long rev = long.MaxValue - ts;
        await Write($"user#001#{rev:D20}");
        var rows = await ReadAll(RowSet.FromRowKeys($"user#001#{rev:D20}"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Composite_key_with_zero_padding()
    {
        await Write("sensor#0001#20240101");
        var rows = await ReadAll(RowSet.FromRowKeys("sensor#0001#20240101"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Composite_key_prefix_scan()
    {
        for (int i = 0; i < 5; i++)
            await Write($"ckps#user#{i:D3}");
        for (int i = 0; i < 3; i++)
            await Write($"ckps#order#{i:D3}");

        var range = RowSet.FromRowRanges(RowRange.ClosedOpen("ckps#user#", "ckps#user~"));
        var rows = await ReadAll(range);
        rows.Should().HaveCount(5);
    }

    #endregion

    #region Sorting behavior

    [Fact]
    public async Task Digits_before_lowercase_in_ASCII()
    {
        // ASCII: '0'=0x30, 'A'=0x41, 'a'=0x61
        await Write("sort-0");
        await Write("sort-A");
        await Write("sort-a");
        var range = RowSet.FromRowRanges(RowRange.ClosedOpen("sort-", "sort-~"));
        var rows = await ReadAll(range);
        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().BeInAscendingOrder();
        keys.IndexOf("sort-0").Should().BeLessThan(keys.IndexOf("sort-A"));
        keys.IndexOf("sort-A").Should().BeLessThan(keys.IndexOf("sort-a"));
    }

    [Fact]
    public async Task Shorter_key_before_longer_same_prefix()
    {
        await Write("sortlen-ab");
        await Write("sortlen-abc");
        await Write("sortlen-a");
        var range = RowSet.FromRowRanges(RowRange.ClosedOpen("sortlen-", "sortlen-~"));
        var rows = await ReadAll(range);
        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().Equal("sortlen-a", "sortlen-ab", "sortlen-abc");
    }

    [Fact]
    public async Task Numeric_strings_sort_lexicographically()
    {
        await Write("sortnum-1");
        await Write("sortnum-10");
        await Write("sortnum-2");
        await Write("sortnum-20");
        await Write("sortnum-100");
        var range = RowSet.FromRowRanges(RowRange.ClosedOpen("sortnum-", "sortnum-~"));
        var rows = await ReadAll(range);
        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        // Lexicographic: "1" < "10" < "100" < "2" < "20"
        keys.Should().Equal("sortnum-1", "sortnum-10", "sortnum-100", "sortnum-2", "sortnum-20");
    }

    [Fact]
    public async Task Zero_padded_numbers_sort_numerically()
    {
        await Write("sortzp-001");
        await Write("sortzp-010");
        await Write("sortzp-002");
        await Write("sortzp-020");
        await Write("sortzp-100");
        var range = RowSet.FromRowRanges(RowRange.ClosedOpen("sortzp-", "sortzp-~"));
        var rows = await ReadAll(range);
        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().Equal("sortzp-001", "sortzp-002", "sortzp-010", "sortzp-020", "sortzp-100");
    }

    [Fact]
    public async Task Binary_keys_sort_by_unsigned_byte_order()
    {
        var keys = new[]
        {
            new byte[] { 0x00 },
            new byte[] { 0x01 },
            new byte[] { 0x7F },
            new byte[] { 0x80 },
            new byte[] { 0xFF },
        };
        foreach (var k in keys) await WriteBinary(k);

        var rows = await ReadAll();
        // Filter to single-byte keys
        var singleByteRows = rows.Where(r => r.Key.Length == 1).ToList();
        var bytes = singleByteRows.Select(r => r.Key.ToByteArray()[0]).ToList();
        // Byte order: 0x00 < 0x01 < 0x7F < 0x80 < 0xFF (unsigned)
        bytes.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Range_scan_with_binary_keys()
    {
        var keys = Enumerable.Range(0, 10).Select(i => new byte[] { (byte)(0x40 + i) }).ToArray();
        foreach (var k in keys) await WriteBinary(k);

        var start = new BigtableByteString(new byte[] { 0x42 });
        var end = new BigtableByteString(new byte[] { 0x47 });
        var range = RowSet.FromRowRanges(RowRange.ClosedOpen(start, end));
        var rows = await ReadAll(range);
        rows.Should().HaveCount(5); // 0x42..0x46
    }

    #endregion

    #region Max-size keys

    [Fact]
    public async Task Key_exactly_4KiB()
    {
        // Ref: Max row key size is 4 KiB
        var key = new string('k', 4096);
        await Write(key);
        var rows = await ReadAll(RowSet.FromRowKeys(key));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Key_near_max_size()
    {
        var key = new string('m', 4000);
        await Write(key);
        var rows = await ReadAll(RowSet.FromRowKeys(key));
        rows.Should().ContainSingle();
    }

    #endregion

    #region Edge case keys

    [Fact]
    public async Task Key_with_leading_spaces()
    {
        await Write("  leading");
        var rows = await ReadAll(RowSet.FromRowKeys("  leading"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Key_with_trailing_spaces()
    {
        await Write("trailing  ");
        var rows = await ReadAll(RowSet.FromRowKeys("trailing  "));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Key_with_tabs()
    {
        await Write("tab\there");
        var rows = await ReadAll(RowSet.FromRowKeys("tab\there"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Key_with_newlines()
    {
        await Write("line1\nline2");
        var rows = await ReadAll(RowSet.FromRowKeys("line1\nline2"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Multiple_similar_keys_are_distinct()
    {
        await Write("dist-abc");
        await Write("dist-abcd");
        await Write("dist-abcde");
        var range = RowSet.FromRowRanges(RowRange.ClosedOpen("dist-", "dist-~"));
        var rows = await ReadAll(range);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Overwrite_same_key_same_version()
    {
        await Client.MutateRowAsync(TN, "rk-ow", Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "rk-ow", Mutations.SetCell(CF, "c", "second", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("rk-ow"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("second");
    }

    #endregion
}

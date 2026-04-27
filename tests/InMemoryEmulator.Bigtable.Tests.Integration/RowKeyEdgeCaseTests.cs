using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for row key patterns: binary keys, special characters, ordering verification,
/// prefix scans, and boundary conditions.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
///   "Row keys are sorted lexicographically by raw byte ordering."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class RowKeyEdgeCaseTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "rk-edge";

    public RowKeyEdgeCaseTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    #region Special character keys

    [Fact]
    public async Task Key_with_slash()
    {
        await Client.MutateRowAsync(TN, "path/to/row",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "path/to/row");
        row!.Key.ToStringUtf8().Should().Be("path/to/row");
    }

    [Fact]
    public async Task Key_with_hash()
    {
        await Client.MutateRowAsync(TN, "user#123",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "user#123");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Key_with_spaces()
    {
        await Client.MutateRowAsync(TN, "row with spaces",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "row with spaces");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Key_with_dots()
    {
        await Client.MutateRowAsync(TN, "a.b.c.d",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "a.b.c.d");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Key_with_unicode()
    {
        await Client.MutateRowAsync(TN, "用户123",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "用户123");
        row!.Key.ToStringUtf8().Should().Be("用户123");
    }

    [Fact]
    public async Task Key_single_character()
    {
        await Client.MutateRowAsync(TN, "X",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "X");
        row.Should().NotBeNull();
    }

    #endregion

    #region Key length edge cases

    [Fact]
    public async Task Key_1_byte()
    {
        await Client.MutateRowAsync(TN, "a",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "a");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Key_256_bytes()
    {
        var key = new string('k', 256);
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, key);
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Key_4kb()
    {
        var key = new string('k', 4096);
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, key);
        row!.Key.ToStringUtf8().Should().HaveLength(4096);
    }

    #endregion

    #region Binary keys

    [Fact]
    public async Task Binary_key_with_null_bytes()
    {
        var keyBytes = ByteString.CopyFrom(new byte[] { 0x01, 0x00, 0x02 });
        await Client.MutateRowAsync(TN, keyBytes,
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, keyBytes);
        row.Should().NotBeNull();
        row!.Key.ToByteArray().Should().BeEquivalentTo(keyBytes.ToByteArray());
    }

    [Fact]
    public async Task Binary_key_all_zero()
    {
        var keyBytes = ByteString.CopyFrom(new byte[] { 0x00, 0x00, 0x00 });
        await Client.MutateRowAsync(TN, keyBytes,
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, keyBytes);
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Binary_key_all_ff()
    {
        var keyBytes = ByteString.CopyFrom(new byte[] { 0xFF, 0xFF, 0xFF });
        await Client.MutateRowAsync(TN, keyBytes,
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, keyBytes);
        row.Should().NotBeNull();
    }

    #endregion

    #region Key ordering

    [Fact]
    public async Task Lexicographic_byte_ordering()
    {
        var keys = new[] { "c", "a", "b", "d" };
        foreach (var k in keys)
            await Client.MutateRowAsync(TN, $"ord-{k}",
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var readKeys = new List<string>();
        await foreach (var row in Client.ReadRows(TN,
            RowSet.FromRowRanges(RowRange.ClosedOpen("ord-", "ord."))))
            readKeys.Add(row.Key.ToStringUtf8());
        readKeys.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Numeric_keys_sorted_lexicographically()
    {
        // "9" > "10" in byte ordering!
        var keys = new[] { "1", "10", "2", "9" };
        foreach (var k in keys)
            await Client.MutateRowAsync(TN, $"num-{k}",
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var readKeys = new List<string>();
        await foreach (var row in Client.ReadRows(TN,
            RowSet.FromRowRanges(RowRange.ClosedOpen("num-", "num."))))
            readKeys.Add(row.Key.ToStringUtf8());
        readKeys.Should().BeEquivalentTo(new[] { "num-1", "num-10", "num-2", "num-9" },
            options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task Case_sensitive_ordering()
    {
        // 'A'=0x41, 'Z'=0x5A, 'a'=0x61 → uppercase sorts before lowercase
        var keys = new[] { "a", "A", "z", "Z" };
        foreach (var k in keys)
            await Client.MutateRowAsync(TN, $"case-{k}",
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var readKeys = new List<string>();
        await foreach (var row in Client.ReadRows(TN,
            RowSet.FromRowRanges(RowRange.ClosedOpen("case-", "case."))))
            readKeys.Add(row.Key.ToStringUtf8());
        readKeys.Should().BeEquivalentTo(new[] { "case-A", "case-Z", "case-a", "case-z" },
            options => options.WithStrictOrdering());
    }

    #endregion

    #region Row key regex filter

    [Fact]
    public async Task RowKeyRegex_exact_match()
    {
        await Client.MutateRowAsync(TN, "rkr-exact",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var found = false;
        await foreach (var _ in Client.ReadRows(TN, rows: null,
            filter: RowFilters.RowKeyRegex("rkr-exact")))
            found = true;
        found.Should().BeTrue();
    }

    [Fact]
    public async Task RowKeyRegex_pattern()
    {
        for (int i = 0; i < 5; i++)
            await Client.MutateRowAsync(TN, $"rkpat-{i}",
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var count = 0;
        await foreach (var _ in Client.ReadRows(TN, rows: null,
            filter: RowFilters.RowKeyRegex("rkpat-[0-2]")))
            count++;
        count.Should().Be(3);
    }

    [Fact]
    public async Task RowKeyRegex_no_match()
    {
        await Client.MutateRowAsync(TN, "rkr-nomatch",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var found = false;
        await foreach (var _ in Client.ReadRows(TN, RowSet.FromRowKeys("rkr-nomatch"),
            filter: RowFilters.RowKeyRegex("rkr-other")))
            found = true;
        found.Should().BeFalse();
    }

    #endregion

    #region ReadRow vs ReadRows

    [Fact]
    public async Task ReadRow_nonexistent_returns_null()
    {
        var row = await Client.ReadRowAsync(TN, "rk-nonexist-xyz");
        row.Should().BeNull();
    }

    [Fact]
    public async Task ReadRows_single_key_returns_one()
    {
        await Client.MutateRowAsync(TN, "rk-single",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var count = 0;
        await foreach (var _ in Client.ReadRows(TN, RowSet.FromRowKeys("rk-single")))
            count++;
        count.Should().Be(1);
    }

    #endregion
}

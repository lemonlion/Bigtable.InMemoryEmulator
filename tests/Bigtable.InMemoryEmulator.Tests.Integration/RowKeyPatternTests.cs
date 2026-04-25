using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for row key patterns and edge cases: binary keys, long keys, special characters.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
///   "row_key: The key of the row to which the mutation should be applied."
/// Ref: https://cloud.google.com/bigtable/docs/schema-design#row-keys
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class RowKeyPatternTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "rk-pat";

    public RowKeyPatternTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task Single_character_row_key()
    {
        await Client.MutateRowAsync(TN, "x",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "x");
        row.Should().NotBeNull();
        row!.Key.ToStringUtf8().Should().Be("x");
    }

    [Fact]
    public async Task Numeric_row_key()
    {
        await Client.MutateRowAsync(TN, "12345",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "12345");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Row_key_with_hash_prefix()
    {
        // Common pattern: hash#key
        await Client.MutateRowAsync(TN, "abc123#user#42",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "abc123#user#42");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Row_key_with_slashes()
    {
        await Client.MutateRowAsync(TN, "path/to/resource",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "path/to/resource");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Row_key_with_dots()
    {
        await Client.MutateRowAsync(TN, "com.example.app",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "com.example.app");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Row_key_lexicographic_ordering()
    {
        // Verify lexicographic byte ordering
        await Client.MutateRowAsync(TN, "b",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "a",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "c",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var rowSet = new RowSet
        {
            RowRanges = { new RowRange { StartKeyClosed = ByteString.CopyFromUtf8("a"), EndKeyOpen = ByteString.CopyFromUtf8("d") } }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);

        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Binary_row_key()
    {
        var binaryKey = ByteString.CopyFrom(new byte[] { 0x00, 0x01, 0xFF, 0xFE });
        await Client.MutateRowAsync(TN, binaryKey,
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, binaryKey);
        row.Should().NotBeNull();
        row!.Key.ToByteArray().Should().BeEquivalentTo(binaryKey.ToByteArray());
    }

    [Fact]
    public async Task Row_key_with_unicode()
    {
        await Client.MutateRowAsync(TN, "こんにちは",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "こんにちは");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Row_key_with_spaces()
    {
        await Client.MutateRowAsync(TN, "hello world",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "hello world");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Row_key_with_null_bytes()
    {
        var keyWithNull = ByteString.CopyFrom(new byte[] { 0x61, 0x00, 0x62 }); // a\0b
        await Client.MutateRowAsync(TN, keyWithNull,
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, keyWithNull);
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Nonexistent_row_key_returns_null()
    {
        var row = await Client.ReadRowAsync(TN, "surely-does-not-exist-xyz");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Row_key_case_sensitive()
    {
        await Client.MutateRowAsync(TN, "CaseSensitive",
            Mutations.SetCell(CF, "c", "upper", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "casesensitive",
            Mutations.SetCell(CF, "c", "lower", new BigtableVersion(1000)));

        var upper = await Client.ReadRowAsync(TN, "CaseSensitive");
        var lower = await Client.ReadRowAsync(TN, "casesensitive");
        upper!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("upper");
        lower!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("lower");
    }
}

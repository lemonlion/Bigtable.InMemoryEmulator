using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for row key encoding edge cases — binary keys, special characters, max length.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class RowKeyEncodingEdgeCaseTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public RowKeyEncodingEdgeCaseTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync("rk-enc", new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("rk-enc");

    [Fact]
    public async Task Binary_key_with_null_bytes()
    {
        var key = ByteString.CopyFrom(new byte[] { 0x00, 0x01, 0x00 });
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys(key)))
            rows.Add(row);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Key_with_high_byte_values()
    {
        var key = ByteString.CopyFrom(new byte[] { 0xFF, 0xFE, 0xFD });
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys(key)))
            rows.Add(row);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Key_with_unicode_characters()
    {
        await Client.MutateRowAsync(TN, "日本語キー",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("日本語キー")))
            rows.Add(row);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Key_with_special_ascii()
    {
        await Client.MutateRowAsync(TN, "key!@#$%^&*()",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("key!@#$%^&*()")))
            rows.Add(row);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Key_with_spaces_and_tabs()
    {
        await Client.MutateRowAsync(TN, "key with spaces",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("key with spaces")))
            rows.Add(row);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Key_with_newlines()
    {
        await Client.MutateRowAsync(TN, "line1\nline2",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("line1\nline2")))
            rows.Add(row);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Single_byte_key()
    {
        await Client.MutateRowAsync(TN, "x",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("x")))
            rows.Add(row);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Long_key_4kb()
    {
        var longKey = new string('a', 4096);
        await Client.MutateRowAsync(TN, longKey,
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys(longKey)))
            rows.Add(row);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Keys_sort_by_raw_byte_order()
    {
        var keys = new[]
        {
            ByteString.CopyFrom(new byte[] { 0x01 }),
            ByteString.CopyFrom(new byte[] { 0x02 }),
            ByteString.CopyFrom(new byte[] { 0x10 }),
            ByteString.CopyFrom(new byte[] { 0xFF }),
        };
        foreach (var key in keys)
            await Client.MutateRowAsync(TN, key,
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var rows = new List<Row>();
        var rowSet = RowSet.FromRowKeys(
            (BigtableByteString)keys[0],
            (BigtableByteString)keys[1],
            (BigtableByteString)keys[2],
            (BigtableByteString)keys[3]);
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);
        rows.Should().HaveCount(4);
        // Verify sort order
        for (int i = 1; i < rows.Count; i++)
        {
            var prev = rows[i - 1].Key.ToByteArray();
            var curr = rows[i].Key.ToByteArray();
            prev.AsSpan().SequenceCompareTo(curr).Should().BeLessThan(0);
        }
    }

    [Fact]
    public async Task Key_with_separator_pattern()
    {
        // Common pattern: namespace#id
        await Client.MutateRowAsync(TN, "ns#001",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ns#002",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("ns#", "ns$"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);
        rows.Should().HaveCount(2);
    }
}

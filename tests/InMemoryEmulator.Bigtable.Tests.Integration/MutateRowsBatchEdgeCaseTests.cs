using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for MutateRows batch operation edge cases and semantics.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowsrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutateRowsBatchEdgeCaseTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public MutateRowsBatchEdgeCaseTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync("batch-edge", new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("batch-edge");

    private async Task<Row?> ReadRow(string key)
    {
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys(key)))
            return row;
        return null;
    }

    #region Basic batch operations

    [Fact]
    public async Task Batch_single_entry()
    {
        var entries = new[] {
            Mutations.CreateEntry("be-single", Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await ReadRow("be-single");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Batch_100_entries()
    {
        var entries = Enumerable.Range(0, 100).Select(i =>
            Mutations.CreateEntry($"be-100-{i:D4}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))).ToArray();
        await Client.MutateRowsAsync(TN, entries);

        int count = 0;
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("be-100-", "be-100."));
        await foreach (var _ in Client.ReadRows(TN, rowSet))
            count++;
        count.Should().Be(100);
    }

    [Fact]
    public async Task Batch_same_row_multiple_entries()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("be-dup", Mutations.SetCell(CF, "c1", "v1", new BigtableVersion(1000))),
            Mutations.CreateEntry("be-dup", Mutations.SetCell(CF, "c2", "v2", new BigtableVersion(1000))),
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await ReadRow("be-dup");
        row.Should().NotBeNull();
        var cols = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("c1").And.Contain("c2");
    }

    #endregion

    #region Multi-mutation entries

    [Fact]
    public async Task Batch_entry_with_multiple_mutations()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("be-multi",
                Mutations.SetCell(CF, "a", "va", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "b", "vb", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "c", "vc", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await ReadRow("be-multi");
        row!.Families[0].Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task Batch_entry_with_set_and_delete()
    {
        // Pre-seed
        await Client.MutateRowAsync(TN, "be-set-del",
            Mutations.SetCell(CF, "old", "val", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("be-set-del",
                Mutations.DeleteFromColumn(CF, "old"),
                Mutations.SetCell(CF, "new", "fresh", new BigtableVersion(2000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await ReadRow("be-set-del");
        var cols = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("new").And.NotContain("old");
    }

    [Fact]
    public async Task Batch_entry_with_delete_row()
    {
        await Client.MutateRowAsync(TN, "be-del-row",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var entries = new[]
        {
            Mutations.CreateEntry("be-del-row", Mutations.DeleteFromRow())
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await ReadRow("be-del-row");
        row.Should().BeNull();
    }

    #endregion

    #region Batch with versions

    [Fact]
    public async Task Batch_multiple_versions_same_cell()
    {
        var entries = Enumerable.Range(1, 5).Select(i =>
            Mutations.CreateEntry("be-versions",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)))).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var row = await ReadRow("be-versions");
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(5);
    }

    [Fact]
    public async Task Batch_overwrite_existing_version()
    {
        await Client.MutateRowAsync(TN, "be-overwrite",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));
        var entries = new[]
        {
            Mutations.CreateEntry("be-overwrite",
                Mutations.SetCell(CF, "c", "new", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await ReadRow("be-overwrite");
        var cell = row!.Families[0].Columns[0].Cells[0];
        cell.Value.ToStringUtf8().Should().Be("new");
    }

    #endregion

    #region Batch ordering

    [Fact]
    public async Task Batch_sequential_batches()
    {
        // First batch
        var entries1 = Enumerable.Range(0, 10).Select(i =>
            Mutations.CreateEntry($"be-seq-{i:D4}",
                Mutations.SetCell(CF, "c", "batch1", new BigtableVersion(1000)))).ToArray();
        await Client.MutateRowsAsync(TN, entries1);

        // Second batch — overwrites
        var entries2 = Enumerable.Range(0, 10).Select(i =>
            Mutations.CreateEntry($"be-seq-{i:D4}",
                Mutations.SetCell(CF, "c", "batch2", new BigtableVersion(2000)))).ToArray();
        await Client.MutateRowsAsync(TN, entries2);

        var row = await ReadRow("be-seq-0005");
        var cells = row!.Families[0].Columns[0].Cells;
        cells[0].Value.ToStringUtf8().Should().Be("batch2"); // Latest
    }

    #endregion

    #region Batch with binary keys

    [Fact]
    public async Task Batch_with_binary_row_keys()
    {
        var entries = new[]
        {
            Mutations.CreateEntry(ByteString.CopyFrom(new byte[] { 0x00, 0x01, 0x02 }),
                Mutations.SetCell(CF, "c", "binary1", new BigtableVersion(1000))),
            Mutations.CreateEntry(ByteString.CopyFrom(new byte[] { 0xFF, 0xFE, 0xFD }),
                Mutations.SetCell(CF, "c", "binary2", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);

        // Read back
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN,
            RowSet.FromRowKeys(
                ByteString.CopyFrom(new byte[] { 0x00, 0x01, 0x02 }),
                ByteString.CopyFrom(new byte[] { 0xFF, 0xFE, 0xFD }))))
            rows.Add(row);
        rows.Should().HaveCount(2);
    }

    #endregion

    #region Large values

    [Fact]
    public async Task Batch_with_large_values()
    {
        var largeVal = new string('x', 50000);
        var entries = Enumerable.Range(0, 5).Select(i =>
            Mutations.CreateEntry($"be-large-{i:D2}",
                Mutations.SetCell(CF, "big", largeVal, new BigtableVersion(1000)))).ToArray();
        await Client.MutateRowsAsync(TN, entries);

        for (int i = 0; i < 5; i++)
        {
            var row = await ReadRow($"be-large-{i:D2}");
            row.Should().NotBeNull();
            row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Length.Should().Be(50000);
        }
    }

    [Fact]
    public async Task Batch_many_columns_per_entry()
    {
        var mutations = Enumerable.Range(0, 50).Select(i =>
            Mutations.SetCell(CF, $"col-{i:D4}", $"v-{i}", new BigtableVersion(1000))).ToArray();
        var entries = new[] { Mutations.CreateEntry("be-50cols", mutations) };
        await Client.MutateRowsAsync(TN, entries);
        var row = await ReadRow("be-50cols");
        row!.Families[0].Columns.Should().HaveCount(50);
    }

    #endregion

    #region Empty and special values

    [Fact]
    public async Task Batch_empty_value()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("be-empty-val",
                Mutations.SetCell(CF, "c", ByteString.Empty, new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await ReadRow("be-empty-val");
        row!.Families[0].Columns[0].Cells[0].Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Batch_unicode_values()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("be-unicode",
                Mutations.SetCell(CF, "emoji", "Hello 🌍", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "chinese", "你好世界", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "arabic", "مرحبا", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await ReadRow("be-unicode");
        row!.Families[0].Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task Batch_null_bytes_in_value()
    {
        var valueWithNulls = ByteString.CopyFrom(new byte[] { 0x01, 0x00, 0x02, 0x00, 0x03 });
        var entries = new[]
        {
            Mutations.CreateEntry("be-nullbytes",
                Mutations.SetCell(CF, "c", valueWithNulls, new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await ReadRow("be-nullbytes");
        row!.Families[0].Columns[0].Cells[0].Value.ToByteArray()
            .Should().BeEquivalentTo(new byte[] { 0x01, 0x00, 0x02, 0x00, 0x03 });
    }

    #endregion
}

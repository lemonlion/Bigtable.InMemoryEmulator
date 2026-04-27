using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Stress tests for MutateRows (batch) operations with various patterns.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowsrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutateRowsBatchAdvancedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "batch-adv";
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public MutateRowsBatchAdvancedTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null)
    {
        var list = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: rows, filter: filter))
            list.Add(row);
        return list;
    }

    #region Batch sizes

    [Fact]
    public async Task Batch_1_entry()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("ba-1", Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("ba-1"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Batch_5_entries()
    {
        var entries = Enumerable.Range(0, 5).Select(i =>
            Mutations.CreateEntry($"ba-5-{i}", Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ba-5-", "ba-5~")));
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task Batch_20_entries()
    {
        var entries = Enumerable.Range(0, 20).Select(i =>
            Mutations.CreateEntry($"ba-20-{i:D3}", Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ba-20-", "ba-20~")));
        rows.Should().HaveCount(20);
    }

    [Fact]
    public async Task Batch_50_entries()
    {
        var entries = Enumerable.Range(0, 50).Select(i =>
            Mutations.CreateEntry($"ba-50-{i:D3}", Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ba-50-", "ba-50~")));
        rows.Should().HaveCount(50);
    }

    [Fact]
    public async Task Batch_100_entries()
    {
        var entries = Enumerable.Range(0, 100).Select(i =>
            Mutations.CreateEntry($"ba-100-{i:D3}", Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ba-100-", "ba-100~")));
        rows.Should().HaveCount(100);
    }

    #endregion

    #region Multi-mutation entries

    [Fact]
    public async Task Entry_with_3_mutations()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("ba-mm3",
                Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("ba-mm3"));
        rows[0].Families[0].Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task Entry_with_cross_family_mutations()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("ba-xf",
                Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
                Mutations.SetCell(CF2, "b", "2", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("ba-xf"));
        rows[0].Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Entry_with_set_and_delete()
    {
        // Pre-seed
        await Client.MutateRowAsync(TN, "ba-sd",
            Mutations.SetCell(CF, "old", "v", new BigtableVersion(1000)));
        var entries = new[]
        {
            Mutations.CreateEntry("ba-sd",
                Mutations.DeleteFromColumn(CF, "old"),
                Mutations.SetCell(CF, "new", "v", new BigtableVersion(2000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("ba-sd"));
        rows[0].Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Entry_with_delete_row()
    {
        await Client.MutateRowAsync(TN, "ba-dr",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var entries = new[] { Mutations.CreateEntry("ba-dr", Mutations.DeleteFromRow()) };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("ba-dr"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Entry_with_multiple_versions()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("ba-mv",
                Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("ba-mv"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(3);
    }

    #endregion

    #region Same-row entries in batch

    [Fact]
    public async Task Two_entries_same_row_different_columns()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("ba-sr-dc",
                Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000))),
            Mutations.CreateEntry("ba-sr-dc",
                Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("ba-sr-dc"));
        rows[0].Families[0].Columns.Should().HaveCount(2);
    }

    [Fact]
    public async Task Two_entries_same_row_same_column_different_versions()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("ba-sr-dv",
                Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000))),
            Mutations.CreateEntry("ba-sr-dv",
                Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("ba-sr-dv"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task Two_entries_same_row_overwrite()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("ba-sr-ow",
                Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000))),
            Mutations.CreateEntry("ba-sr-ow",
                Mutations.SetCell(CF, "c", "second", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("ba-sr-ow"));
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("second");
    }

    #endregion

    #region Sequential batches

    [Fact]
    public async Task Two_sequential_batches()
    {
        var entries1 = Enumerable.Range(0, 10).Select(i =>
            Mutations.CreateEntry($"ba-seq-{i:D3}", Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))
        ).ToArray();
        var entries2 = Enumerable.Range(10, 10).Select(i =>
            Mutations.CreateEntry($"ba-seq-{i:D3}", Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries1);
        await Client.MutateRowsAsync(TN, entries2);
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ba-seq-", "ba-seq~")));
        rows.Should().HaveCount(20);
    }

    [Fact]
    public async Task Batch_then_single_row_mutation()
    {
        var entries = Enumerable.Range(0, 5).Select(i =>
            Mutations.CreateEntry($"ba-bs-{i}", Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        await Client.MutateRowAsync(TN, "ba-bs-5",
            Mutations.SetCell(CF, "c", "v5", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ba-bs-", "ba-bs~")));
        rows.Should().HaveCount(6);
    }

    [Fact]
    public async Task Batch_update_existing_rows()
    {
        // Initial data
        var entries1 = Enumerable.Range(0, 5).Select(i =>
            Mutations.CreateEntry($"ba-upd-{i}", Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries1);

        // Update same rows
        var entries2 = Enumerable.Range(0, 5).Select(i =>
            Mutations.CreateEntry($"ba-upd-{i}", Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries2);

        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ba-upd-", "ba-upd~")),
            RowFilters.CellsPerColumnLimit(1));
        foreach (var row in rows)
            row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    #endregion

    #region Large values in batch

    [Fact]
    public async Task Batch_with_1KB_values()
    {
        var val = new string('X', 1024);
        var entries = Enumerable.Range(0, 10).Select(i =>
            Mutations.CreateEntry($"ba-1kb-{i}", Mutations.SetCell(CF, "c", val, new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ba-1kb-", "ba-1kb~")));
        rows.Should().HaveCount(10);
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Length.Should().Be(1024);
    }

    [Fact]
    public async Task Batch_with_empty_values()
    {
        var entries = Enumerable.Range(0, 5).Select(i =>
            Mutations.CreateEntry($"ba-empty-{i}", Mutations.SetCell(CF, "c", ByteString.Empty, new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ba-empty-", "ba-empty~")));
        rows.Should().HaveCount(5);
        rows[0].Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task Batch_with_binary_values()
    {
        var entries = Enumerable.Range(0, 5).Select(i =>
            Mutations.CreateEntry($"ba-bin-{i}",
                Mutations.SetCell(CF, "c", ByteString.CopyFrom(Enumerable.Range(0, 256).Select(x => (byte)x).ToArray()), new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ba-bin-", "ba-bin~")));
        rows.Should().HaveCount(5);
    }

    #endregion

    #region Batch ordering

    [Fact]
    public async Task Batch_ordering_preserved_in_reads()
    {
        var entries = Enumerable.Range(0, 10).Select(i =>
            Mutations.CreateEntry($"ba-ord-{i:D3}", Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ba-ord-", "ba-ord~")));
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Batch_reverse_key_order_still_sorted_in_reads()
    {
        var entries = Enumerable.Range(0, 10).Reverse().Select(i =>
            Mutations.CreateEntry($"ba-rev-{i:D3}", Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ba-rev-", "ba-rev~")));
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeInAscendingOrder();
    }

    #endregion

    #region Batch idempotency

    [Fact]
    public async Task Same_batch_twice_is_idempotent_for_same_version()
    {
        var entries = Enumerable.Range(0, 5).Select(i =>
            Mutations.CreateEntry($"ba-idem-{i}", Mutations.SetCell(CF, "c", "same", new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        await Client.MutateRowsAsync(TN, entries); // Same batch again
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ba-idem-", "ba-idem~")));
        rows.Should().HaveCount(5);
        foreach (var row in rows)
            row.Families[0].Columns[0].Cells.Should().ContainSingle();
    }

    #endregion
}

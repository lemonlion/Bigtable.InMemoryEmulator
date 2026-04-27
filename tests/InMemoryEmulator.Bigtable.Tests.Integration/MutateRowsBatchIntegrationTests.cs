using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// MutateRows batch integration tests — partial failures, large batches,
/// mixed success/failure entries, and error-per-entry semantics.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowsrequest
///   "Mutates multiple rows in a batch. Each individual row is mutated atomically."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutateRowsBatchIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "batch-tests";
    private const string CF = "cf";

    public MutateRowsBatchIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    #region Basic batch

    [Fact]
    public async Task MutateRows_single_entry_succeeds()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("batch-1", Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "batch-1");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task MutateRows_multiple_entries_all_succeed()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("batch-m1", Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000))),
            Mutations.CreateEntry("batch-m2", Mutations.SetCell(CF, "c", "v2", new BigtableVersion(1000))),
            Mutations.CreateEntry("batch-m3", Mutations.SetCell(CF, "c", "v3", new BigtableVersion(1000))),
        };
        await Client.MutateRowsAsync(TN, entries);

        // Verify all 3 rows exist
        for (int i = 1; i <= 3; i++)
        {
            var row = await Client.ReadRowAsync(TN, $"batch-m{i}");
            row.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task MutateRows_10_entries()
    {
        var entries = Enumerable.Range(1, 10)
            .Select(i => Mutations.CreateEntry($"batch-10-{i:D3}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000))))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);

        // Verify all 10 rows exist
        for (int i = 1; i <= 10; i++)
        {
            var row = await Client.ReadRowAsync(TN, $"batch-10-{i:D3}");
            row.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task MutateRows_50_entries()
    {
        var entries = Enumerable.Range(1, 50)
            .Select(i => Mutations.CreateEntry($"batch-50-{i:D3}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000))))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);

        // Spot-check some rows
        var first = await Client.ReadRowAsync(TN, "batch-50-001");
        first.Should().NotBeNull();
        var last = await Client.ReadRowAsync(TN, "batch-50-050");
        last.Should().NotBeNull();
    }

    #endregion

    #region Multiple mutations per entry

    [Fact]
    public async Task MutateRows_entry_with_multiple_mutations()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("batch-multi",
                Mutations.SetCell(CF, "a", "va", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "b", "vb", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "c", "vc", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "batch-multi");
        row!.Families[0].Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task MutateRows_entry_with_set_and_delete()
    {
        // Pre-populate
        await Client.MutateRowAsync(TN, "batch-sd",
            Mutations.SetCell(CF, "keep", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "remove", "v1", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("batch-sd",
                Mutations.SetCell(CF, "keep", "updated", new BigtableVersion(2000)),
                Mutations.DeleteFromColumn(CF, "remove"))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "batch-sd");
        var cols = row!.Families[0].Columns;
        cols.Should().Contain(c => c.Qualifier.ToStringUtf8() == "keep");
        cols.Should().NotContain(c => c.Qualifier.ToStringUtf8() == "remove");
    }

    #endregion

    #region Mixed success/failure

    [Fact]
    public async Task MutateRows_invalid_family_does_not_persist()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowsrequest
        //   Per-entry errors for invalid families should prevent the mutation from persisting.
        var entries = new MutateRowsRequest.Types.Entry[]
        {
            Mutations.CreateEntry("batch-bad-fam",
                Mutations.SetCell("nonexistent_family", "c", "v1", new BigtableVersion(1000))),
        };
        try
        {
            await Client.MutateRowsAsync(TN, entries);
        }
        catch
        {
            // SDK may throw for per-entry errors
        }
        // The entry with invalid family should NOT have been persisted
        var row = await Client.ReadRowAsync(TN, "batch-bad-fam");
        row.Should().BeNull();
    }

    #endregion

    #region Batch with versions

    [Fact]
    public async Task MutateRows_same_row_different_entries_creates_versions()
    {
        // Multiple entries targeting different timestamps on the same row
        var entries = Enumerable.Range(1, 5)
            .Select(i => Mutations.CreateEntry("batch-ver",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000))))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "batch-ver");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(5);
    }

    [Fact]
    public async Task MutateRows_batch_creates_distinct_rows()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("batch-dist-a", Mutations.SetCell(CF, "c", "a", new BigtableVersion(1000))),
            Mutations.CreateEntry("batch-dist-b", Mutations.SetCell(CF, "c", "b", new BigtableVersion(1000))),
            Mutations.CreateEntry("batch-dist-c", Mutations.SetCell(CF, "c", "c", new BigtableVersion(1000))),
        };
        await Client.MutateRowsAsync(TN, entries);

        // Read all and verify distinctness
        var rows = new List<Row>();
        var readResponse = Client.ReadRows(TN, RowSet.FromRowKeys("batch-dist-a", "batch-dist-b", "batch-dist-c"));
        await foreach (var row in readResponse)
        {
            rows.Add(row);
        }
        rows.Should().HaveCount(3);
    }

    #endregion

    #region Edge cases

    [Fact]
    public async Task MutateRows_same_row_same_column_same_timestamp_last_wins()
    {
        // Multiple entries writing to same cell — last write should win
        var entries = new[]
        {
            Mutations.CreateEntry("batch-lw", Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000))),
            Mutations.CreateEntry("batch-lw", Mutations.SetCell(CF, "c", "second", new BigtableVersion(1000))),
            Mutations.CreateEntry("batch-lw", Mutations.SetCell(CF, "c", "third", new BigtableVersion(1000))),
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "batch-lw");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("third");
    }

    [Fact]
    public async Task MutateRows_with_delete_from_row_in_entry()
    {
        await Client.MutateRowAsync(TN, "batch-del",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("batch-del", Mutations.DeleteFromRow())
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "batch-del");
        row.Should().BeNull();
    }

    [Fact]
    public async Task MutateRows_preserves_lexicographic_read_order()
    {
        // Write out of order
        var entries = new[]
        {
            Mutations.CreateEntry("zzz", Mutations.SetCell(CF, "c", "z", new BigtableVersion(1000))),
            Mutations.CreateEntry("aaa", Mutations.SetCell(CF, "c", "a", new BigtableVersion(1000))),
            Mutations.CreateEntry("mmm", Mutations.SetCell(CF, "c", "m", new BigtableVersion(1000))),
        };
        await Client.MutateRowsAsync(TN, entries);

        var rows = new List<Row>();
        var readResponse = Client.ReadRows(TN, RowSet.FromRowKeys("aaa", "mmm", "zzz"));
        await foreach (var row in readResponse)
        {
            rows.Add(row);
        }
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeInAscendingOrder();
    }

    #endregion
}

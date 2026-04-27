using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for batch (MutateRows) edge cases, per-entry errors, and result patterns.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowsrequest
///   "entries: Required. Each entry is applied atomically to the given row."
///   "The MutateRows response provides per-entry status."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class BatchMutationEdgeCaseTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public BatchMutationEdgeCaseTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync("batch-ec", new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("batch-ec");

    [Fact]
    public async Task Batch_single_entry()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("batch-single",
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "batch-single");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Batch_100_entries()
    {
        var entries = Enumerable.Range(0, 100).Select(i =>
            Mutations.CreateEntry($"batch-100-{i:D4}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))).ToArray();
        await Client.MutateRowsAsync(TN, entries);

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN,
            new RowSet
            {
                RowRanges =
                {
                    RowRange.ClosedOpen("batch-100-0000", "batch-100-9999")
                }
            }))
            rows.Add(row);

        rows.Should().HaveCount(100);
    }

    [Fact]
    public async Task Batch_same_row_key_multiple_entries()
    {
        // Multiple entries targeting the same row key
        var entries = new[]
        {
            Mutations.CreateEntry("batch-dup-key",
                Mutations.SetCell(CF, "c1", "v1", new BigtableVersion(1000))),
            Mutations.CreateEntry("batch-dup-key",
                Mutations.SetCell(CF, "c2", "v2", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "batch-dup-key");
        row.Should().NotBeNull();
        row!.Families[0].Columns.Should().HaveCount(2);
    }

    [Fact]
    public async Task Batch_entry_with_multiple_mutations()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("batch-multi-mut",
                Mutations.SetCell(CF, "c1", "v1", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "c2", "v2", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "c3", "v3", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "batch-multi-mut");
        row!.Families[0].Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task Batch_set_and_delete_different_rows()
    {
        // Seed a row to delete
        await Client.MutateRowAsync(TN, "batch-del-target",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("batch-new-row",
                Mutations.SetCell(CF, "c", "new", new BigtableVersion(1000))),
            Mutations.CreateEntry("batch-del-target",
                Mutations.DeleteFromRow())
        };
        await Client.MutateRowsAsync(TN, entries);

        var newRow = await Client.ReadRowAsync(TN, "batch-new-row");
        newRow.Should().NotBeNull();

        var delRow = await Client.ReadRowAsync(TN, "batch-del-target");
        delRow.Should().BeNull();
    }

    [Fact]
    public async Task Batch_with_server_assigned_timestamps()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("batch-ts-1",
                Mutations.SetCell(CF, "c", "v1", new BigtableVersion(-1))),
            Mutations.CreateEntry("batch-ts-2",
                Mutations.SetCell(CF, "c", "v2", new BigtableVersion(-1)))
        };
        await Client.MutateRowsAsync(TN, entries);

        var r1 = await Client.ReadRowAsync(TN, "batch-ts-1");
        var r2 = await Client.ReadRowAsync(TN, "batch-ts-2");
        r1!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().BeGreaterThan(0);
        r2!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Batch_idempotent_same_data()
    {
        // Same batch applied twice should produce same result (SetCell is idempotent at same timestamp)
        var entries = Enumerable.Range(0, 5).Select(i =>
            Mutations.CreateEntry($"batch-idem-{i}",
                Mutations.SetCell(CF, "c", "fixed", new BigtableVersion(1000)))).ToArray();

        await Client.MutateRowsAsync(TN, entries);
        await Client.MutateRowsAsync(TN, entries); // Apply again

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN,
            new RowSet
            {
                RowRanges = { RowRange.ClosedOpen("batch-idem-0", "batch-idem-9") }
            }))
            rows.Add(row);

        rows.Should().HaveCount(5);
        foreach (var row in rows)
            row.Families[0].Columns[0].Cells.Should().ContainSingle();
    }

    [Fact]
    public async Task Batch_multiple_versions_same_column()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("batch-ver",
                Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "batch-ver");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(3);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v3"); // newest first
    }

    [Fact]
    public async Task Batch_across_multiple_families()
    {
        await _fixture.CreateTableAsync("batch-mf", new[] { "f1", "f2", "f3" });
        var tn = _fixture.GetTableName("batch-mf");
        var entries = new[]
        {
            Mutations.CreateEntry("mf-row",
                Mutations.SetCell("f1", "c", "v1", new BigtableVersion(1000)),
                Mutations.SetCell("f2", "c", "v2", new BigtableVersion(1000)),
                Mutations.SetCell("f3", "c", "v3", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(tn, entries);

        var row = await Client.ReadRowAsync(tn, "mf-row");
        row!.Families.Should().HaveCount(3);
    }

    [Fact]
    public async Task Concurrent_batch_operations()
    {
        var tasks = Enumerable.Range(0, 10).Select(batch =>
        {
            var entries = Enumerable.Range(0, 10).Select(i =>
                Mutations.CreateEntry($"conc-batch-{batch:D2}-{i:D2}",
                    Mutations.SetCell(CF, "c", $"v{batch}-{i}", new BigtableVersion(1000)))).ToArray();
            return Client.MutateRowsAsync(TN, entries);
        }).ToArray();

        await Task.WhenAll(tasks);

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN,
            new RowSet
            {
                RowRanges = { RowRange.ClosedOpen("conc-batch-", "conc-batch-~") }
            }))
            rows.Add(row);

        rows.Should().HaveCount(100);
    }

    [Fact]
    public async Task Batch_delete_then_set_same_row_in_separate_entries()
    {
        await Client.MutateRowAsync(TN, "batch-ds",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("batch-ds", Mutations.DeleteFromRow()),
            Mutations.CreateEntry("batch-ds",
                Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "batch-ds");
        // The second entry's Set should survive since entries are independent
        row.Should().NotBeNull();
    }
}

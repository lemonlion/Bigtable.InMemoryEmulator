using System.Collections.Generic;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for MutateRows (batch) error handling and partial failure behavior.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowsrequest
///   "Mutates multiple rows in a batch. Each individual row is mutated atomically."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutateRowsBatchErrorTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "batch-err";

    public MutateRowsBatchErrorTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task Batch_single_entry_succeeds()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("be-r1", Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "be-r1");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Batch_multiple_entries_all_succeed()
    {
        var entries = Enumerable.Range(1, 5)
            .Select(i => Mutations.CreateEntry($"be-r2-{i}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000))))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);
        for (int i = 1; i <= 5; i++)
        {
            var row = await Client.ReadRowAsync(TN, $"be-r2-{i}");
            row.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Batch_with_delete_entries()
    {
        await Client.MutateRowAsync(TN, "be-r3",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var entries = new[]
        {
            Mutations.CreateEntry("be-r3", Mutations.DeleteFromRow())
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "be-r3");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Batch_mixed_set_and_delete()
    {
        await Client.MutateRowAsync(TN, "be-r4-del",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));
        var entries = new[]
        {
            Mutations.CreateEntry("be-r4-new",
                Mutations.SetCell(CF, "c", "new", new BigtableVersion(1000))),
            Mutations.CreateEntry("be-r4-del", Mutations.DeleteFromRow())
        };
        await Client.MutateRowsAsync(TN, entries);
        (await Client.ReadRowAsync(TN, "be-r4-new")).Should().NotBeNull();
        (await Client.ReadRowAsync(TN, "be-r4-del")).Should().BeNull();
    }

    [Fact]
    public async Task Batch_multiple_mutations_per_entry()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("be-r5",
                Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "be-r5");
        row!.Families[0].Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task Batch_large_number_of_entries()
    {
        var entries = Enumerable.Range(1, 50)
            .Select(i => Mutations.CreateEntry($"be-r6-{i:D3}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000))))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex("be-r6-.*")))
            rows.Add(__row);
        rows.Should().HaveCount(50);
    }

    [Fact]
    public async Task Batch_with_version_range_delete()
    {
        await Client.MutateRowAsync(TN, "be-r7",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        var entries = new[]
        {
            Mutations.CreateEntry("be-r7",
                Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(1000, 3000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "be-r7");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v3");
    }

    [Fact]
    public async Task Batch_same_row_twice()
    {
        // Bigtable allows the same row key to appear in multiple entries
        var entries = new[]
        {
            Mutations.CreateEntry("be-r8",
                Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000))),
            Mutations.CreateEntry("be-r8",
                Mutations.SetCell(CF, "c", "second", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "be-r8");
        // Second entry overwrites (same timestamp)
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task Batch_delete_nonexistent_row_succeeds()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("be-r9-missing", Mutations.DeleteFromRow())
        };
        // Should not throw
        await Client.MutateRowsAsync(TN, entries);
    }

    [Fact]
    public async Task Batch_preserves_other_rows()
    {
        await Client.MutateRowAsync(TN, "be-r10-keep",
            Mutations.SetCell(CF, "c", "keep", new BigtableVersion(1000)));
        var entries = new[]
        {
            Mutations.CreateEntry("be-r10-new",
                Mutations.SetCell(CF, "c", "new", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        (await Client.ReadRowAsync(TN, "be-r10-keep")).Should().NotBeNull();
        (await Client.ReadRowAsync(TN, "be-r10-new")).Should().NotBeNull();
    }
}

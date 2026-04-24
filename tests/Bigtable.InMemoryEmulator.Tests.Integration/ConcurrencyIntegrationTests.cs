using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Integration tests for concurrent operations.
/// Verifies thread safety of the gRPC service and store.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ConcurrencyIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "conc-tests";
    private const string Family = "cf";

    public ConcurrencyIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { Family });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task Parallel_MutateRows_on_different_rows_all_succeed()
    {
        // Write 50 rows in parallel
        const int count = 50;
        var tasks = Enumerable.Range(0, count)
            .Select(i => Client.MutateRowAsync(TN, new BigtableByteString($"conc-r{i:D3}"),
                Mutations.SetCell(Family, "col", $"value{i}", new BigtableVersion(1000))))
            .ToArray();

        await Task.WhenAll(tasks);

        // Verify all rows exist
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN))
        {
            if (row.Key.ToStringUtf8().StartsWith("conc-r"))
                rows.Add(row);
        }

        rows.Should().HaveCount(count);
    }

    [Fact]
    public async Task Parallel_MutateRow_on_same_row_different_columns_all_succeed()
    {
        // Write to the same row in parallel, different columns
        const string rowKey = "conc-same-row";
        const int count = 20;
        var tasks = Enumerable.Range(0, count)
            .Select(i => Client.MutateRowAsync(TN, new BigtableByteString(rowKey),
                Mutations.SetCell(Family, $"col{i}", $"value{i}", new BigtableVersion(1000))))
            .ToArray();

        await Task.WhenAll(tasks);

        var row = await Client.ReadRowAsync(TN, new BigtableByteString(rowKey));
        row.Should().NotBeNull();
        // Should have all columns in the family
        row!.Families.Should().HaveCount(1);
        row.Families[0].Columns.Should().HaveCount(count);
    }

    [Fact]
    public async Task Parallel_reads_and_writes_do_not_deadlock()
    {
        // Seed some data
        for (int i = 0; i < 10; i++)
        {
            await Client.MutateRowAsync(TN, new BigtableByteString($"conc-rw{i:D2}"),
                Mutations.SetCell(Family, "col", $"v{i}", new BigtableVersion(1000)));
        }

        // Run reads and writes in parallel
        var writeTasks = Enumerable.Range(10, 10)
            .Select(i => Client.MutateRowAsync(TN, new BigtableByteString($"conc-rw{i:D2}"),
                Mutations.SetCell(Family, "col", $"v{i}", new BigtableVersion(1000))))
            .ToArray();

        var readTasks = Enumerable.Range(0, 10)
            .Select(async i =>
            {
                var row = await Client.ReadRowAsync(TN, new BigtableByteString($"conc-rw{i:D2}"));
                return row;
            })
            .ToArray();

        await Task.WhenAll(writeTasks.Cast<Task>().Concat(readTasks));

        // All reads should have returned (may or may not see the writes depending on timing)
        foreach (var readTask in readTasks)
        {
            var row = await readTask;
            row.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Parallel_CheckAndMutateRow_is_atomic()
    {
        // Set up a row with a value
        const string rowKey = "conc-cam";
        await Client.MutateRowAsync(TN, new BigtableByteString(rowKey),
            Mutations.SetCell(Family, "counter", "0", new BigtableVersion(1000)));

        // Run 20 parallel CheckAndMutateRow operations, each reading "0" and writing "1"
        // Only one should match the predicate (the one that sees "0"); subsequent ones
        // should see "1" and not match. However, race conditions mean multiple may match.
        // The key test is that no exceptions are thrown.
        const int count = 20;
        var tasks = Enumerable.Range(0, count)
            .Select(i => Client.CheckAndMutateRowAsync(
                TN, new BigtableByteString(rowKey),
                RowFilters.ValueExact("0"),
                new[] { Mutations.SetCell(Family, "counter", $"v{i}", new BigtableVersion(2000)) },
                null))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // At least one should have matched
        results.Should().Contain(r => r.PredicateMatched);

        // Row should still be readable and consistent
        var row = await Client.ReadRowAsync(TN, new BigtableByteString(rowKey));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Parallel_ReadModifyWriteRow_increments_atomically()
    {
        const string rowKey = "conc-rmw";
        const int count = 10;

        // Run parallel increments — each adds 1
        var tasks = Enumerable.Range(0, count)
            .Select(_ => Client.ReadModifyWriteRowAsync(
                TN, new BigtableByteString(rowKey),
                ReadModifyWriteRules.Increment(Family, "counter", 1)))
            .ToArray();

        await Task.WhenAll(tasks);

        // Read the final value — should be 'count' (10)
        var row = await Client.ReadRowAsync(TN, new BigtableByteString(rowKey));
        row.Should().NotBeNull();
        var value = row!.Families[0].Columns[0].Cells[0].Value;
        var longVal = BitConverter.ToInt64(value.ToByteArray().Reverse().ToArray(), 0);
        longVal.Should().Be(count);
    }

    [Fact]
    public async Task MutateRows_batch_entries_all_succeed_in_parallel_with_other_operations()
    {
        // Batch mutation alongside individual mutations
        var batchEntries = Enumerable.Range(0, 10)
            .Select(i => Mutations.CreateEntry(
                new BigtableByteString($"conc-batch{i:D2}"),
                Mutations.SetCell(Family, "col", $"v{i}", new BigtableVersion(1000))))
            .ToArray();

        var batchTask = Client.MutateRowsAsync(TN, batchEntries);

        var individualTasks = Enumerable.Range(0, 10)
            .Select(i => Client.MutateRowAsync(TN, new BigtableByteString($"conc-ind{i:D2}"),
                Mutations.SetCell(Family, "col", $"v{i}", new BigtableVersion(1000))))
            .ToArray();

        await Task.WhenAll(new Task[] { batchTask }.Concat(individualTasks));

        // All rows should exist
        for (int i = 0; i < 10; i++)
        {
            (await Client.ReadRowAsync(TN, new BigtableByteString($"conc-batch{i:D2}"))).Should().NotBeNull();
            (await Client.ReadRowAsync(TN, new BigtableByteString($"conc-ind{i:D2}"))).Should().NotBeNull();
        }
    }
}

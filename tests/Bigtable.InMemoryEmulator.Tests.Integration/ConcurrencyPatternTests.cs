using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for concurrent operations — parallel reads, writes, and mutations.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ConcurrencyPatternTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public ConcurrencyPatternTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync("conc-test", new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("conc-test");

    #region Parallel writes

    [Fact]
    public async Task Parallel_writes_to_different_rows_all_succeed()
    {
        var tasks = Enumerable.Range(0, 50).Select(i =>
            Client.MutateRowAsync(TN, $"par-w-{i:D4}",
                Mutations.SetCell(CF, "c", $"v-{i}", new BigtableVersion(1000))));
        await Task.WhenAll(tasks);

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN))
            rows.Add(row);
        rows.Should().HaveCountGreaterThanOrEqualTo(50);
    }

    [Fact]
    public async Task Parallel_writes_to_same_row_different_columns()
    {
        var tasks = Enumerable.Range(0, 20).Select(i =>
            Client.MutateRowAsync(TN, "par-same-row",
                Mutations.SetCell(CF, $"col-{i:D3}", $"v-{i}", new BigtableVersion(1000))));
        await Task.WhenAll(tasks);

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("par-same-row")))
            rows.Add(row);
        rows.Should().ContainSingle();
        var cols = rows[0].Families.SelectMany(f => f.Columns).ToList();
        cols.Should().HaveCount(20);
    }

    [Fact]
    public async Task Parallel_writes_to_same_cell_different_versions()
    {
        var tasks = Enumerable.Range(1, 10).Select(i =>
            Client.MutateRowAsync(TN, "par-versions",
                Mutations.SetCell(CF, "c", $"v-{i}", new BigtableVersion(i * 1000))));
        await Task.WhenAll(tasks);

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("par-versions")))
            rows.Add(row);
        rows.Should().ContainSingle();
        var cells = rows[0].Families.SelectMany(f => f.Columns.SelectMany(c => c.Cells)).ToList();
        cells.Should().HaveCount(10);
    }

    #endregion

    #region Parallel reads

    [Fact]
    public async Task Parallel_reads_return_consistent_data()
    {
        // Seed data
        for (int i = 0; i < 10; i++)
            await Client.MutateRowAsync(TN, $"par-read-{i:D2}",
                Mutations.SetCell(CF, "c", $"v-{i}", new BigtableVersion(1000)));

        // Parallel reads
        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            var rows = new List<Row>();
            await foreach (var row in Client.ReadRows(TN,
                RowSet.FromRowKeys("par-read-00", "par-read-01", "par-read-02")))
                rows.Add(row);
            return rows.Count;
        });
        var results = await Task.WhenAll(tasks);
        results.Should().AllSatisfy(count => count.Should().Be(3));
    }

    [Fact]
    public async Task Parallel_reads_with_different_filters()
    {
        for (int i = 0; i < 5; i++)
            await Client.MutateRowAsync(TN, $"par-filt-{i:D2}",
                Mutations.SetCell(CF, "c", $"val-{i}", new BigtableVersion(1000)));

        var task1 = ReadCount(RowFilters.ValueRegex("val-0"));
        var task2 = ReadCount(RowFilters.ValueRegex("val-1"));
        var task3 = ReadCount(RowFilters.ValueRegex("val-2"));
        var results = await Task.WhenAll(task1, task2, task3);
        results.Should().AllSatisfy(c => c.Should().BeGreaterThanOrEqualTo(1));

        async Task<int> ReadCount(RowFilter filter)
        {
            int count = 0;
            await foreach (var _ in Client.ReadRows(TN, rows: null, filter))
                count++;
            return count;
        }
    }

    #endregion

    #region Parallel RMW

    [Fact]
    public async Task Parallel_RMW_increments_are_cumulative()
    {
        // Initialize counter
        await Client.ReadModifyWriteRowAsync(TN, "par-rmw-counter",
            ReadModifyWriteRules.Increment(CF, "count", 0));

        // Parallel increments
        var tasks = Enumerable.Range(0, 20).Select(_ =>
            Client.ReadModifyWriteRowAsync(TN, "par-rmw-counter",
                ReadModifyWriteRules.Increment(CF, "count", 1)));
        await Task.WhenAll(tasks);

        // Read final value
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("par-rmw-counter")))
            rows.Add(row);
        rows.Should().ContainSingle();
        var cell = rows[0].Families[0].Columns[0].Cells[0];
        var val = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(cell.Value.Span);
        val.Should().Be(20);
    }

    [Fact]
    public async Task Parallel_RMW_appends_to_different_columns()
    {
        var tasks = Enumerable.Range(0, 10).Select(i =>
            Client.ReadModifyWriteRowAsync(TN, "par-rmw-cols",
                ReadModifyWriteRules.Append(CF, $"col-{i:D2}", $"v-{i}")));
        await Task.WhenAll(tasks);

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("par-rmw-cols")))
            rows.Add(row);
        rows.Should().ContainSingle();
        var cols = rows[0].Families.SelectMany(f => f.Columns).ToList();
        cols.Should().HaveCount(10);
    }

    #endregion

    #region Parallel CheckAndMutate

    [Fact]
    public async Task Parallel_CaM_on_different_rows()
    {
        // Seed rows
        for (int i = 0; i < 10; i++)
            await Client.MutateRowAsync(TN, $"par-cam-{i:D2}",
                Mutations.SetCell(CF, "status", "active", new BigtableVersion(1000)));

        // Parallel CaM
        var tasks = Enumerable.Range(0, 10).Select(i =>
            Client.CheckAndMutateRowAsync(TN, $"par-cam-{i:D2}",
                predicateFilter: RowFilters.ValueRegex("active"),
                trueMutations: new[] { Mutations.SetCell(CF, "status", "done", new BigtableVersion(2000)) },
                falseMutations: null));
        var results = await Task.WhenAll(tasks);
        results.Should().AllSatisfy(r => r.PredicateMatched.Should().BeTrue());
    }

    #endregion

    #region Mixed operations

    [Fact]
    public async Task Parallel_write_and_read_different_rows()
    {
        // Pre-seed some rows
        for (int i = 0; i < 5; i++)
            await Client.MutateRowAsync(TN, $"mix-pre-{i:D2}",
                Mutations.SetCell(CF, "c", "pre", new BigtableVersion(1000)));

        // Parallel writes + reads
        var writeTasks = Enumerable.Range(0, 5).Select(i =>
            Client.MutateRowAsync(TN, $"mix-new-{i:D2}",
                Mutations.SetCell(CF, "c", "new", new BigtableVersion(1000)))).Cast<Task>();

        var readTasks = Enumerable.Range(0, 5).Select(async i =>
        {
            var rows = new List<Row>();
            await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys($"mix-pre-{i:D2}")))
                rows.Add(row);
            return rows.Count;
        }).Cast<Task>();

        await Task.WhenAll(writeTasks.Concat(readTasks));
    }

    [Fact]
    public async Task Parallel_batch_mutations()
    {
        var tasks = Enumerable.Range(0, 5).Select(batch =>
        {
            var entries = Enumerable.Range(0, 10).Select(i =>
                Mutations.CreateEntry($"batch-{batch:D2}-{i:D2}",
                    Mutations.SetCell(CF, "c", $"v-{batch}-{i}", new BigtableVersion(1000)))).ToArray();
            return Client.MutateRowsAsync(TN, entries);
        }).Cast<Task>();
        await Task.WhenAll(tasks);

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null,
            RowFilters.RowKeyRegex("batch-.*")))
            rows.Add(row);
        rows.Should().HaveCount(50);
    }

    [Fact]
    public async Task Parallel_deletes_followed_by_reads()
    {
        // Seed rows
        for (int i = 0; i < 10; i++)
            await Client.MutateRowAsync(TN, $"par-del-{i:D2}",
                Mutations.SetCell(CF, "c", $"v-{i}", new BigtableVersion(1000)));

        // Delete all in parallel
        var tasks = Enumerable.Range(0, 10).Select(i =>
            Client.MutateRowAsync(TN, $"par-del-{i:D2}", Mutations.DeleteFromRow()));
        await Task.WhenAll(tasks);

        // Read should find none
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null,
            RowFilters.RowKeyRegex("par-del-.*")))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    #endregion
}

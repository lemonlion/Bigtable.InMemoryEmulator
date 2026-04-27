using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for concurrent operations across multiple tasks.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowrequest
///   "Mutations are applied atomically and in order to the specified row."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ConcurrencyAdvancedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "conc-adv";
    private const string CF = "cf";

    public ConcurrencyAdvancedTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
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

    #region Concurrent writes to different rows

    [Fact]
    public async Task Parallel_writes_to_different_rows()
    {
        var tasks = Enumerable.Range(0, 20).Select(i =>
            Client.MutateRowAsync(TN, $"ca-par-{i:D3}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))
        ).ToList();
        await Task.WhenAll(tasks);
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ca-par-", "ca-par~")));
        rows.Should().HaveCount(20);
    }

    [Fact]
    public async Task Parallel_batch_writes()
    {
        var tasks = Enumerable.Range(0, 5).Select(batch =>
        {
            var entries = Enumerable.Range(0, 10).Select(i =>
                Mutations.CreateEntry($"ca-pbw-{batch}-{i}",
                    Mutations.SetCell(CF, "c", $"b{batch}v{i}", new BigtableVersion(1000)))
            ).ToArray();
            return Client.MutateRowsAsync(TN, entries);
        }).ToList();
        await Task.WhenAll(tasks);
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ca-pbw-", "ca-pbw~")));
        rows.Should().HaveCount(50);
    }

    #endregion

    #region Concurrent writes to same row

    [Fact]
    public async Task Parallel_writes_same_row_different_columns()
    {
        var tasks = Enumerable.Range(0, 10).Select(i =>
            Client.MutateRowAsync(TN, "ca-same-cols",
                Mutations.SetCell(CF, $"col-{i}", $"v{i}", new BigtableVersion(1000)))
        ).ToList();
        await Task.WhenAll(tasks);
        var rows = await ReadAll(RowSet.FromRowKeys("ca-same-cols"));
        rows[0].Families[0].Columns.Should().HaveCount(10);
    }

    [Fact]
    public async Task Parallel_writes_same_row_different_versions()
    {
        var tasks = Enumerable.Range(1, 10).Select(v =>
            Client.MutateRowAsync(TN, "ca-same-ver",
                Mutations.SetCell(CF, "c", $"v{v}", new BigtableVersion(v * 1000)))
        ).ToList();
        await Task.WhenAll(tasks);
        var rows = await ReadAll(RowSet.FromRowKeys("ca-same-ver"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(10);
    }

    #endregion

    #region Concurrent reads and writes

    [Fact]
    public async Task Read_during_writes()
    {
        // Seed initial data
        for (int i = 0; i < 10; i++)
            await Client.MutateRowAsync(TN, $"ca-rw-{i:D2}",
                Mutations.SetCell(CF, "c", "initial", new BigtableVersion(1000)));
        // Read and write concurrently
        var readTask = Task.Run(async () =>
        {
            var rows = new List<Row>();
            await foreach (var row in Client.ReadRows(TN, RowSet.FromRowRanges(RowRange.ClosedOpen("ca-rw-", "ca-rw~"))))
                rows.Add(row);
            return rows;
        });
        var writeTasks = Enumerable.Range(10, 5).Select(i =>
            Client.MutateRowAsync(TN, $"ca-rw-{i:D2}",
                Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)))
        ).Cast<Task>().ToList();
        writeTasks.Add(readTask);
        await Task.WhenAll(writeTasks);
        var finalRows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ca-rw-", "ca-rw~")));
        finalRows.Count.Should().BeGreaterThanOrEqualTo(10);
    }

    #endregion

    #region Concurrent RMW

    [Fact]
    public async Task Parallel_RMW_increments_are_atomic()
    {
        var key = "ca-rmw-inc";
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF, "counter",
                ByteString.CopyFrom(BitConverter.GetBytes(0L).Reverse().ToArray()),
                new BigtableVersion(1000)));
        var tasks = Enumerable.Range(0, 20).Select(_ =>
            Client.ReadModifyWriteRowAsync(TN, key,
                ReadModifyWriteRules.Increment(CF, "counter", 1))
        ).ToList();
        await Task.WhenAll(tasks);
        var rows = await ReadAll(RowSet.FromRowKeys(key), RowFilters.CellsPerColumnLimit(1));
        var val = BitConverter.ToInt64(rows[0].Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray());
        val.Should().Be(20);
    }

    [Fact]
    public async Task Parallel_RMW_appends_all_applied()
    {
        var key = "ca-rmw-app";
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF, "data", "", new BigtableVersion(1000)));
        var tasks = Enumerable.Range(0, 10).Select(i =>
            Client.ReadModifyWriteRowAsync(TN, key,
                ReadModifyWriteRules.Append(CF, "data", "x"))
        ).ToList();
        await Task.WhenAll(tasks);
        var rows = await ReadAll(RowSet.FromRowKeys(key), RowFilters.CellsPerColumnLimit(1));
        var val = rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8();
        val.Should().HaveLength(10);
    }

    #endregion

    #region Concurrent CAM

    [Fact]
    public async Task Concurrent_CAM_only_one_succeeds_toggle()
    {
        var key = "ca-cam-tog";
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF, "status", "available", new BigtableVersion(1000)));
        // 10 concurrent attempts to claim the resource
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            Client.CheckAndMutateRowAsync(TN, key,
                RowFilters.Chain(RowFilters.CellsPerColumnLimit(1), RowFilters.ValueExact("available")),
                Mutations.SetCell(CF, "status", $"claimed-{i}", new BigtableVersion(2000)))
                
        ));
        // Exactly one should succeed (the first to execute)
        results.Count(r => r.PredicateMatched).Should().Be(1);
    }

    #endregion

    #region Concurrent deletes

    [Fact]
    public async Task Parallel_deletes_of_different_rows()
    {
        for (int i = 0; i < 10; i++)
            await Client.MutateRowAsync(TN, $"ca-del-{i:D2}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)));
        var tasks = Enumerable.Range(0, 10).Select(i =>
            Client.MutateRowAsync(TN, $"ca-del-{i:D2}", Mutations.DeleteFromRow())
        ).ToList();
        await Task.WhenAll(tasks);
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ca-del-", "ca-del~")));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Parallel_delete_and_create_different_rows()
    {
        for (int i = 0; i < 5; i++)
            await Client.MutateRowAsync(TN, $"ca-dc-old-{i}",
                Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));
        var deleteTasks = Enumerable.Range(0, 5).Select(i =>
            Client.MutateRowAsync(TN, $"ca-dc-old-{i}", Mutations.DeleteFromRow())
        );
        var createTasks = Enumerable.Range(0, 5).Select(i =>
            Client.MutateRowAsync(TN, $"ca-dc-new-{i}",
                Mutations.SetCell(CF, "c", "new", new BigtableVersion(1000)))
        );
        await Task.WhenAll(deleteTasks.Concat(createTasks));
        (await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ca-dc-old-", "ca-dc-old~")))).Should().BeEmpty();
        (await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ca-dc-new-", "ca-dc-new~")))).Should().HaveCount(5);
    }

    #endregion

    #region Concurrent table operations

    [Fact]
    public async Task Parallel_reads_same_data()
    {
        for (int i = 0; i < 10; i++)
            await Client.MutateRowAsync(TN, $"ca-pread-{i:D2}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)));
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            Task.Run(async () =>
            {
                var rows = new List<Row>();
                await foreach (var row in Client.ReadRows(TN, RowSet.FromRowRanges(RowRange.ClosedOpen("ca-pread-", "ca-pread~"))))
                    rows.Add(row);
                return rows;
            })
        ));
        foreach (var rows in results)
            rows.Should().HaveCount(10);
    }

    #endregion
}

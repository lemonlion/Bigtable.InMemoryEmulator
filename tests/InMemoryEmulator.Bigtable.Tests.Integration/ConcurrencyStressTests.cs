using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Concurrency stress tests — parallel reads, writes, CheckAndMutate, and RMW.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ConcurrencyStressTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "concurrency-stress";
    private const string CF = "cf";

    public ConcurrencyStressTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<Row>> ReadAll(RowSet? rows = null)
    {
        var list = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: rows))
            list.Add(row);
        return list;
    }

    private static long ReadInt64(ByteString value)
    {
        var bytes = value.ToByteArray();
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToInt64(bytes, 0);
    }

    #region Parallel writes

    [Fact]
    public async Task Parallel_20_MutateRow_different_rows()
    {
        var tasks = Enumerable.Range(0, 20).Select(i =>
            Client.MutateRowAsync(TN, $"conc-pw-{i:D3}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))
        ).ToArray();
        await Task.WhenAll(tasks);
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("conc-pw-", "conc-pw-~")));
        rows.Should().HaveCount(20);
    }

    [Fact]
    public async Task Parallel_20_MutateRow_same_row_different_columns()
    {
        var tasks = Enumerable.Range(0, 20).Select(i =>
            Client.MutateRowAsync(TN, "conc-psrc",
                Mutations.SetCell(CF, $"col-{i:D3}", $"v{i}", new BigtableVersion(1000)))
        ).ToArray();
        await Task.WhenAll(tasks);
        var rows = await ReadAll(RowSet.FromRowKeys("conc-psrc"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns.Should().HaveCount(20);
    }

    [Fact]
    public async Task Parallel_MutateRow_same_row_same_column_different_versions()
    {
        var tasks = Enumerable.Range(1, 10).Select(i =>
            Client.MutateRowAsync(TN, "conc-pscv",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)))
        ).ToArray();
        await Task.WhenAll(tasks);
        var rows = await ReadAll(RowSet.FromRowKeys("conc-pscv"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(10);
    }

    [Fact]
    public async Task Parallel_MutateRows_batches()
    {
        var tasks = Enumerable.Range(0, 5).Select(batch =>
        {
            var entries = Enumerable.Range(0, 10).Select(i =>
                Mutations.CreateEntry($"conc-pmb-{batch}-{i:D2}",
                    Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)))
            ).ToArray();
            return Client.MutateRowsAsync(TN, entries);
        }).ToArray();
        await Task.WhenAll(tasks);
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("conc-pmb-", "conc-pmb-~")));
        rows.Should().HaveCount(50);
    }

    #endregion

    #region Parallel reads and writes

    [Fact]
    public async Task Concurrent_reads_do_not_block()
    {
        // Seed data
        for (int i = 0; i < 10; i++)
            await Client.MutateRowAsync(TN, $"conc-rd-{i:D2}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)));

        var tasks = Enumerable.Range(0, 10).Select(_ =>
            ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("conc-rd-", "conc-rd-~")))
        ).ToArray();
        var results = await Task.WhenAll(tasks);
        foreach (var rows in results)
            rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task Concurrent_reads_and_writes_no_deadlock()
    {
        // Seed initial data
        for (int i = 0; i < 5; i++)
            await Client.MutateRowAsync(TN, $"conc-rw-{i:D2}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)));

        var writeTasks = Enumerable.Range(5, 5).Select(i =>
            Client.MutateRowAsync(TN, $"conc-rw-{i:D2}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))
        );
        var readTasks = Enumerable.Range(0, 5).Select(_ =>
            ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("conc-rw-", "conc-rw-~")))
        );

        var allTasks = writeTasks.Cast<Task>().Concat(readTasks).ToArray();
        await Task.WhenAll(allTasks);
    }

    #endregion

    #region Parallel CheckAndMutate

    [Fact]
    public async Task Parallel_CheckAndMutate_on_different_rows()
    {
        for (int i = 0; i < 10; i++)
            await Client.MutateRowAsync(TN, $"conc-cam-{i:D2}",
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var tasks = Enumerable.Range(0, 10).Select(i =>
            Client.CheckAndMutateRowAsync(TN, $"conc-cam-{i:D2}",
                RowFilters.PassAllFilter(),
                new[] { Mutations.SetCell(CF, "marked", "yes", new BigtableVersion(2000)) },
                null)
        ).ToArray();
        var results = await Task.WhenAll(tasks);
        results.Should().AllSatisfy(r => r.PredicateMatched.Should().BeTrue());
    }

    [Fact]
    public async Task Parallel_CheckAndMutate_same_row()
    {
        await Client.MutateRowAsync(TN, "conc-cam-same",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var tasks = Enumerable.Range(0, 5).Select(i =>
            Client.CheckAndMutateRowAsync(TN, "conc-cam-same",
                RowFilters.PassAllFilter(),
                new[] { Mutations.SetCell(CF, $"mark-{i}", "yes", new BigtableVersion(2000)) },
                null)
        ).ToArray();
        var results = await Task.WhenAll(tasks);
        results.Should().AllSatisfy(r => r.PredicateMatched.Should().BeTrue());
    }

    #endregion

    #region Parallel ReadModifyWrite

    [Fact]
    public async Task Parallel_increments_atomic()
    {
        // Ref: ReadModifyWriteRow is atomic per row
        var tasks = Enumerable.Range(0, 20).Select(_ =>
            Client.ReadModifyWriteRowAsync(TN, "conc-rmw-atom",
                ReadModifyWriteRules.Increment(CF, "counter", 1))
        ).ToArray();
        await Task.WhenAll(tasks);

        var rows = await ReadAll(RowSet.FromRowKeys("conc-rmw-atom"));
        var val = ReadInt64(rows[0].Families[0].Columns[0].Cells[0].Value);
        val.Should().Be(20);
    }

    [Fact]
    public async Task Parallel_appends_all_data_present()
    {
        var tasks = Enumerable.Range(0, 10).Select(i =>
            Client.ReadModifyWriteRowAsync(TN, "conc-rmw-app",
                ReadModifyWriteRules.Append(CF, "log", $"[{i}]"))
        ).ToArray();
        await Task.WhenAll(tasks);

        var rows = await ReadAll(RowSet.FromRowKeys("conc-rmw-app"));
        var val = rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8();
        val.Length.Should().Be(30); // 10 * "[N]" = 10 * 3 chars
    }

    [Fact]
    public async Task Parallel_increments_different_rows()
    {
        var tasks = Enumerable.Range(0, 10).Select(i =>
            Client.ReadModifyWriteRowAsync(TN, $"conc-rmw-dr-{i:D2}",
                ReadModifyWriteRules.Increment(CF, "counter", 1))
        ).ToArray();
        await Task.WhenAll(tasks);

        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("conc-rmw-dr-", "conc-rmw-dr-~")));
        rows.Should().HaveCount(10);
        foreach (var row in rows)
            ReadInt64(row.Families[0].Columns[0].Cells[0].Value).Should().Be(1);
    }

    #endregion

    #region Mixed parallel operations

    [Fact]
    public async Task Mixed_writes_reads_cam_rmw()
    {
        // Seed initial data
        await Client.MutateRowAsync(TN, "conc-mix",
            Mutations.SetCell(CF, "c", "initial", new BigtableVersion(1000)));

        var tasks = new List<Task>
        {
            Client.MutateRowAsync(TN, "conc-mix",
                Mutations.SetCell(CF, "w1", "val", new BigtableVersion(2000))),
            Client.ReadModifyWriteRowAsync(TN, "conc-mix",
                ReadModifyWriteRules.Increment(CF, "counter", 1)),
            Client.CheckAndMutateRowAsync(TN, "conc-mix",
                RowFilters.PassAllFilter(),
                new[] { Mutations.SetCell(CF, "cam", "ok", new BigtableVersion(3000)) },
                null),
            ReadAll(RowSet.FromRowKeys("conc-mix")),
        };
        await Task.WhenAll(tasks);

        // Just verify no deadlock/crash
        var rows = await ReadAll(RowSet.FromRowKeys("conc-mix"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Parallel_delete_and_write_different_rows()
    {
        // Seed
        for (int i = 0; i < 5; i++)
            await Client.MutateRowAsync(TN, $"conc-dw-{i:D2}",
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var deleteTasks = Enumerable.Range(0, 3).Select(i =>
            Client.MutateRowAsync(TN, $"conc-dw-{i:D2}", Mutations.DeleteFromRow()));
        var writeTasks = Enumerable.Range(5, 5).Select(i =>
            Client.MutateRowAsync(TN, $"conc-dw-{i:D2}",
                Mutations.SetCell(CF, "c", "new", new BigtableVersion(1000))));

        await Task.WhenAll(deleteTasks.Concat(writeTasks));

        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("conc-dw-", "conc-dw-~")));
        // 3 deleted, 2 original remain, 5 new = 7
        rows.Should().HaveCount(7);
    }

    #endregion
}

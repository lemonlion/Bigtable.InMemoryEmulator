using System.Diagnostics;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;

namespace Bigtable.InMemoryEmulator.Tests.Performance;

/// <summary>
/// Performance benchmarks for the in-memory Bigtable emulator.
/// Measures latency and throughput for core operations (writes, reads, filters, SQL queries).
///
/// These are xUnit tests that emit timing results as test output.
/// They serve as regression detection — not BenchmarkDotNet microbenchmarks.
///
/// Ref: Verification Checklist item 12 — "Benchmark comparison: in-memory vs Go emulator latency/throughput"
/// </summary>
public sealed class PerformanceBenchmarkTests : IAsyncLifetime
{
    private InMemoryBigtableResult _result = null!;
    private BigtableClient _client = null!;
    private TableName _tableName = null!;
    private const string Table = "perf_test";
    private const string Family = "cf";

    public async ValueTask InitializeAsync()
    {
        _result = InMemoryBigtable.Builder()
            .AddTable(Table, [Family])
            .Build();
        _client = _result.Client;
        _tableName = _result.GetTableName(Table);

        // Warmup — create some rows
        for (int i = 0; i < 10; i++)
        {
            await _client.MutateRowAsync(_tableName, new BigtableByteString($"warmup-{i:D3}"),
                Mutations.SetCell(Family, "col", "val", new BigtableVersion(1000)));
        }
    }

    public ValueTask DisposeAsync()
    {
        _result.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task MutateRow_throughput_1000_writes()
    {
        const int count = 1000;
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < count; i++)
        {
            await _client.MutateRowAsync(_tableName, new BigtableByteString($"write-{i:D5}"),
                Mutations.SetCell(Family, "col", $"value-{i}", new BigtableVersion(1000)));
        }

        sw.Stop();
        var avgMs = sw.Elapsed.TotalMilliseconds / count;

        // Sanity threshold — in-memory should be well under 5ms per write
        avgMs.Should().BeLessThan(5.0,
            $"Average MutateRow latency was {avgMs:F3}ms ({count} ops in {sw.ElapsedMilliseconds}ms)");
    }

    [Fact]
    public async Task ReadRows_throughput_read_1000_rows()
    {
        const int rowCount = 1000;
        // Seed rows
        for (int i = 0; i < rowCount; i++)
        {
            await _client.MutateRowAsync(_tableName, new BigtableByteString($"read-{i:D5}"),
                Mutations.SetCell(Family, "col", $"value-{i}", new BigtableVersion(1000)));
        }

        var sw = Stopwatch.StartNew();
        int count = 0;
        var rows = _client.ReadRows(_tableName);
        await foreach (var _ in rows) count++;
        sw.Stop();

        count.Should().BeGreaterThanOrEqualTo(rowCount);
        var avgMs = sw.Elapsed.TotalMilliseconds / count;
        avgMs.Should().BeLessThan(1.0,
            $"Average ReadRows latency was {avgMs:F3}ms ({count} rows in {sw.ElapsedMilliseconds}ms)");
    }

    [Fact]
    public async Task ReadRows_with_filter_throughput()
    {
        const int rowCount = 500;
        // Seed rows with multiple columns
        for (int i = 0; i < rowCount; i++)
        {
            await _client.MutateRowAsync(_tableName, new BigtableByteString($"filter-{i:D5}"),
                Mutations.SetCell(Family, "col-a", $"a-{i}", new BigtableVersion(1000)),
                Mutations.SetCell(Family, "col-b", $"b-{i}", new BigtableVersion(1000)));
        }

        var filter = RowFilters.ColumnQualifierRegex("col-a");

        var sw = Stopwatch.StartNew();
        int count = 0;
        var rows = _client.ReadRows(_tableName, filter: filter);
        await foreach (var _ in rows) count++;
        sw.Stop();

        count.Should().BeGreaterThanOrEqualTo(rowCount);
        var avgMs = sw.Elapsed.TotalMilliseconds / count;
        avgMs.Should().BeLessThan(2.0,
            $"Average filtered ReadRows latency was {avgMs:F3}ms ({count} rows in {sw.ElapsedMilliseconds}ms)");
    }

    [Fact]
    public async Task CheckAndMutateRow_throughput()
    {
        const int count = 200;
        // Seed a row
        await _client.MutateRowAsync(_tableName, new BigtableByteString("cas-row"),
            Mutations.SetCell(Family, "counter", "0", new BigtableVersion(1000)));

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            await _client.CheckAndMutateRowAsync(_tableName, new BigtableByteString("cas-row"),
                RowFilters.PassAllFilter(),
                new[] { Mutations.SetCell(Family, "counter", i.ToString(), new BigtableVersion((i + 1) * 1000)) },
                null);
        }
        sw.Stop();

        var avgMs = sw.Elapsed.TotalMilliseconds / count;
        avgMs.Should().BeLessThan(5.0,
            $"Average CheckAndMutateRow latency was {avgMs:F3}ms ({count} ops in {sw.ElapsedMilliseconds}ms)");
    }

    [Fact]
    public async Task Concurrent_writes_throughput()
    {
        const int taskCount = 10;
        const int writesPerTask = 100;

        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, taskCount).Select(t => Task.Run(async () =>
        {
            for (int i = 0; i < writesPerTask; i++)
            {
                await _client.MutateRowAsync(_tableName, new BigtableByteString($"conc-{t:D2}-{i:D3}"),
                    Mutations.SetCell(Family, "col", $"val-{i}", new BigtableVersion(1000)));
            }
        }));
        await Task.WhenAll(tasks);
        sw.Stop();

        int totalOps = taskCount * writesPerTask;
        var avgMs = sw.Elapsed.TotalMilliseconds / totalOps;
        avgMs.Should().BeLessThan(5.0,
            $"Average concurrent write latency was {avgMs:F3}ms ({totalOps} ops in {sw.ElapsedMilliseconds}ms)");
    }

    [Fact]
    public async Task ExecuteQuery_throughput()
    {
        const int rowCount = 200;
        // Seed rows
        for (int i = 0; i < rowCount; i++)
        {
            await _client.MutateRowAsync(_tableName, new BigtableByteString($"sql-{i:D5}"),
                Mutations.SetCell(Family, "col", $"val-{i}", new BigtableVersion(1000)));
        }

        var serviceApiClient = new BigtableServiceApiClientBuilder
        {
            CallInvoker = _result.Channel.CreateCallInvoker()
        }.Build();

        const int queryCount = 50;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < queryCount; i++)
        {
            var stream = serviceApiClient.ExecuteQuery(new ExecuteQueryRequest
            {
                InstanceName = $"projects/{_result.ProjectId}/instances/{_result.InstanceId}",
                Query = $"SELECT _key FROM {Table} LIMIT 10",
                ProtoFormat = new ProtoFormat(),
            });
            var enumerator = stream.GetResponseStream().GetAsyncEnumerator(default);
            while (await enumerator.MoveNextAsync()) { }
        }
        sw.Stop();

        var avgMs = sw.Elapsed.TotalMilliseconds / queryCount;
        avgMs.Should().BeLessThan(50.0,
            $"Average ExecuteQuery latency was {avgMs:F3}ms ({queryCount} queries in {sw.ElapsedMilliseconds}ms)");
    }
}

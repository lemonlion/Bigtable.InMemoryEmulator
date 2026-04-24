using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// End-to-end domain scenarios: IoT data ingestion, leaderboard, event log, message queue.
///
/// Ref: https://cloud.google.com/bigtable/docs/schema-design-time-series
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class EndToEndDomainScenarioTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "data";
    private const string META = "meta";

    public EndToEndDomainScenarioTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync("e2e-domain", new[] { CF, META });
    }
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("e2e-domain");

    #region IoT Telemetry

    [Fact]
    public async Task IoT_ingest_sensor_readings()
    {
        // Schema: rowkey = "sensor#<id>#<reverse_ts>"
        for (int i = 0; i < 10; i++)
        {
            var reverseTs = (long.MaxValue - (1000000L + i * 1000)).ToString("D19");
            await Client.MutateRowAsync(TN, $"sensor#device001#{reverseTs}",
                Mutations.SetCell(CF, "temp", $"{20.0 + i * 0.5}", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "humidity", $"{50 + i}", new BigtableVersion(1000)));
        }

        // Read all for device001
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("sensor#device001#", "sensor#device002#"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task IoT_read_latest_5_readings()
    {
        for (int i = 0; i < 10; i++)
        {
            var reverseTs = (long.MaxValue - (2000000L + i * 1000)).ToString("D19");
            await Client.MutateRowAsync(TN, $"sensor#dev002#{reverseTs}",
                Mutations.SetCell(CF, "temp", $"{25.0 + i}", new BigtableVersion(1000)));
        }

        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("sensor#dev002#", "sensor#dev003#"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet, rowsLimit: 5))
            rows.Add(row);
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task IoT_multi_device_scan()
    {
        string[] devices = { "devA", "devB", "devC" };
        foreach (var dev in devices)
            for (int i = 0; i < 3; i++)
                await Client.MutateRowAsync(TN, $"iot#{dev}#{i:D4}",
                    Mutations.SetCell(CF, "val", "1.0", new BigtableVersion(1000)));

        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("iot#", "iot$")); // $ > # in ASCII
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);
        rows.Should().HaveCount(9);
    }

    #endregion

    #region Leaderboard

    [Fact]
    public async Task Leaderboard_write_and_read_scores()
    {
        var players = new[] { ("alice", 1500), ("bob", 2200), ("charlie", 1800), ("diana", 3000), ("eve", 900) };
        foreach (var (name, score) in players)
        {
            // Reverse score for descending sort
            var reverseScore = (999999 - score).ToString("D6");
            await Client.MutateRowAsync(TN, $"leaderboard#{reverseScore}#{name}",
                Mutations.SetCell(CF, "name", name, new BigtableVersion(1000)),
                Mutations.SetCell(CF, "score", score.ToString(), new BigtableVersion(1000)));
        }

        // Read top 3
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("leaderboard#", "leaderboard$"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet, rowsLimit: 3))
            rows.Add(row);
        rows.Should().HaveCount(3);
        // First should be diana (score 3000 → reverse 997000)
        var firstName = rows[0].Families
            .SelectMany(f => f.Columns)
            .First(c => c.Qualifier.ToStringUtf8() == "name")
            .Cells[0].Value.ToStringUtf8();
        firstName.Should().Be("diana");
    }

    [Fact]
    public async Task Leaderboard_update_score_with_CaM()
    {
        await Client.MutateRowAsync(TN, "lb-player#alice",
            Mutations.SetCell(CF, "score", "1500", new BigtableVersion(1000)));

        // CaM: if score exists, update it
        var result = await Client.CheckAndMutateRowAsync(TN, "lb-player#alice",
            predicateFilter: RowFilters.Chain(
                RowFilters.ColumnQualifierExact("score"),
                RowFilters.CellsPerColumnLimit(1)),
            trueMutations: new[] { Mutations.SetCell(CF, "score", "1600", new BigtableVersion(2000)) },
            falseMutations: null);
        result.PredicateMatched.Should().BeTrue();

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("lb-player#alice"),
            RowFilters.Chain(RowFilters.ColumnQualifierExact("score"), RowFilters.CellsPerColumnLimit(1))))
            rows.Add(row);
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("1600");
    }

    #endregion

    #region Event Log

    [Fact]
    public async Task EventLog_append_and_scan()
    {
        for (int i = 0; i < 20; i++)
        {
            var ts = (1000000 + i * 100).ToString("D10");
            await Client.MutateRowAsync(TN, $"event#{ts}",
                Mutations.SetCell(CF, "type", i % 2 == 0 ? "click" : "view", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "page", $"/page/{i % 5}", new BigtableVersion(1000)));
        }

        // Scan all events
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("event#", "event$"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);
        rows.Should().HaveCount(20);
    }

    [Fact]
    public async Task EventLog_filter_by_type()
    {
        for (int i = 0; i < 10; i++)
        {
            var ts = (2000000 + i * 100).ToString("D10");
            await Client.MutateRowAsync(TN, $"evtype#{ts}",
                Mutations.SetCell(CF, "type", i % 3 == 0 ? "error" : "info", new BigtableVersion(1000)));
        }

        // Filter for "error" type
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("evtype#", "evtype$"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet,
            RowFilters.Chain(RowFilters.ColumnQualifierExact("type"), RowFilters.ValueExact("error"))))
            rows.Add(row);
        // Indices 0, 3, 6, 9 → 4 errors
        rows.Should().HaveCount(4);
    }

    #endregion

    #region Message Queue

    [Fact]
    public async Task MessageQueue_mark_processed_with_CaM()
    {
        // Write messages
        for (int i = 0; i < 5; i++)
            await Client.MutateRowAsync(TN, $"msg#{i:D4}",
                Mutations.SetCell(CF, "body", $"message-{i}", new BigtableVersion(1000)),
                Mutations.SetCell(META, "status", "pending", new BigtableVersion(1000)));

        // Process first message: mark as processed
        var result = await Client.CheckAndMutateRowAsync(TN, "msg#0000",
            predicateFilter: RowFilters.Chain(
                RowFilters.FamilyNameExact(META),
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.ValueExact("pending")),
            trueMutations: new[] { Mutations.SetCell(META, "status", "processed", new BigtableVersion(2000)) },
            falseMutations: null);
        result.PredicateMatched.Should().BeTrue();

        // Try processing again — should not match (latest version is "processed")
        var secondResult = await Client.CheckAndMutateRowAsync(TN, "msg#0000",
            predicateFilter: RowFilters.Chain(
                RowFilters.FamilyNameExact(META),
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueExact("pending")),
            trueMutations: new[] { Mutations.SetCell(META, "status", "processed", new BigtableVersion(3000)) },
            falseMutations: null);
        secondResult.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task MessageQueue_counter_tracks_processing()
    {
        await Client.MutateRowAsync(TN, "msg-stats",
            Mutations.SetCell(META, "total", "0", new BigtableVersion(1000)));

        // Increment counter for each processed message
        for (int i = 0; i < 10; i++)
            await Client.ReadModifyWriteRowAsync(TN, "msg-stats",
                ReadModifyWriteRules.Increment(META, "processed", 1));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("msg-stats")))
            rows.Add(row);

        var processedCell = rows[0].Families
            .SelectMany(f => f.Columns)
            .First(c => c.Qualifier.ToStringUtf8() == "processed")
            .Cells[0];
        var val = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(processedCell.Value.Span);
        val.Should().Be(10);
    }

    #endregion

    #region User Session Tracking

    [Fact]
    public async Task UserSession_write_and_read_activity()
    {
        var userId = "user#u100";
        // Login
        await Client.MutateRowAsync(TN, userId,
            Mutations.SetCell(CF, "last_login", "2024-01-15T10:00:00Z", new BigtableVersion(1000)),
            Mutations.SetCell(META, "session_count", "1", new BigtableVersion(1000)));

        // Increment session count
        await Client.ReadModifyWriteRowAsync(TN, userId,
            ReadModifyWriteRules.Increment(META, "session_count_int", 1));

        // Page views
        for (int i = 0; i < 3; i++)
            await Client.MutateRowAsync(TN, userId,
                Mutations.SetCell(CF, $"page_{i}", $"/section/{i}", new BigtableVersion((2000 + i) * 1000)));

        // Read full profile
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys(userId)))
            rows.Add(row);
        rows.Should().ContainSingle();
        var cols = rows[0].Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("last_login");
        cols.Should().Contain("page_0");
    }

    [Fact]
    public async Task UserSession_delete_session_data()
    {
        await Client.MutateRowAsync(TN, "user#u200",
            Mutations.SetCell(CF, "name", "Bob", new BigtableVersion(1000)),
            Mutations.SetCell(META, "token", "abc123", new BigtableVersion(1000)));

        // Delete session metadata but keep user data
        await Client.MutateRowAsync(TN, "user#u200",
            Mutations.DeleteFromFamily(META));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("user#u200")))
            rows.Add(row);
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle(); // Only CF remains
        rows[0].Families[0].Name.Should().Be(CF);
    }

    #endregion

    #region Batch Processing

    [Fact]
    public async Task BatchProcess_insert_and_scan_1000_rows()
    {
        // Insert in batches of 100
        for (int batch = 0; batch < 10; batch++)
        {
            var entries = Enumerable.Range(batch * 100, 100).Select(i =>
                Mutations.CreateEntry($"batch#{i:D6}",
                    Mutations.SetCell(CF, "val", $"data-{i}", new BigtableVersion(1000)))).ToArray();
            await Client.MutateRowsAsync(TN, entries);
        }

        // Count all batch rows
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("batch#", "batch$"));
        int count = 0;
        await foreach (var _ in Client.ReadRows(TN, rowSet))
            count++;
        count.Should().Be(1000);
    }

    [Fact]
    public async Task BatchProcess_partial_scan_with_limit()
    {
        // Seed 50 rows
        var entries = Enumerable.Range(0, 50).Select(i =>
            Mutations.CreateEntry($"bpart#{i:D4}",
                Mutations.SetCell(CF, "val", $"d-{i}", new BigtableVersion(1000)))).ToArray();
        await Client.MutateRowsAsync(TN, entries);

        // Read only first 10
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("bpart#", "bpart$"));
        int count = 0;
        await foreach (var _ in Client.ReadRows(TN, rowSet, rowsLimit: 10))
            count++;
        count.Should().Be(10);
    }

    #endregion
}

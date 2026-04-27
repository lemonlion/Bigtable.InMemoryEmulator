using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// End-to-end scenario tests simulating realistic application workflows.
///
/// Ref: https://cloud.google.com/bigtable/docs/overview
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class EndToEndScenarioTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "e2e-scen";
    private const string CF = "cf";
    private const string CF2 = "meta";
    private const string CF3 = "stats";

    public EndToEndScenarioTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, CF2, CF3 });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null, long? limit = null)
    {
        var list = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: rows, filter: filter, rowsLimit: limit))
            list.Add(row);
        return list;
    }

    #region User session tracking

    [Fact]
    public async Task User_session_create_update_read()
    {
        var userId = "e2e-u1";
        // Create session
        await Client.MutateRowAsync(TN, userId,
            Mutations.SetCell(CF, "name", "Alice", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "created", "2024-01-01", new BigtableVersion(1000)),
            Mutations.SetCell(CF3, "logins", ByteString.CopyFrom(BitConverter.GetBytes(1L).Reverse().ToArray()), new BigtableVersion(1000)));
        // Increment login count
        var result = await Client.ReadModifyWriteRowAsync(TN, userId,
            ReadModifyWriteRules.Increment(CF3, "logins", 1));
        // Update last login
        await Client.MutateRowAsync(TN, userId,
            Mutations.SetCell(CF2, "last_login", "2024-01-15", new BigtableVersion(2000)));
        // Read full profile
        var rows = await ReadAll(RowSet.FromRowKeys(userId), RowFilters.CellsPerColumnLimit(1));
        rows.Should().ContainSingle();
        var families = rows[0].Families.ToDictionary(f => f.Name);
        families[CF].Columns.First(c => c.Qualifier.ToStringUtf8() == "name")
            .Cells[0].Value.ToStringUtf8().Should().Be("Alice");
        families[CF2].Columns.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task User_profile_with_version_history()
    {
        var userId = "e2e-u2";
        await Client.MutateRowAsync(TN, userId,
            Mutations.SetCell(CF, "email", "old@test.com", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, userId,
            Mutations.SetCell(CF, "email", "new@test.com", new BigtableVersion(2000)));
        // Read all versions
        var rows = await ReadAll(RowSet.FromRowKeys(userId));
        var cells = rows[0].Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2);
        cells[0].Value.ToStringUtf8().Should().Be("new@test.com"); // latest first
        cells[1].Value.ToStringUtf8().Should().Be("old@test.com");
    }

    #endregion

    #region Time-series data

    [Fact]
    public async Task Sensor_readings_time_series()
    {
        // Insert sensor readings at different timestamps
        for (int hour = 0; hour < 24; hour++)
        {
            await Client.MutateRowAsync(TN, "e2e-sensor-001",
                Mutations.SetCell(CF, "temp", $"{20 + hour % 5}", new BigtableVersion((1000 + hour) * 1000)));
        }
        // Read latest reading
        var latest = await ReadAll(RowSet.FromRowKeys("e2e-sensor-001"), RowFilters.CellsPerColumnLimit(1));
        latest.Should().ContainSingle();
        // Read all readings
        var all = await ReadAll(RowSet.FromRowKeys("e2e-sensor-001"));
        all[0].Families[0].Columns[0].Cells.Should().HaveCount(24);
    }

    [Fact]
    public async Task Multi_sensor_latest_readings()
    {
        for (int sensor = 0; sensor < 5; sensor++)
        {
            for (int reading = 0; reading < 3; reading++)
            {
                await Client.MutateRowAsync(TN, $"e2e-sensor-{sensor:D2}",
                    Mutations.SetCell(CF, "value", $"s{sensor}r{reading}", new BigtableVersion((reading + 1) * 1000)));
            }
        }
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("e2e-sensor-", "e2e-sensor~")),
            RowFilters.CellsPerColumnLimit(1));
        rows.Should().HaveCount(5);
        foreach (var row in rows)
            row.Families[0].Columns[0].Cells.Should().ContainSingle();
    }

    #endregion

    #region Event log pattern

    [Fact]
    public async Task Append_events_to_log()
    {
        var baseKey = "e2e-log-001";
        // Write events at different versions
        await Client.MutateRowAsync(TN, baseKey,
            Mutations.SetCell(CF, "event", "created", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, baseKey,
            Mutations.SetCell(CF, "event", "updated", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, baseKey,
            Mutations.SetCell(CF, "event", "completed", new BigtableVersion(3000)));
        // Read all events in order
        var rows = await ReadAll(RowSet.FromRowKeys(baseKey));
        var events = rows[0].Families[0].Columns[0].Cells.Select(c => c.Value.ToStringUtf8()).ToList();
        events.Should().BeEquivalentTo(new[] { "completed", "updated", "created" }); // newest first
    }

    [Fact]
    public async Task Event_log_with_multiple_columns()
    {
        var baseKey = "e2e-log-002";
        await Client.MutateRowAsync(TN, baseKey,
            Mutations.SetCell(CF, "event_type", "login", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "event_data", "ip=1.2.3.4", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "user_agent", "Chrome/120", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys(baseKey));
        rows[0].Families.Should().HaveCount(2);
    }

    #endregion

    #region Counter pattern

    [Fact]
    public async Task Atomic_counter_pattern()
    {
        var key = "e2e-counter-001";
        // Initialize counter
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF3, "views", ByteString.CopyFrom(BitConverter.GetBytes(0L).Reverse().ToArray()), new BigtableVersion(1000)));
        // Increment 10 times
        for (int i = 0; i < 10; i++)
            await Client.ReadModifyWriteRowAsync(TN, key,
                ReadModifyWriteRules.Increment(CF3, "views", 1));
        // Check final count
        var rows = await ReadAll(RowSet.FromRowKeys(key), RowFilters.CellsPerColumnLimit(1));
        var val = BitConverter.ToInt64(rows[0].Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray());
        val.Should().Be(10);
    }

    [Fact]
    public async Task Multi_counter_pattern()
    {
        var key = "e2e-counter-002";
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF3, "views", ByteString.CopyFrom(BitConverter.GetBytes(0L).Reverse().ToArray()), new BigtableVersion(1000)),
            Mutations.SetCell(CF3, "clicks", ByteString.CopyFrom(BitConverter.GetBytes(0L).Reverse().ToArray()), new BigtableVersion(1000)));
        for (int i = 0; i < 5; i++)
            await Client.ReadModifyWriteRowAsync(TN, key,
                ReadModifyWriteRules.Increment(CF3, "views", 2),
                ReadModifyWriteRules.Increment(CF3, "clicks", 1));
        var rows = await ReadAll(RowSet.FromRowKeys(key), RowFilters.CellsPerColumnLimit(1));
        var viewsCol = rows[0].Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "views");
        var clicksCol = rows[0].Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "clicks");
        var views = BitConverter.ToInt64(viewsCol.Cells[0].Value.ToByteArray().Reverse().ToArray());
        var clicks = BitConverter.ToInt64(clicksCol.Cells[0].Value.ToByteArray().Reverse().ToArray());
        views.Should().Be(10);
        clicks.Should().Be(5);
    }

    #endregion

    #region Conditional update pattern

    [Fact]
    public async Task Conditional_state_machine()
    {
        var key = "e2e-state-001";
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF, "status", "pending", new BigtableVersion(1000)));
        // Transition pending -> processing
        var result = await Client.CheckAndMutateRowAsync(TN, key,
            RowFilters.Chain(RowFilters.CellsPerColumnLimit(1), RowFilters.ValueExact("pending")),
            Mutations.SetCell(CF, "status", "processing", new BigtableVersion(2000)));
        result.PredicateMatched.Should().BeTrue();
        // Attempt invalid transition (pending -> processing should fail since it's now "processing")
        var result2 = await Client.CheckAndMutateRowAsync(TN, key,
            RowFilters.Chain(RowFilters.CellsPerColumnLimit(1), RowFilters.ValueExact("pending")),
            Mutations.SetCell(CF, "status", "processing", new BigtableVersion(3000)));
        result2.PredicateMatched.Should().BeFalse();
        // Complete
        var result3 = await Client.CheckAndMutateRowAsync(TN, key,
            RowFilters.Chain(RowFilters.CellsPerColumnLimit(1), RowFilters.ValueExact("processing")),
            Mutations.SetCell(CF, "status", "complete", new BigtableVersion(3000)));
        result3.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Optimistic_lock_pattern()
    {
        var key = "e2e-lock-001";
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF, "data", "initial", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "version", "1", new BigtableVersion(1000)));
        // Writer 1: check version=1, update
        var w1 = await Client.CheckAndMutateRowAsync(TN, key,
            RowFilters.Chain(
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ColumnQualifierExact("version"),
                RowFilters.ValueExact("1")),
            Mutations.SetCell(CF, "data", "updated-by-w1", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "version", "2", new BigtableVersion(2000)));
        w1.PredicateMatched.Should().BeTrue();
        // Writer 2: check version=1 (stale!) — should fail
        var w2 = await Client.CheckAndMutateRowAsync(TN, key,
            RowFilters.Chain(
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ColumnQualifierExact("version"),
                RowFilters.ValueExact("1")),
            Mutations.SetCell(CF, "data", "updated-by-w2", new BigtableVersion(3000)),
            Mutations.SetCell(CF, "version", "2", new BigtableVersion(3000)));
        w2.PredicateMatched.Should().BeFalse();
    }

    #endregion

    #region Batch scan and aggregate

    [Fact]
    public async Task Scan_and_count_by_prefix()
    {
        // Insert items with category prefixes
        var entries = new List<MutateRowsRequest.Types.Entry>();
        for (int i = 0; i < 10; i++)
            entries.Add(Mutations.CreateEntry($"e2e-cat-A-{i}", Mutations.SetCell(CF, "c", "a", new BigtableVersion(1000))));
        for (int i = 0; i < 15; i++)
            entries.Add(Mutations.CreateEntry($"e2e-cat-B-{i}", Mutations.SetCell(CF, "c", "b", new BigtableVersion(1000))));
        await Client.MutateRowsAsync(TN, entries.ToArray());

        var catA = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("e2e-cat-A-", "e2e-cat-A~")));
        var catB = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("e2e-cat-B-", "e2e-cat-B~")));
        catA.Should().HaveCount(10);
        catB.Should().HaveCount(15);
    }

    [Fact]
    public async Task Batch_insertion_then_filtered_scan()
    {
        var entries = Enumerable.Range(0, 20).Select(i =>
            Mutations.CreateEntry($"e2e-bf-{i:D3}",
                Mutations.SetCell(CF, "value", i % 2 == 0 ? "even" : "odd", new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        // Read only even values
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("e2e-bf-", "e2e-bf~")),
            RowFilters.ValueExact("even"));
        rows.Should().HaveCount(10);
    }

    #endregion

    #region Multi-column writes and reads

    [Fact]
    public async Task Wide_row_with_many_columns()
    {
        var mutations = Enumerable.Range(0, 50).Select(i =>
            Mutations.SetCell(CF, $"col-{i:D3}", $"val-{i}", new BigtableVersion(1000))
        ).ToArray();
        await Client.MutateRowAsync(TN, "e2e-wide", mutations);
        var rows = await ReadAll(RowSet.FromRowKeys("e2e-wide"));
        rows[0].Families[0].Columns.Should().HaveCount(50);
    }

    [Fact]
    public async Task Wide_row_filter_specific_column()
    {
        var mutations = Enumerable.Range(0, 20).Select(i =>
            Mutations.SetCell(CF, $"col-{i:D3}", $"val-{i}", new BigtableVersion(1000))
        ).ToArray();
        await Client.MutateRowAsync(TN, "e2e-wide2", mutations);
        var rows = await ReadAll(RowSet.FromRowKeys("e2e-wide2"),
            RowFilters.ColumnQualifierExact("col-010"));
        rows[0].Families[0].Columns.Should().ContainSingle()
            .Which.Cells[0].Value.ToStringUtf8().Should().Be("val-10");
    }

    [Fact]
    public async Task Wide_row_column_range()
    {
        var mutations = Enumerable.Range(0, 20).Select(i =>
            Mutations.SetCell(CF, $"col-{i:D3}", $"val-{i}", new BigtableVersion(1000))
        ).ToArray();
        await Client.MutateRowAsync(TN, "e2e-wide3", mutations);
        var rows = await ReadAll(RowSet.FromRowKeys("e2e-wide3"),
            RowFilters.ColumnRange(ColumnRange.ClosedOpen(CF, "col-005", "col-010")));
        rows[0].Families[0].Columns.Should().HaveCount(5);
    }

    #endregion

    #region Mixed operation workflow

    [Fact]
    public async Task Full_crud_lifecycle()
    {
        var key = "e2e-crud";
        // Create
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF, "name", "Test Item", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "created", "2024-01-01", new BigtableVersion(1000)));
        // Read
        var rows = await ReadAll(RowSet.FromRowKeys(key));
        rows.Should().ContainSingle();
        // Update
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF, "name", "Updated Item", new BigtableVersion(2000)));
        // Verify update
        var updated = await ReadAll(RowSet.FromRowKeys(key), RowFilters.CellsPerColumnLimit(1));
        updated[0].Families.First(f => f.Name == CF).Columns.First(c => c.Qualifier.ToStringUtf8() == "name")
            .Cells[0].Value.ToStringUtf8().Should().Be("Updated Item");
        // Delete
        await Client.MutateRowAsync(TN, key, Mutations.DeleteFromRow());
        (await ReadAll(RowSet.FromRowKeys(key))).Should().BeEmpty();
    }

    [Fact]
    public async Task Mixed_operations_preserve_consistency()
    {
        // Setup: 10 rows
        for (int i = 0; i < 10; i++)
            await Client.MutateRowAsync(TN, $"e2e-mix-{i:D2}",
                Mutations.SetCell(CF, "v", "initial", new BigtableVersion(1000)));
        // Delete odd rows
        for (int i = 1; i < 10; i += 2)
            await Client.MutateRowAsync(TN, $"e2e-mix-{i:D2}", Mutations.DeleteFromRow());
        // Update even rows
        for (int i = 0; i < 10; i += 2)
            await Client.MutateRowAsync(TN, $"e2e-mix-{i:D2}",
                Mutations.SetCell(CF, "v", "updated", new BigtableVersion(2000)));
        // Verify: 5 remaining rows, all "updated"
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("e2e-mix-", "e2e-mix~")),
            RowFilters.CellsPerColumnLimit(1));
        rows.Should().HaveCount(5);
        rows.All(r => r.Families[0].Columns[0].Cells[0].Value.ToStringUtf8() == "updated").Should().BeTrue();
    }

    #endregion
}

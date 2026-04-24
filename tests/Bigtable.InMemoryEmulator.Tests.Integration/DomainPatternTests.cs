using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for domain-specific data patterns: time series, IoT, user profiles.
///
/// Ref: https://cloud.google.com/bigtable/docs/schema-design-time-series
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class DomainPatternTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "domain";
    private const string CF = "cf";

    public DomainPatternTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { "metrics", "profile", "events" });
    }

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

    #region Time series pattern

    [Fact]
    public async Task TimeSeries_write_and_range_scan()
    {
        // Key pattern: sensor#YYYY-MM-DD#HH:MM
        for (int hour = 0; hour < 24; hour++)
            await Client.MutateRowAsync(TN, $"sensor01#2024-01-15#{hour:D2}:00",
                Mutations.SetCell("metrics", "temp", $"{20 + hour}", new BigtableVersion(1000)));

        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen(
            "sensor01#2024-01-15#08:00", "sensor01#2024-01-15#18:00"));
        var rows = await ReadAll(rows: rowSet);
        rows.Should().HaveCount(10); // 08 through 17
    }

    [Fact]
    public async Task TimeSeries_latest_reading()
    {
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "ts-sensor-latest",
                Mutations.SetCell("metrics", "reading", $"val{i}", new BigtableVersion(i)));

        var rows = await ReadAll(
            rows: RowSet.FromRowKeys("ts-sensor-latest"),
            filter: RowFilters.CellsPerColumnLimit(1));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("val5");
    }

    [Fact]
    public async Task TimeSeries_multi_sensor()
    {
        for (int sensor = 1; sensor <= 3; sensor++)
            for (int reading = 0; reading < 5; reading++)
                await Client.MutateRowAsync(TN, $"ts-ms-s{sensor}#r{reading}",
                    Mutations.SetCell("metrics", "value", $"s{sensor}r{reading}", new BigtableVersion(1000)));

        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("ts-ms-s2#", "ts-ms-s2~"));
        var rows = await ReadAll(rows: rowSet);
        rows.Should().HaveCount(5);
    }

    #endregion

    #region User profile pattern

    [Fact]
    public async Task UserProfile_CRUD()
    {
        // Create
        await Client.MutateRowAsync(TN, "user#u001",
            Mutations.SetCell("profile", "name", "Alice", new BigtableVersion(1000)),
            Mutations.SetCell("profile", "email", "alice@test.com", new BigtableVersion(1000)),
            Mutations.SetCell("profile", "age", "30", new BigtableVersion(1000)));

        // Read
        var rows = await ReadAll(rows: RowSet.FromRowKeys("user#u001"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns.Should().HaveCount(3);

        // Update
        await Client.MutateRowAsync(TN, "user#u001",
            Mutations.SetCell("profile", "age", "31", new BigtableVersion(2000)));

        rows = await ReadAll(rows: RowSet.FromRowKeys("user#u001"),
            filter: RowFilters.CellsPerColumnLimit(1));
        var ageCells = rows[0].Families[0].Columns
            .First(c => c.Qualifier.ToStringUtf8() == "age");
        ageCells.Cells[0].Value.ToStringUtf8().Should().Be("31");

        // Delete (one column)
        await Client.MutateRowAsync(TN, "user#u001",
            Mutations.DeleteFromColumn("profile", "email"));
        rows = await ReadAll(rows: RowSet.FromRowKeys("user#u001"),
            filter: RowFilters.CellsPerColumnLimit(1));
        var cols = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().NotContain("email");
    }

    [Fact]
    public async Task UserProfile_range_scan_by_prefix()
    {
        for (int i = 1; i <= 10; i++)
            await Client.MutateRowAsync(TN, $"uprof#user{i:D3}",
                Mutations.SetCell("profile", "name", $"User {i}", new BigtableVersion(1000)));

        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("uprof#user001", "uprof#user006"));
        var rows = await ReadAll(rows: rowSet);
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task UserProfile_conditional_update()
    {
        await Client.MutateRowAsync(TN, "uprof#cond",
            Mutations.SetCell("profile", "status", "active", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "uprof#cond",
            predicateFilter: RowFilters.Chain(
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.ValueExact("active"),
                RowFilters.CellsPerColumnLimit(1)),
            trueMutations: new[] { Mutations.SetCell("profile", "status", "banned", new BigtableVersion(2000)) },
            falseMutations: null);
        result.PredicateMatched.Should().BeTrue();
    }

    #endregion

    #region Event log pattern

    [Fact]
    public async Task EventLog_append_and_scan()
    {
        for (int i = 0; i < 20; i++)
            await Client.MutateRowAsync(TN, $"evt#2024-01-15#{i:D5}",
                Mutations.SetCell("events", "type", i % 3 == 0 ? "error" : "info", new BigtableVersion(1000)),
                Mutations.SetCell("events", "msg", $"Event {i}", new BigtableVersion(1000)));

        // Read first 10
        var rows = await ReadAll(limit: 10,
            filter: RowFilters.RowKeyRegex("evt#2024-01-15#.*"));
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task EventLog_filter_errors()
    {
        for (int i = 0; i < 10; i++)
            await Client.MutateRowAsync(TN, $"evt-err#2024#{i:D3}",
                Mutations.SetCell("events", "type", i % 2 == 0 ? "error" : "info", new BigtableVersion(1000)));

        var filter = RowFilters.Chain(
            RowFilters.RowKeyRegex("evt-err#2024#.*"),
            RowFilters.ColumnQualifierExact("type"),
            RowFilters.ValueExact("error"));
        var rows = await ReadAll(filter: filter);
        rows.Should().HaveCount(5);
    }

    #endregion

    #region Counter pattern

    [Fact]
    public async Task Counter_increment_and_read()
    {
        for (int i = 0; i < 10; i++)
            await Client.ReadModifyWriteRowAsync(TN, "ctr#page-views",
                ReadModifyWriteRules.Increment("metrics", "count", 1));

        var rows = await ReadAll(rows: RowSet.FromRowKeys("ctr#page-views"));
        var bytes = rows[0].Families[0].Columns[0].Cells[0].Value.ToByteArray();
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        var count = BitConverter.ToInt64(bytes, 0);
        count.Should().Be(10);
    }

    [Fact]
    public async Task Counter_multiple_counters()
    {
        await Client.ReadModifyWriteRowAsync(TN, "ctr#multi",
            ReadModifyWriteRules.Increment("metrics", "views", 5),
            ReadModifyWriteRules.Increment("metrics", "clicks", 2));
        await Client.ReadModifyWriteRowAsync(TN, "ctr#multi",
            ReadModifyWriteRules.Increment("metrics", "views", 3),
            ReadModifyWriteRules.Increment("metrics", "clicks", 1));

        var rows = await ReadAll(rows: RowSet.FromRowKeys("ctr#multi"));
        var cols = rows[0].Families[0].Columns;
        foreach (var col in cols)
        {
            var bts = col.Cells[0].Value.ToByteArray();
            if (BitConverter.IsLittleEndian) Array.Reverse(bts);
            var val = BitConverter.ToInt64(bts, 0);
            if (col.Qualifier.ToStringUtf8() == "views") val.Should().Be(8);
            if (col.Qualifier.ToStringUtf8() == "clicks") val.Should().Be(3);
        }
    }

    #endregion

    #region Shopping cart pattern

    [Fact]
    public async Task ShoppingCart_add_items_and_check()
    {
        await Client.MutateRowAsync(TN, "cart#c001",
            Mutations.SetCell("events", "item1", "Widget A", new BigtableVersion(1000)),
            Mutations.SetCell("events", "item2", "Widget B", new BigtableVersion(1000)));

        // Add another item
        await Client.MutateRowAsync(TN, "cart#c001",
            Mutations.SetCell("events", "item3", "Widget C", new BigtableVersion(2000)));

        var rows = await ReadAll(rows: RowSet.FromRowKeys("cart#c001"));
        rows[0].Families[0].Columns.Should().HaveCount(3);

        // Remove item2
        await Client.MutateRowAsync(TN, "cart#c001",
            Mutations.DeleteFromColumn("events", "item2"));

        rows = await ReadAll(rows: RowSet.FromRowKeys("cart#c001"));
        var items = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        items.Should().HaveCount(2);
        items.Should().NotContain("item2");
    }

    [Fact]
    public async Task ShoppingCart_checkout_clears_cart()
    {
        await Client.MutateRowAsync(TN, "cart#c002",
            Mutations.SetCell("events", "item1", "Product X", new BigtableVersion(1000)));

        // Checkout = delete all
        await Client.MutateRowAsync(TN, "cart#c002", Mutations.DeleteFromRow());

        var rows = await ReadAll(rows: RowSet.FromRowKeys("cart#c002"));
        rows.Should().BeEmpty();
    }

    #endregion

    #region Audit trail pattern

    [Fact]
    public async Task AuditTrail_version_history()
    {
        // Each version represents a change
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "audit#doc001",
                Mutations.SetCell("profile", "content", $"revision-{i}", new BigtableVersion(i)));

        // Read all revisions
        var rows = await ReadAll(rows: RowSet.FromRowKeys("audit#doc001"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(5);
        // Latest first
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("revision-5");
        rows[0].Families[0].Columns[0].Cells[4].Value.ToStringUtf8().Should().Be("revision-1");
    }

    [Fact]
    public async Task AuditTrail_latest_only()
    {
        for (int i = 1; i <= 3; i++)
            await Client.MutateRowAsync(TN, "audit#doc002",
                Mutations.SetCell("profile", "status", $"state-{i}", new BigtableVersion(i)));

        var rows = await ReadAll(
            rows: RowSet.FromRowKeys("audit#doc002"),
            filter: RowFilters.CellsPerColumnLimit(1));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("state-3");
    }

    #endregion
}

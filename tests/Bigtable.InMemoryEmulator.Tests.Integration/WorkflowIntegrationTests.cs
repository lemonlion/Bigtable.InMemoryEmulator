using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// End-to-end workflow tests — realistic multi-step scenarios.
///
/// Ref: https://cloud.google.com/bigtable/docs/how-to
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class WorkflowIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "workflow";
    private const string CF = "cf";
    private const string CF2 = "meta";

    public WorkflowIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
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

    private static long ReadInt64(ByteString value)
    {
        var bytes = value.ToByteArray();
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToInt64(bytes, 0);
    }

    #region User profile CRUD workflow

    [Fact]
    public async Task User_profile_create_read_update_delete()
    {
        var userKey = "wf-user#001";

        // Create
        await Client.MutateRowAsync(TN, userKey,
            Mutations.SetCell(CF, "name", "Alice", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "email", "alice@example.com", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "created", "2024-01-01", new BigtableVersion(1000)));

        // Read
        var rows = await ReadAll(RowSet.FromRowKeys(userKey));
        rows.Should().ContainSingle();
        var cfCols = rows[0].Families.First(f => f.Name == CF).Columns;
        cfCols.First(c => c.Qualifier.ToStringUtf8() == "name").Cells[0].Value.ToStringUtf8()
            .Should().Be("Alice");

        // Update
        await Client.MutateRowAsync(TN, userKey,
            Mutations.SetCell(CF, "email", "alice2@example.com", new BigtableVersion(2000)));
        rows = await ReadAll(RowSet.FromRowKeys(userKey), RowFilters.CellsPerColumnLimit(1));
        cfCols = rows[0].Families.First(f => f.Name == CF).Columns;
        cfCols.First(c => c.Qualifier.ToStringUtf8() == "email").Cells[0].Value.ToStringUtf8()
            .Should().Be("alice2@example.com");

        // Delete
        await Client.MutateRowAsync(TN, userKey, Mutations.DeleteFromRow());
        rows = await ReadAll(RowSet.FromRowKeys(userKey));
        rows.Should().BeEmpty();
    }

    #endregion

    #region Counter/metrics workflow

    [Fact]
    public async Task Metrics_accumulation_workflow()
    {
        var key = "wf-metric#page_views";

        // Increment 5 times
        for (int i = 0; i < 5; i++)
            await Client.ReadModifyWriteRowAsync(TN, key,
                ReadModifyWriteRules.Increment(CF, "total", 10));

        // Read counter
        var rows = await ReadAll(RowSet.FromRowKeys(key));
        var total = ReadInt64(rows[0].Families[0].Columns[0].Cells[0].Value);
        total.Should().Be(50);
    }

    [Fact]
    public async Task Event_log_append_workflow()
    {
        var key = "wf-log#app1";

        await Client.ReadModifyWriteRowAsync(TN, key,
            ReadModifyWriteRules.Append(CF, "events", "[start]"));
        await Client.ReadModifyWriteRowAsync(TN, key,
            ReadModifyWriteRules.Append(CF, "events", "[process]"));
        await Client.ReadModifyWriteRowAsync(TN, key,
            ReadModifyWriteRules.Append(CF, "events", "[end]"));

        var rows = await ReadAll(RowSet.FromRowKeys(key));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8()
            .Should().Be("[start][process][end]");
    }

    #endregion

    #region Conditional update workflow

    [Fact]
    public async Task Conditional_status_transition_active_to_archived()
    {
        var key = "wf-status#order001";
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF, "status", "active", new BigtableVersion(1000)));

        // Only archive if currently active
        var result = await Client.CheckAndMutateRowAsync(TN, key,
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueExact("active")),
            new[] { Mutations.SetCell(CF, "status", "archived", new BigtableVersion(2000)) },
            null);
        result.PredicateMatched.Should().BeTrue();

        // Verify transition
        var rows = await ReadAll(RowSet.FromRowKeys(key), RowFilters.CellsPerColumnLimit(1));
        rows[0].Families.First(f => f.Name == CF).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "status").Cells[0].Value.ToStringUtf8()
            .Should().Be("archived");
    }

    [Fact]
    public async Task Conditional_update_prevents_double_processing()
    {
        var key = "wf-proc#job001";
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF, "status", "pending", new BigtableVersion(1000)));

        // First claim succeeds (check latest only)
        var r1 = await Client.CheckAndMutateRowAsync(TN, key,
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueExact("pending")),
            new[] { Mutations.SetCell(CF, "status", "processing", new BigtableVersion(2000)) },
            null);
        r1.PredicateMatched.Should().BeTrue();

        // Second claim fails (latest is now "processing")
        var r2 = await Client.CheckAndMutateRowAsync(TN, key,
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueExact("pending")),
            new[] { Mutations.SetCell(CF, "status", "processing", new BigtableVersion(3000)) },
            null);
        r2.PredicateMatched.Should().BeFalse();
    }

    #endregion

    #region Batch write + scan workflow

    [Fact]
    public async Task Batch_write_then_prefix_scan()
    {
        var entries = Enumerable.Range(0, 20).Select(i =>
            Mutations.CreateEntry($"wf-scan#product#{i:D4}",
                Mutations.SetCell(CF, "name", $"Product {i}", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "price", $"{i * 10}", new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);

        // Prefix scan
        var rows = await ReadAll(RowSet.FromRowRanges(
            RowRange.ClosedOpen("wf-scan#product#", "wf-scan#product~")));
        rows.Should().HaveCount(20);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Batch_write_then_filter_scan()
    {
        var entries = Enumerable.Range(0, 10).Select(i =>
            Mutations.CreateEntry($"wf-fscan#item#{i:D3}",
                Mutations.SetCell(CF, "type", i % 2 == 0 ? "A" : "B", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "value", $"{i}", new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);

        // Filter for type=A
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("type"),
            RowFilters.ValueExact("A"));
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("wf-fscan#item#", "wf-fscan#item~")),
            filter);
        rows.Should().HaveCount(5);
    }

    #endregion

    #region Multi-version history workflow

    [Fact]
    public async Task Version_history_workflow()
    {
        var key = "wf-hist#config#app1";

        // Write 5 config versions
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, key,
                Mutations.SetCell(CF, "settings", $"{{\"ver\":{i}}}", new BigtableVersion(i * 1000)));

        // Read latest only
        var rows = await ReadAll(RowSet.FromRowKeys(key), RowFilters.CellsPerColumnLimit(1));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("{\"ver\":5}");

        // Read full history
        rows = await ReadAll(RowSet.FromRowKeys(key));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(5);
        var timestamps = rows[0].Families[0].Columns[0].Cells.Select(c => c.TimestampMicros).ToList();
        timestamps.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Purge_old_versions_workflow()
    {
        var key = "wf-purge#data";

        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, key,
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));

        // Delete old versions (keep 4ms and 5ms)
        await Client.MutateRowAsync(TN, key,
            Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(new BigtableVersion(0), new BigtableVersion(4000))));

        var rows = await ReadAll(RowSet.FromRowKeys(key));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    #endregion

    #region Write + immediate read consistency

    [Fact]
    public async Task Write_then_immediate_read_consistent()
    {
        for (int i = 0; i < 10; i++)
        {
            var key = $"wf-cons#{i}";
            await Client.MutateRowAsync(TN, key,
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)));
            var rows = await ReadAll(RowSet.FromRowKeys(key));
            rows.Should().ContainSingle();
            rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be($"v{i}");
        }
    }

    [Fact]
    public async Task Delete_then_immediate_read_consistent()
    {
        var key = "wf-dcons";
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, key, Mutations.DeleteFromRow());
        var rows = await ReadAll(RowSet.FromRowKeys(key));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task RMW_then_immediate_read_consistent()
    {
        var key = "wf-rmwcons";
        await Client.ReadModifyWriteRowAsync(TN, key,
            ReadModifyWriteRules.Increment(CF, "counter", 42));
        var rows = await ReadAll(RowSet.FromRowKeys(key));
        ReadInt64(rows[0].Families[0].Columns[0].Cells[0].Value).Should().Be(42);
    }

    #endregion

    #region Multi-table workflow

    [Fact]
    public async Task Cross_reference_workflow()
    {
        // Simulate a simple foreign key pattern
        var userKey = "wf-xref#user#001";
        var orderKey = "wf-xref#order#A01";

        await Client.MutateRowAsync(TN, userKey,
            Mutations.SetCell(CF, "name", "Bob", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "order_ref", "A01", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, orderKey,
            Mutations.SetCell(CF, "user_ref", "001", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "total", "99.99", new BigtableVersion(1000)));

        // Read user, find order ref
        var users = await ReadAll(RowSet.FromRowKeys(userKey));
        var orderRef = users[0].Families.First(f => f.Name == CF).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "order_ref").Cells[0].Value.ToStringUtf8();

        // Read order by ref
        var orders = await ReadAll(RowSet.FromRowKeys($"wf-xref#order#{orderRef}"));
        orders.Should().ContainSingle();
        orders[0].Families.First(f => f.Name == CF).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "total").Cells[0].Value.ToStringUtf8()
            .Should().Be("99.99");
    }

    #endregion

    #region Paginated scan

    [Fact]
    public async Task Paginated_scan_covers_all_rows()
    {
        // Write 25 rows
        for (int i = 0; i < 25; i++)
            await Client.MutateRowAsync(TN, $"wf-page#{i:D3}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)));

        // Read in pages of 10
        var allKeys = new List<string>();
        string? lastKey = null;
        while (true)
        {
            RowSet rowSet;
            if (lastKey == null)
                rowSet = RowSet.FromRowRanges(RowRange.ClosedOpen("wf-page#", "wf-page~"));
            else
                rowSet = RowSet.FromRowRanges(RowRange.Open(lastKey, "wf-page~"));

            var rows = await ReadAll(rowSet, limit: 10);
            if (rows.Count == 0) break;
            foreach (var r in rows)
                allKeys.Add(r.Key.ToStringUtf8());
            lastKey = rows.Last().Key.ToStringUtf8();
        }
        allKeys.Should().HaveCount(25);
        allKeys.Should().BeInAscendingOrder();
    }

    #endregion
}

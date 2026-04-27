using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// End-to-end domain scenario tests: realistic Bigtable usage patterns.
///
/// Ref: https://cloud.google.com/bigtable/docs/schema-design
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class DomainScenarioAdvancedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;

    public DomainScenarioAdvancedTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync("domain-adv", new[] { "profile", "activity", "metrics" });
    }
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("domain-adv");

    [Fact]
    public async Task User_profile_CRUD()
    {
        // Create
        await Client.MutateRowAsync(TN, "user#001",
            Mutations.SetCell("profile", "name", "Alice", new BigtableVersion(1000)),
            Mutations.SetCell("profile", "email", "alice@test.com", new BigtableVersion(1000)),
            Mutations.SetCell("profile", "created", "2024-01-01", new BigtableVersion(1000)));

        // Read
        var row = await Client.ReadRowAsync(TN, "user#001");
        row.Should().NotBeNull();
        var name = row!.Families.First(f => f.Name == "profile").Columns
            .First(c => c.Qualifier.ToStringUtf8() == "name").Cells[0].Value.ToStringUtf8();
        name.Should().Be("Alice");

        // Update
        await Client.MutateRowAsync(TN, "user#001",
            Mutations.SetCell("profile", "name", "Alice Smith", new BigtableVersion(2000)));

        var updated = await Client.ReadRowAsync(TN, "user#001");
        var latestName = updated!.Families.First(f => f.Name == "profile").Columns
            .First(c => c.Qualifier.ToStringUtf8() == "name").Cells[0].Value.ToStringUtf8();
        latestName.Should().Be("Alice Smith");

        // Delete
        await Client.MutateRowAsync(TN, "user#001", Mutations.DeleteFromRow());
        var deleted = await Client.ReadRowAsync(TN, "user#001");
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task Activity_log_time_series()
    {
        // Write activity events with timestamps
        for (int day = 1; day <= 7; day++)
        {
            await Client.MutateRowAsync(TN, "user#002",
                Mutations.SetCell("activity", $"day-{day:D2}",
                    $"action-{day}", new BigtableVersion(day * 1000)));
        }

        // Read all activity
        var row = await Client.ReadRowAsync(TN, "user#002");
        var activity = row!.Families.First(f => f.Name == "activity");
        activity.Columns.Should().HaveCount(7);

        // Filter last 3 days
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact("activity"),
            RowFilters.ColumnQualifierRegex("day-0[5-7]"));

        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, RowSet.FromRowKeys("user#002"), filter))
            rows.Add(r);

        rows.Should().ContainSingle();
        rows[0].Families[0].Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task Metric_counter_pattern()
    {
        // Initial metric values
        var zero = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(0L));
        await Client.MutateRowAsync(TN, "user#003",
            Mutations.SetCell("metrics", "page-views", zero, new BigtableVersion(1000)),
            Mutations.SetCell("metrics", "api-calls", zero, new BigtableVersion(1000)));

        // Increment page views
        for (int i = 0; i < 10; i++)
        {
            await Client.ReadModifyWriteRowAsync(TN, "user#003",
                ReadModifyWriteRules.Increment("metrics", "page-views", 1));
        }

        // Increment API calls
        for (int i = 0; i < 5; i++)
        {
            await Client.ReadModifyWriteRowAsync(TN, "user#003",
                ReadModifyWriteRules.Increment("metrics", "api-calls", 1));
        }

        // Read metrics
        var row = await Client.ReadRowAsync(TN, "user#003");
        var metrics = row!.Families.First(f => f.Name == "metrics");

        var pageViews = System.Net.IPAddress.NetworkToHostOrder(BitConverter.ToInt64(
            metrics.Columns.First(c => c.Qualifier.ToStringUtf8() == "page-views").Cells[0].Value.ToByteArray()));
        var apiCalls = System.Net.IPAddress.NetworkToHostOrder(BitConverter.ToInt64(
            metrics.Columns.First(c => c.Qualifier.ToStringUtf8() == "api-calls").Cells[0].Value.ToByteArray()));

        pageViews.Should().Be(10);
        apiCalls.Should().Be(5);
    }

    [Fact]
    public async Task Range_scan_by_prefix()
    {
        // Create users
        for (int i = 1; i <= 20; i++)
            await Client.MutateRowAsync(TN, $"user#{i:D3}",
                Mutations.SetCell("profile", "name", $"User-{i}", new BigtableVersion(1000)));

        // Scan users 10-19
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN,
            new RowSet
            {
                RowRanges = { RowRange.ClosedOpen("user#010", "user#020") }
            }))
            rows.Add(row);

        rows.Should().HaveCount(10);
        rows.First().Key.ToStringUtf8().Should().Be("user#010");
        rows.Last().Key.ToStringUtf8().Should().Be("user#019");
    }

    [Fact]
    public async Task Conditional_update_pattern()
    {
        // Create a user with status "active"
        await Client.MutateRowAsync(TN, "user#cond",
            Mutations.SetCell("profile", "status", "active", new BigtableVersion(1000)),
            Mutations.SetCell("profile", "name", "Test User", new BigtableVersion(1000)));

        // Try to deactivate (only if currently active)
        var response = await Client.CheckAndMutateRowAsync(TN, "user#cond",
            RowFilters.Chain(
                RowFilters.FamilyNameExact("profile"),
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueRegex("active")),
            trueMutations: new[]
            {
                Mutations.SetCell("profile", "status", "inactive", new BigtableVersion(2000)),
                Mutations.SetCell("activity", "deactivated", "2024-01-15", new BigtableVersion(2000))
            },
            falseMutations: null);

        response.PredicateMatched.Should().BeTrue();

        // Verify
        var row = await Client.ReadRowAsync(TN, "user#cond");
        row!.Families.First(f => f.Name == "profile").Columns
            .First(c => c.Qualifier.ToStringUtf8() == "status")
            .Cells[0].Value.ToStringUtf8().Should().Be("inactive");
    }

    [Fact]
    public async Task Multi_tenant_isolation()
    {
        // Different tenants share same table but use key prefixes
        await Client.MutateRowAsync(TN, "tenant-a#doc#1",
            Mutations.SetCell("profile", "data", "a-secret", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "tenant-b#doc#1",
            Mutations.SetCell("profile", "data", "b-secret", new BigtableVersion(1000)));

        // Scan only tenant-a
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN,
            new RowSet
            {
                RowRanges = { RowRange.ClosedOpen("tenant-a#", "tenant-a$") }
            }))
            rows.Add(row);

        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().StartWith("tenant-a#");
    }

    [Fact]
    public async Task Versioned_config_pattern()
    {
        // Store config changes over time
        await Client.MutateRowAsync(TN, "config#main",
            Mutations.SetCell("profile", "max-users", "100", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "config#main",
            Mutations.SetCell("profile", "max-users", "200", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, "config#main",
            Mutations.SetCell("profile", "max-users", "500", new BigtableVersion(3000)));

        // Read latest
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact("profile"),
            RowFilters.ColumnQualifierExact("max-users"),
            RowFilters.CellsPerColumnLimit(1));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("config#main"), filter))
            rows.Add(row);

        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("500");

        // Read all versions
        var historyRows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("config#main"),
            RowFilters.Chain(
                RowFilters.FamilyNameExact("profile"),
                RowFilters.ColumnQualifierExact("max-users"))))
            historyRows.Add(row);

        historyRows[0].Families[0].Columns[0].Cells.Should().HaveCount(3);
    }
}

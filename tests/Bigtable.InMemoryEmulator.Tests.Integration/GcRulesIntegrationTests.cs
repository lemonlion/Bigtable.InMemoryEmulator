using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Integration tests for GC rule enforcement (MaxVersions, MaxAge).
///
/// Ref: https://cloud.google.com/bigtable/docs/garbage-collection
///   "Garbage collection removes expired data during compaction and on read."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class GcRulesIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "gc-tests";
    private const string Family = "cf";

    public GcRulesIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { Family });

        // Add a family with max-versions GC rule via Admin API
        var tablePath = _fixture.InstanceName + "/tables/" + Table;

        await _fixture.AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = tablePath,
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "gcv",
                    Create = new Google.Cloud.Bigtable.Admin.V2.ColumnFamily
                    {
                        GcRule = new Google.Cloud.Bigtable.Admin.V2.GcRule
                        {
                            MaxNumVersions = 2,
                        }
                    }
                },
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "gca",
                    Create = new Google.Cloud.Bigtable.Admin.V2.ColumnFamily
                    {
                        GcRule = new Google.Cloud.Bigtable.Admin.V2.GcRule
                        {
                            MaxAge = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromHours(1)),
                        }
                    }
                },
            }
        });
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    // Go emulator divergence: does not apply GC rules at read time; cells are only removed during compaction.
    // Ref: https://cloud.google.com/bigtable/docs/garbage-collection#when_data_is_deleted
    //   "Until the data is deleted, it appears in read results."
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task MaxVersions_rule_limits_returned_cells()
    {
        // Ref: https://cloud.google.com/bigtable/docs/garbage-collection#max-versions
        //   "Store only the most recent n versions of each value."
        var rowKey = new BigtableByteString("gc-mv1");

        // Write 4 versions
        for (int i = 1; i <= 4; i++)
        {
            await Client.MutateRowAsync(TN, rowKey,
                Mutations.SetCell("gcv", "col", $"v{i}", new BigtableVersion(i * 1000)));
        }

        var row = await Client.ReadRowAsync(TN, rowKey);
        row.Should().NotBeNull();

        var cells = row!.Families
            .First(f => f.Name == "gcv")
            .Columns.First()
            .Cells;

        // MaxNumVersions = 2 — should only return 2 most recent versions
        cells.Should().HaveCount(2);
        cells[0].Value.ToStringUtf8().Should().Be("v4");
        cells[1].Value.ToStringUtf8().Should().Be("v3");
    }

    // Go emulator divergence: does not apply GC rules at read time; cells are only removed during compaction.
    // Ref: https://cloud.google.com/bigtable/docs/garbage-collection#when_data_is_deleted
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task MaxAge_rule_filters_expired_cells()
    {
        // Ref: https://cloud.google.com/bigtable/docs/garbage-collection#max-age
        //   "Delete cells that are older than the configured age."
        var rowKey = new BigtableByteString("gc-ma1");

        // Write a cell with a timestamp 2 hours ago (should be expired, MaxAge = 1h)
        // BigtableVersion constructor takes milliseconds; .Micros = value * 1000
        var twoHoursAgoMs = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds();
        twoHoursAgoMs = twoHoursAgoMs / 1000 * 1000; // round to seconds

        await Client.MutateRowAsync(TN, rowKey,
            Mutations.SetCell("gca", "col", "old", new BigtableVersion(twoHoursAgoMs)));

        // Write a recent cell
        var recentMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        recentMs = recentMs / 1000 * 1000; // round to seconds
        await Client.MutateRowAsync(TN, rowKey,
            Mutations.SetCell("gca", "col", "new", new BigtableVersion(recentMs)));

        var row = await Client.ReadRowAsync(TN, rowKey);
        row.Should().NotBeNull();

        var cells = row!.Families
            .First(f => f.Name == "gca")
            .Columns.First()
            .Cells;

        // The old cell (2h ago) should be filtered out by MaxAge (1h)
        cells.Should().HaveCount(1);
        cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Family_without_gc_rule_keeps_all_versions()
    {
        var rowKey = new BigtableByteString("gc-no");

        // Write 5 versions to the family without GC rules
        for (int i = 1; i <= 5; i++)
        {
            await Client.MutateRowAsync(TN, rowKey,
                Mutations.SetCell(Family, "col", $"v{i}", new BigtableVersion(i * 1000)));
        }

        var row = await Client.ReadRowAsync(TN, rowKey);
        row.Should().NotBeNull();

        var cells = row!.Families
            .First(f => f.Name == Family)
            .Columns.First()
            .Cells;

        // No GC rule — all 5 versions retained
        cells.Should().HaveCount(5);
    }

    [Fact]
    public async Task GetTable_returns_gc_rules_for_families()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#gettablerequest
        var tablePath = _fixture.InstanceName + "/tables/" + Table;
        var table = await _fixture.AdminClient.GetTableAsync(tablePath);

        table.ColumnFamilies["gcv"].GcRule.MaxNumVersions.Should().Be(2);
        table.ColumnFamilies["gca"].GcRule.MaxAge.Should().NotBeNull();
    }
}

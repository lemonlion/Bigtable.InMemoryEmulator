using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for composite key patterns used in Bigtable schema design.
///
/// Ref: https://cloud.google.com/bigtable/docs/schema-design
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CompositeKeyPatternTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public CompositeKeyPatternTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync("comp-key", new[] { CF });
    }
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("comp-key");

    #region Hash-prefixed keys

    [Fact]
    public async Task Hash_prefix_distributes_writes()
    {
        // Schema: rowkey = "<hash_prefix>#<entity_id>"
        for (int i = 0; i < 20; i++)
        {
            var hash = (i % 4).ToString("D2"); // 4 buckets: 00, 01, 02, 03
            await Client.MutateRowAsync(TN, $"{hash}#entity-{i:D4}",
                Mutations.SetCell(CF, "val", $"data-{i}", new BigtableVersion(1000)));
        }

        // Scan bucket 00
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("00#", "01#"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);
        rows.Should().HaveCount(5); // 0,4,8,12,16
    }

    [Fact]
    public async Task Hash_prefix_scan_all_buckets()
    {
        for (int i = 0; i < 12; i++)
        {
            var hash = (i % 3).ToString("D2");
            await Client.MutateRowAsync(TN, $"hb-{hash}#item-{i:D4}",
                Mutations.SetCell(CF, "v", "x", new BigtableVersion(1000)));
        }

        // Read all buckets
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("hb-00#", "hb-03#"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);
        rows.Should().HaveCount(12);
    }

    #endregion

    #region Reverse timestamp keys

    [Fact]
    public async Task Reverse_timestamp_ordered_newest_first()
    {
        var baseTs = 1700000000L;
        for (int i = 0; i < 5; i++)
        {
            var ts = baseTs + i * 1000;
            var reverseTs = (long.MaxValue - ts).ToString("D19");
            await Client.MutateRowAsync(TN, $"ts#{reverseTs}",
                Mutations.SetCell(CF, "event", $"event-{i}", new BigtableVersion(1000)));
        }

        // Reading in order should give newest first (highest original ts → lowest reverse ts)
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("ts#", "ts$"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);
        rows.Should().HaveCount(5);
        // First row should be newest event (event-4)
        var firstVal = rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8();
        firstVal.Should().Be("event-4");
    }

    [Fact]
    public async Task Reverse_timestamp_with_entity_prefix()
    {
        var baseTs = 1800000000L;
        foreach (var user in new[] { "alice", "bob" })
        {
            for (int i = 0; i < 3; i++)
            {
                var reverseTs = (long.MaxValue - (baseTs + i * 100)).ToString("D19");
                await Client.MutateRowAsync(TN, $"rts#{user}#{reverseTs}",
                    Mutations.SetCell(CF, "action", $"act-{i}", new BigtableVersion(1000)));
            }
        }

        // Read alice's events
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("rts#alice#", "rts#alice$"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);
        rows.Should().HaveCount(3);
    }

    #endregion

    #region Time-bucketed keys

    [Fact]
    public async Task Time_bucket_daily()
    {
        // Schema: "metrics#<date>#<metric_name>"
        var dates = new[] { "2024-01-15", "2024-01-16", "2024-01-17" };
        var metrics = new[] { "cpu", "mem", "disk" };
        foreach (var date in dates)
            foreach (var metric in metrics)
                await Client.MutateRowAsync(TN, $"metrics#{date}#{metric}",
                    Mutations.SetCell(CF, "value", "42.5", new BigtableVersion(1000)));

        // Query single day
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("metrics#2024-01-16#", "metrics#2024-01-17#"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);
        rows.Should().HaveCount(3); // 3 metrics for that day
    }

    [Fact]
    public async Task Time_bucket_range_query()
    {
        for (int day = 1; day <= 10; day++)
            await Client.MutateRowAsync(TN, $"daily#2024-02-{day:D2}#summary",
                Mutations.SetCell(CF, "total", day.ToString(), new BigtableVersion(1000)));

        // Query days 3-7
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.Closed("daily#2024-02-03#", "daily#2024-02-07#summary"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);
        rows.Should().HaveCount(5);
    }

    #endregion

    #region Hierarchical keys

    [Fact]
    public async Task Hierarchical_org_team_member()
    {
        var data = new[]
        {
            ("org1#teamA#alice", "eng"),
            ("org1#teamA#bob", "eng"),
            ("org1#teamB#charlie", "sales"),
            ("org2#teamA#diana", "eng"),
            ("org2#teamA#eve", "eng"),
        };
        foreach (var (key, dept) in data)
            await Client.MutateRowAsync(TN, key,
                Mutations.SetCell(CF, "dept", dept, new BigtableVersion(1000)));

        // Query all of org1
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("org1#", "org2#"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Hierarchical_prefix_scan()
    {
        for (int i = 0; i < 6; i++)
            await Client.MutateRowAsync(TN, $"geo#us#ca#{i:D3}",
                Mutations.SetCell(CF, "v", "x", new BigtableVersion(1000)));
        for (int i = 0; i < 4; i++)
            await Client.MutateRowAsync(TN, $"geo#us#ny#{i:D3}",
                Mutations.SetCell(CF, "v", "x", new BigtableVersion(1000)));

        // Scan all US locations
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("geo#us#", "geo#us$"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);
        rows.Should().HaveCount(10);
    }

    #endregion

    #region Padded numeric keys

    [Fact]
    public async Task Padded_keys_sort_numerically()
    {
        var values = new[] { 1, 5, 10, 50, 100, 500, 1000 };
        foreach (var v in values)
            await Client.MutateRowAsync(TN, $"num#{v:D8}",
                Mutations.SetCell(CF, "v", v.ToString(), new BigtableVersion(1000)));

        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("num#", "num$"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);
        rows.Should().HaveCount(7);

        // Should be in ascending numeric order due to padding
        var vals = rows.Select(r =>
            int.Parse(r.Families[0].Columns[0].Cells[0].Value.ToStringUtf8())).ToList();
        vals.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Padded_keys_range_scan()
    {
        for (int i = 0; i < 100; i++)
            await Client.MutateRowAsync(TN, $"idx#{i:D6}",
                Mutations.SetCell(CF, "v", "x", new BigtableVersion(1000)));

        // Scan 25-75
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.Closed("idx#000025", "idx#000075"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);
        rows.Should().HaveCount(51); // inclusive on both ends
    }

    #endregion

    #region Multi-component keys with filters

    [Fact]
    public async Task Composite_key_with_value_filter()
    {
        for (int i = 0; i < 10; i++)
            await Client.MutateRowAsync(TN, $"product#cat{i % 3}#item{i:D3}",
                Mutations.SetCell(CF, "status", i % 2 == 0 ? "active" : "inactive", new BigtableVersion(1000)));

        // Filter active products in category 0
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("product#cat0#", "product#cat1#"));
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("status"),
            RowFilters.ValueExact("active"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet, filter))
            rows.Add(row);
        // Cat0: items 0,3,6,9 → active: 0,6 → 2
        rows.Should().HaveCount(2);
    }

    #endregion
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for timestamp-based operations: version ranges, timestamp filters, ordering.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class TimestampOperationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "tso-tests";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public TimestampOperationTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });

        // Create a row with 10 versions
        for (int i = 1; i <= 10; i++)
            await Client.MutateRowAsync(TN, "tso-versions",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task All_versions_present()
    {
        var row = await Client.ReadRowAsync(TN, "tso-versions");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(10);
    }

    [Fact]
    public async Task Cells_ordered_by_timestamp_descending()
    {
        var row = await Client.ReadRowAsync(TN, "tso-versions");
        var timestamps = row!.Families[0].Columns[0].Cells
            .Select(c => c.TimestampMicros).ToList();
        timestamps.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Latest_version_is_first()
    {
        var row = await Client.ReadRowAsync(TN, "tso-versions");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v10");
    }

    [Fact]
    public async Task TimestampRange_single_version()
    {
        var start = new DateTime(1970, 1, 1, 0, 0, 5, DateTimeKind.Utc);  // 5000ms
        var end = new DateTime(1970, 1, 1, 0, 0, 6, DateTimeKind.Utc);    // 6000ms
        var request = MakeRequest(RowFilters.TimestampRange(start, end));
        var vals = await CollectValues(request);
        vals.Should().ContainSingle("v5");
    }

    [Fact]
    public async Task TimestampRange_multiple_versions()
    {
        var start = new DateTime(1970, 1, 1, 0, 0, 3, DateTimeKind.Utc);
        var end = new DateTime(1970, 1, 1, 0, 0, 7, DateTimeKind.Utc);
        var request = MakeRequest(RowFilters.TimestampRange(start, end));
        var vals = await CollectValues(request);
        vals.Should().HaveCount(4); // 3000, 4000, 5000, 6000
    }

    [Fact]
    public async Task TimestampRange_no_match()
    {
        var start = new DateTime(1970, 1, 1, 0, 0, 20, DateTimeKind.Utc);
        var end = new DateTime(1970, 1, 1, 0, 0, 30, DateTimeKind.Utc);
        var request = MakeRequest(RowFilters.TimestampRange(start, end));
        var vals = await CollectValues(request);
        vals.Should().BeEmpty();
    }

    [Fact]
    public async Task CellsPerColumnLimit_1_is_latest()
    {
        var request = MakeRequest(RowFilters.CellsPerColumnLimit(1));
        var vals = await CollectValues(request);
        vals.Should().ContainSingle("v10");
    }

    [Fact]
    public async Task CellsPerColumnLimit_5()
    {
        var request = MakeRequest(RowFilters.CellsPerColumnLimit(5));
        var vals = await CollectValues(request);
        vals.Should().HaveCount(5);
        vals.Should().Contain("v10");
        vals.Should().Contain("v6");
    }

    [Fact]
    public async Task DeleteFromColumn_version_range_leaves_others()
    {
        await Client.MutateRowAsync(TN, "tso-delrange",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));

        await Client.MutateRowAsync(TN, "tso-delrange",
            Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(new BigtableVersion(1000), new BigtableVersion(3000))));

        var row = await Client.ReadRowAsync(TN, "tso-delrange");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("v3");
    }

    [Fact]
    public async Task Same_timestamp_overwrites()
    {
        await Client.MutateRowAsync(TN, "tso-overwrite",
            Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "tso-overwrite",
            Mutations.SetCell(CF, "c", "second", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "tso-overwrite");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task Different_timestamps_accumulate()
    {
        await Client.MutateRowAsync(TN, "tso-accum",
            Mutations.SetCell(CF, "c", "a", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "tso-accum",
            Mutations.SetCell(CF, "c", "b", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, "tso-accum");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task Timestamp_filter_with_chain()
    {
        var start = new DateTime(1970, 1, 1, 0, 0, 8, DateTimeKind.Utc);
        var end = new DateTime(1970, 1, 1, 0, 0, 11, DateTimeKind.Utc); // Covers 8, 9, 10

        var request = MakeRequest(RowFilters.Chain(
            RowFilters.TimestampRange(start, end),
            RowFilters.CellsPerColumnLimit(2)));
        var vals = await CollectValues(request);
        vals.Should().HaveCount(2);
    }

    [Fact]
    public async Task Timestamp_micros_stored_correctly()
    {
        await Client.MutateRowAsync(TN, "tso-micro",
            Mutations.SetCell(CF, "c", "val", new BigtableVersion(5000)));

        var row = await Client.ReadRowAsync(TN, "tso-micro");
        // BigtableVersion(5000) = 5000ms = 5,000,000 microseconds
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(5000 * 1000);
    }

    [Fact]
    public async Task Multiple_columns_different_timestamps()
    {
        await Client.MutateRowAsync(TN, "tso-multicol",
            Mutations.SetCell(CF, "a", "va", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "vb", new BigtableVersion(5000)));

        var start = new DateTime(1970, 1, 1, 0, 0, 4, DateTimeKind.Utc);
        var end = new DateTime(1970, 1, 1, 0, 0, 6, DateTimeKind.Utc);

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.TimestampRange(start, end),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("tso-multicol") } }
        };
        var cols = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
                cols.Add(c.Qualifier.ToStringUtf8());
        cols.Should().ContainSingle("b");
    }

    [Fact]
    public async Task Version_1ms_granularity()
    {
        await Client.MutateRowAsync(TN, "tso-1ms",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2)));

        var row = await Client.ReadRowAsync(TN, "tso-1ms");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task Large_timestamp()
    {
        var ts = new BigtableVersion(1_000_000_000); // 1 billion ms ≈ 31 years
        await Client.MutateRowAsync(TN, "tso-largets",
            Mutations.SetCell(CF, "c", "future", ts));

        var row = await Client.ReadRowAsync(TN, "tso-largets");
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(1_000_000_000L * 1000);
    }

    private ReadRowsRequest MakeRequest(RowFilter filter) =>
        new()
        {
            TableNameAsTableName = TN,
            Filter = filter,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("tso-versions") } }
        };

    private async Task<List<string>> CollectValues(ReadRowsRequest request)
    {
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                vals.Add(cell.Value.ToStringUtf8());
        return vals;
    }
}

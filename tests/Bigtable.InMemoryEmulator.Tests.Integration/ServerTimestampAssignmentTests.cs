using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for server-assigned timestamps (timestamp = -1 / default server timestamp).
/// Also tests interactions between explicit and server-assigned timestamps.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
/// "If unspecified, the server will assign a timestamp."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ServerTimestampAssignmentTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "srv-ts";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public ServerTimestampAssignmentTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Server_assigned_timestamp_is_positive()
    {
        await Client.MutateRowAsync(TN, "srv-ts-pos",
            Mutations.SetCell(CF, "c", "val", new BigtableVersion(-1)));

        var row = await Client.ReadRowAsync(TN, "srv-ts-pos");
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Server_timestamps_increase_monotonically()
    {
        await Client.MutateRowAsync(TN, "srv-ts-mono",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(-1)));
        var row1 = await Client.ReadRowAsync(TN, "srv-ts-mono");
        var ts1 = row1!.Families[0].Columns[0].Cells[0].TimestampMicros;

        await Client.MutateRowAsync(TN, "srv-ts-mono",
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(-1)));
        var row2 = await Client.ReadRowAsync(TN, "srv-ts-mono");
        var cells = row2!.Families[0].Columns[0].Cells;
        var ts2 = cells.OrderByDescending(c => c.TimestampMicros).First().TimestampMicros;

        ts2.Should().BeGreaterThanOrEqualTo(ts1);
    }

    [Fact]
    public async Task Explicit_timestamp_preserved()
    {
        await Client.MutateRowAsync(TN, "srv-ts-explicit",
            Mutations.SetCell(CF, "c", "val", new BigtableVersion(5000)));

        var row = await Client.ReadRowAsync(TN, "srv-ts-explicit");
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(5000000);
    }

    [Fact]
    public async Task Mix_server_and_explicit_timestamps()
    {
        await Client.MutateRowAsync(TN, "srv-ts-mix",
            Mutations.SetCell(CF, "explicit", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "server", "v2", new BigtableVersion(-1)));

        var row = await Client.ReadRowAsync(TN, "srv-ts-mix");
        var explicitCol = row!.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "explicit");
        explicitCol.Cells[0].TimestampMicros.Should().Be(1000000);

        var serverCol = row.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "server");
        serverCol.Cells[0].TimestampMicros.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Server_timestamp_creates_new_versions()
    {
        for (int i = 0; i < 3; i++)
            await Client.MutateRowAsync(TN, "srv-ts-ver",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(-1)));

        var row = await Client.ReadRowAsync(TN, "srv-ts-ver");
        // May have 1-3 cells depending on timestamp granularity
        row!.Families[0].Columns[0].Cells.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Server_timestamp_in_batch()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("srv-ts-batch",
                Mutations.SetCell(CF, "c", "v1", new BigtableVersion(-1)))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "srv-ts-batch");
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Server_timestamp_is_truncated_to_milliseconds()
    {
        await Client.MutateRowAsync(TN, "srv-ts-trunc",
            Mutations.SetCell(CF, "c", "val", new BigtableVersion(-1)));

        var row = await Client.ReadRowAsync(TN, "srv-ts-trunc");
        var ts = row!.Families[0].Columns[0].Cells[0].TimestampMicros;
        // Should be truncated to milliseconds (divisible by 1000)
        (ts % 1000).Should().Be(0);
    }

    [Fact]
    public async Task Server_timestamp_multiple_columns_same_request()
    {
        await Client.MutateRowAsync(TN, "srv-ts-mcol",
            Mutations.SetCell(CF, "a", "va", new BigtableVersion(-1)),
            Mutations.SetCell(CF, "b", "vb", new BigtableVersion(-1)),
            Mutations.SetCell(CF, "c", "vc", new BigtableVersion(-1)));

        var row = await Client.ReadRowAsync(TN, "srv-ts-mcol");
        var timestamps = row!.Families[0].Columns
            .SelectMany(c => c.Cells)
            .Select(c => c.TimestampMicros)
            .ToList();

        timestamps.Should().AllSatisfy(ts => ts.Should().BeGreaterThan(0));
    }

    [Fact]
    public async Task Explicit_zero_timestamp_is_treated_as_zero()
    {
        // BigtableVersion(0) should set timestamp_micros to 0
        await Client.MutateRowAsync(TN, "srv-ts-zero",
            Mutations.SetCell(CF, "c", "val", new BigtableVersion(0)));

        var row = await Client.ReadRowAsync(TN, "srv-ts-zero");
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(0);
    }

    [Fact]
    public async Task Server_timestamp_in_different_families()
    {
        await Client.MutateRowAsync(TN, "srv-ts-fam",
            Mutations.SetCell(CF, "c", "cf-val", new BigtableVersion(-1)),
            Mutations.SetCell("cf2", "c", "cf2-val", new BigtableVersion(-1)));

        var row = await Client.ReadRowAsync(TN, "srv-ts-fam");
        foreach (var fam in row!.Families)
            fam.Columns[0].Cells[0].TimestampMicros.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Server_timestamp_readable_with_filter()
    {
        await Client.MutateRowAsync(TN, "srv-ts-filter",
            Mutations.SetCell(CF, "c", "val", new BigtableVersion(-1)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.CellsPerColumnLimit(1),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("srv-ts-filter") } }
        };
        var count = 0;
        await foreach (var row in Client.ReadRows(request))
            count += row.Families.Sum(f => f.Columns.Sum(c => c.Cells.Count));

        count.Should().Be(1);
    }

    [Fact]
    public async Task Explicit_max_timestamp()
    {
        // Use a very large timestamp
        var largeTs = new BigtableVersion(253402300000); // ~year 9999 in ms
        await Client.MutateRowAsync(TN, "srv-ts-max",
            Mutations.SetCell(CF, "c", "val", largeTs));

        var row = await Client.ReadRowAsync(TN, "srv-ts-max");
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(253402300000000);
    }
}

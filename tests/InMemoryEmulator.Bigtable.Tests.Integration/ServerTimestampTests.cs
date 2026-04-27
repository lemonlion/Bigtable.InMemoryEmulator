using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for MutateRow with server-assigned timestamps.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
///   "timestamp_micros: The timestamp of the cell into which new data should be written.
///    Use -1 for current Bigtable server time."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ServerTimestampTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "server-ts";

    public ServerTimestampTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task Server_timestamp_positive()
    {
        await Client.MutateRowAsync(TN, "st-r1",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(-1)));
        var row = await Client.ReadRowAsync(TN, "st-r1");
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Server_timestamp_multiple_of_1000()
    {
        await Client.MutateRowAsync(TN, "st-r2",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(-1)));
        var row = await Client.ReadRowAsync(TN, "st-r2");
        var ts = row!.Families[0].Columns[0].Cells[0].TimestampMicros;
        (ts % 1000).Should().Be(0);
    }

    [Fact]
    public async Task Server_timestamps_nondecreasing()
    {
        for (int i = 0; i < 5; i++)
            await Client.MutateRowAsync(TN, $"st-r3-{i}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(-1)));
        var timestamps = new List<long>();
        for (int i = 0; i < 5; i++)
        {
            var row = await Client.ReadRowAsync(TN, $"st-r3-{i}");
            timestamps.Add(row!.Families[0].Columns[0].Cells[0].TimestampMicros);
        }
        for (int i = 1; i < timestamps.Count; i++)
            timestamps[i].Should().BeGreaterThanOrEqualTo(timestamps[i - 1]);
    }

    [Fact]
    public async Task Server_timestamp_different_columns()
    {
        await Client.MutateRowAsync(TN, "st-r4",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(-1)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(-1)));
        var row = await Client.ReadRowAsync(TN, "st-r4");
        row!.Families[0].Columns.Should().HaveCount(2);
        foreach (var col in row.Families[0].Columns)
            col.Cells[0].TimestampMicros.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Server_timestamp_in_batch()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("st-r5",
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(-1)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "st-r5");
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Mixed_server_and_explicit_timestamps()
    {
        await Client.MutateRowAsync(TN, "st-r6",
            Mutations.SetCell(CF, "a", "server", new BigtableVersion(-1)),
            Mutations.SetCell(CF, "b", "explicit", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "st-r6");
        var serverTs = row!.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "a")
            .Cells[0].TimestampMicros;
        var explicitTs = row!.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "b")
            .Cells[0].TimestampMicros;
        serverTs.Should().BeGreaterThan(1_000_000); // Much larger than 1000ms=1000000us
        explicitTs.Should().Be(1_000_000);
    }

    [Fact]
    public async Task Server_timestamp_in_readmodifywrite()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "st-r7",
            ReadModifyWriteRules.Append(CF, "c", "hello"));
        resp.Row.Families[0].Columns[0].Cells[0].TimestampMicros.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Server_timestamp_is_recent()
    {
        // Server timestamp should be close to current time
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000; // micros
        await Client.MutateRowAsync(TN, "st-r8",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(-1)));
        var after = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000) * 1000; // micros + 1s buffer
        var row = await Client.ReadRowAsync(TN, "st-r8");
        var ts = row!.Families[0].Columns[0].Cells[0].TimestampMicros;
        ts.Should().BeGreaterThanOrEqualTo(before);
        ts.Should().BeLessThanOrEqualTo(after);
    }
}

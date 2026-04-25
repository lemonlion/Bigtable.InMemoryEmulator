using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for concurrent ReadModifyWrite atomicity.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readmodifywriterowrequest
///   "ReadModifyWrite operations are atomic."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadModifyWriteAtomicityTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public ReadModifyWriteAtomicityTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync("rmw-atomic", new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("rmw-atomic");

    [Fact]
    public async Task Concurrent_increments_sum_correctly()
    {
        // Start with 0
        var initialValue = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(0L));
        await Client.MutateRowAsync(TN, "rmw-conc-inc",
            Mutations.SetCell(CF, "counter", initialValue, new BigtableVersion(1000)));

        const int concurrency = 50;
        var tasks = Enumerable.Range(0, concurrency)
            .Select(_ => Client.ReadModifyWriteRowAsync(TN, "rmw-conc-inc",
                ReadModifyWriteRules.Increment(CF, "counter", 1)))
            .ToArray();

        await Task.WhenAll(tasks);

        var row = await Client.ReadRowAsync(TN, "rmw-conc-inc");
        var val = System.Net.IPAddress.NetworkToHostOrder(
            BitConverter.ToInt64(row!.Families[0].Columns[0].Cells[0].Value.ToByteArray()));
        val.Should().Be(concurrency);
    }

    [Fact]
    public async Task Concurrent_appends_contain_all_data()
    {
        const int concurrency = 20;
        var tasks = Enumerable.Range(0, concurrency)
            .Select(i => Client.ReadModifyWriteRowAsync(TN, "rmw-conc-app",
                ReadModifyWriteRules.Append(CF, "log", $"[{i}]")))
            .ToArray();

        await Task.WhenAll(tasks);

        var row = await Client.ReadRowAsync(TN, "rmw-conc-app");
        var val = row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8();

        // All entries should be present
        for (int i = 0; i < concurrency; i++)
            val.Should().Contain($"[{i}]");
    }

    [Fact]
    public async Task RMW_response_contains_post_mutation_state()
    {
        var initialValue = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(10L));
        await Client.MutateRowAsync(TN, "rmw-response",
            Mutations.SetCell(CF, "counter", initialValue, new BigtableVersion(1000)));

        var response = await Client.ReadModifyWriteRowAsync(TN, "rmw-response",
            ReadModifyWriteRules.Increment(CF, "counter", 5));

        var val = System.Net.IPAddress.NetworkToHostOrder(
            BitConverter.ToInt64(response.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray()));
        val.Should().Be(15);
    }

    [Fact]
    public async Task RMW_increment_and_append_on_different_columns()
    {
        var initialValue = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(100L));
        await Client.MutateRowAsync(TN, "rmw-mixed",
            Mutations.SetCell(CF, "counter", initialValue, new BigtableVersion(1000)),
            Mutations.SetCell(CF, "log", "start", new BigtableVersion(1000)));

        var response = await Client.ReadModifyWriteRowAsync(TN, "rmw-mixed",
            ReadModifyWriteRules.Increment(CF, "counter", 50),
            ReadModifyWriteRules.Append(CF, "log", "-done"));

        var fam = response.Row.Families.First(f => f.Name == CF);
        var counter = fam.Columns.First(c => c.Qualifier.ToStringUtf8() == "counter");
        var log = fam.Columns.First(c => c.Qualifier.ToStringUtf8() == "log");

        System.Net.IPAddress.NetworkToHostOrder(
            BitConverter.ToInt64(counter.Cells[0].Value.ToByteArray())).Should().Be(150);
        log.Cells[0].Value.ToStringUtf8().Should().Be("start-done");
    }

    [Fact]
    public async Task Concurrent_increments_on_multiple_columns()
    {
        var zero = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(0L));
        await Client.MutateRowAsync(TN, "rmw-mc",
            Mutations.SetCell(CF, "a", zero, new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", zero, new BigtableVersion(1000)));

        const int concurrency = 30;
        var tasks = Enumerable.Range(0, concurrency)
            .Select(_ => Client.ReadModifyWriteRowAsync(TN, "rmw-mc",
                ReadModifyWriteRules.Increment(CF, "a", 1),
                ReadModifyWriteRules.Increment(CF, "b", 2)))
            .ToArray();

        await Task.WhenAll(tasks);

        var row = await Client.ReadRowAsync(TN, "rmw-mc");
        var fam = row!.Families.First(f => f.Name == CF);
        var a = System.Net.IPAddress.NetworkToHostOrder(BitConverter.ToInt64(
            fam.Columns.First(c => c.Qualifier.ToStringUtf8() == "a").Cells[0].Value.ToByteArray()));
        var b = System.Net.IPAddress.NetworkToHostOrder(BitConverter.ToInt64(
            fam.Columns.First(c => c.Qualifier.ToStringUtf8() == "b").Cells[0].Value.ToByteArray()));

        a.Should().Be(concurrency);
        b.Should().Be(concurrency * 2);
    }

    [Fact]
    public async Task Sequential_increments_are_cumulative()
    {
        for (int i = 0; i < 10; i++)
        {
            await Client.ReadModifyWriteRowAsync(TN, "rmw-seq",
                ReadModifyWriteRules.Increment(CF, "counter", 1));
        }

        var row = await Client.ReadRowAsync(TN, "rmw-seq");
        var val = System.Net.IPAddress.NetworkToHostOrder(
            BitConverter.ToInt64(row!.Families[0].Columns[0].Cells[0].Value.ToByteArray()));
        val.Should().Be(10);
    }

    [Fact]
    public async Task RMW_with_nonexistent_family_throws()
    {
        var act = () => Client.ReadModifyWriteRowAsync(TN, "rmw-nofam",
            ReadModifyWriteRules.Increment("nosuchfamily", "counter", 1));
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }
}

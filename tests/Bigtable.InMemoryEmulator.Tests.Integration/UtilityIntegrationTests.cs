using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class UtilityIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "utility-tests";
    private const string Family = "cf";

    public UtilityIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { Family });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private BigtableServiceApiClient ApiClient => _fixture.ServiceApiClient;

    [Fact]
    public async Task PingAndWarm_succeeds()
    {
        var act = async () => await ApiClient.PingAndWarmAsync(new PingAndWarmRequest
        {
            Name = _fixture.InstanceName,
        });
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SampleRowKeys_returns_samples()
    {
        for (int i = 0; i < 5; i++)
            await Client.MutateRowAsync(TN, new BigtableByteString($"samplekey-{i:D3}"),
                Mutations.SetCell(Family, "col", "val", new BigtableVersion(1000)));

        var stream = ApiClient.SampleRowKeys(new SampleRowKeysRequest
        {
            TableNameAsTableName = TN,
        });
        var samples = new List<SampleRowKeysResponse>();
        var e = stream.GetResponseStream().GetAsyncEnumerator(default);
        while (await e.MoveNextAsync()) samples.Add(e.Current);
        samples.Should().NotBeEmpty();
    }

    [Fact]
    public async Task MutateRow_succeeds_and_data_is_readable()
    {
        // Verifies the trailing metadata doesn't break the basic write/read cycle
        await Client.MutateRowAsync(TN, new BigtableByteString("trailer-row"),
            Mutations.SetCell(Family, "col", "trailer-val", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, new BigtableByteString("trailer-row"));
        row.Should().NotBeNull();
    }
}

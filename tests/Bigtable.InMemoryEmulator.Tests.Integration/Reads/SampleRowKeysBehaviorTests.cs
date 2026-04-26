using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class SampleRowKeysBehaviorTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "srk-beh";
    private const string CF = "cf";

    public SampleRowKeysBehaviorTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        for (int i = 0; i < 50; i++)
            await Client.MutateRowAsync(TN, $"row-{i:D3}", Mutations.SetCell(CF, "v", $"{i}"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Returns_at_least_one_sample()
    {
        var response = _fixture.ServiceApiClient.SampleRowKeys(
            new SampleRowKeysRequest { TableName = TN.ToString() });
        var samples = new List<SampleRowKeysResponse>();
        var stream = response.GetResponseStream();
        while (await stream.MoveNextAsync())
            samples.Add(stream.Current);
        samples.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Last_sample_has_empty_key()
    {
        var response = _fixture.ServiceApiClient.SampleRowKeys(
            new SampleRowKeysRequest { TableName = TN.ToString() });
        var samples = new List<SampleRowKeysResponse>();
        var stream = response.GetResponseStream();
        while (await stream.MoveNextAsync())
            samples.Add(stream.Current);
        samples.Last().RowKey.Should().BeEmpty();
    }

    [Fact]
    public async Task Offset_non_negative()
    {
        var response = _fixture.ServiceApiClient.SampleRowKeys(
            new SampleRowKeysRequest { TableName = TN.ToString() });
        var samples = new List<SampleRowKeysResponse>();
        var stream = response.GetResponseStream();
        while (await stream.MoveNextAsync())
            samples.Add(stream.Current);
        foreach (var s in samples)
            s.OffsetBytes.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Row_keys_are_sorted()
    {
        var response = _fixture.ServiceApiClient.SampleRowKeys(
            new SampleRowKeysRequest { TableName = TN.ToString() });
        var samples = new List<SampleRowKeysResponse>();
        var stream = response.GetResponseStream();
        while (await stream.MoveNextAsync())
            samples.Add(stream.Current);
        var keys = samples.Where(s => !s.RowKey.IsEmpty).Select(s => s.RowKey.ToStringUtf8()).ToList();
        keys.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Empty_table_sample()
    {
        await _fixture.CreateTableAsync("srk-empty", new[] { CF });
        var tn = _fixture.GetTableName("srk-empty");
        var response = _fixture.ServiceApiClient.SampleRowKeys(
            new SampleRowKeysRequest { TableName = tn.ToString() });
        var samples = new List<SampleRowKeysResponse>();
        var stream = response.GetResponseStream();
        while (await stream.MoveNextAsync())
            samples.Add(stream.Current);
        samples.Should().HaveCountLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task Multiple_calls_consistent()
    {
        var getCount = async () =>
        {
            var response = _fixture.ServiceApiClient.SampleRowKeys(
                new SampleRowKeysRequest { TableName = TN.ToString() });
            var samples = new List<SampleRowKeysResponse>();
            var stream = response.GetResponseStream();
            while (await stream.MoveNextAsync())
                samples.Add(stream.Current);
            return samples.Count;
        };
        var count1 = await getCount();
        var count2 = await getCount();
        count1.Should().Be(count2);
    }

    [Fact]
    public async Task Offsets_non_decreasing()
    {
        var response = _fixture.ServiceApiClient.SampleRowKeys(
            new SampleRowKeysRequest { TableName = TN.ToString() });
        var samples = new List<SampleRowKeysResponse>();
        var stream = response.GetResponseStream();
        while (await stream.MoveNextAsync())
            samples.Add(stream.Current);
        var offsets = samples.Select(s => s.OffsetBytes).ToList();
        for (int i = 1; i < offsets.Count; i++)
            offsets[i].Should().BeGreaterThanOrEqualTo(offsets[i - 1]);
    }
}

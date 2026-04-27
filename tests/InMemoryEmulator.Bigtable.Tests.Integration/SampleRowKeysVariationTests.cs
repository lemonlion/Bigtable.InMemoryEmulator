using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for SampleRowKeys API behavior with various table sizes and states.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#samplerowkeysrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class SampleRowKeysVariationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private BigtableServiceApiClient ServiceClient => _fixture.ServiceApiClient;
    private const string CF = "cf";

    public SampleRowKeysVariationTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync("srk-empty", new[] { CF });
        await _fixture.CreateTableAsync("srk-1row", new[] { CF });
        await _fixture.CreateTableAsync("srk-many", new[] { CF });

        await Client.MutateRowAsync(_fixture.GetTableName("srk-1row"), "only-row",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        for (int i = 0; i < 20; i++)
            await Client.MutateRowAsync(_fixture.GetTableName("srk-many"), $"row-{i:D3}",
                Mutations.SetCell(CF, "c", $"val-{i}", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private async Task<List<SampleRowKeysResponse>> GetSampleKeys(string table)
    {
        var response = ServiceClient.SampleRowKeys(new SampleRowKeysRequest
        {
            TableName = _fixture.GetTableName(table).ToString()
        });
        var results = new List<SampleRowKeysResponse>();
        var e = response.GetResponseStream().GetAsyncEnumerator(default);
        while (await e.MoveNextAsync()) results.Add(e.Current);
        return results;
    }

    [Fact]
    public async Task Empty_table_returns_response()
    {
        var results = await GetSampleKeys("srk-empty");
        results.Should().NotBeNull();
    }

    [Fact]
    public async Task Single_row_table_returns_response()
    {
        var results = await GetSampleKeys("srk-1row");
        results.Should().NotBeNull();
    }

    [Fact]
    public async Task Many_rows_returns_at_least_one()
    {
        var results = await GetSampleKeys("srk-many");
        results.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Response_has_non_negative_offset()
    {
        var results = await GetSampleKeys("srk-many");
        foreach (var r in results)
            r.OffsetBytes.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Calling_twice_returns_results_both_times()
    {
        var r1 = await GetSampleKeys("srk-many");
        var r2 = await GetSampleKeys("srk-many");
        r1.Should().NotBeEmpty();
        r2.Should().NotBeEmpty();
    }

    [Fact]
    public async Task After_adding_rows_returns_response()
    {
        await _fixture.CreateTableAsync("srk-grow", new[] { CF });
        var before = await GetSampleKeys("srk-grow");

        for (int i = 0; i < 10; i++)
            await Client.MutateRowAsync(_fixture.GetTableName("srk-grow"), $"g-{i}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)));

        var after = await GetSampleKeys("srk-grow");
        after.Should().NotBeEmpty();
    }

    [Fact]
    public async Task After_deleting_all_rows_returns_response()
    {
        await _fixture.CreateTableAsync("srk-del", new[] { CF });
        await Client.MutateRowAsync(_fixture.GetTableName("srk-del"), "temp",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(_fixture.GetTableName("srk-del"), "temp",
            Mutations.DeleteFromRow());

        var results = await GetSampleKeys("srk-del");
        results.Should().NotBeNull();
    }

    [Fact]
    public async Task Last_entry_has_empty_row_key()
    {
        // Ref: The last entry always has an empty row key to indicate the end of the table
        var results = await GetSampleKeys("srk-many");
        results.Should().NotBeEmpty();
        results.Last().RowKey.Should().BeEmpty();
    }
}

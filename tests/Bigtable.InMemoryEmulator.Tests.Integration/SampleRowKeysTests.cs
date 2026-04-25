using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for SampleRowKeys RPC.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#samplerowkeysresponse
///   "Returns a stream of approximate split points for the table's data."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class SampleRowKeysTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public SampleRowKeysTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync("sample-rk", new[] { CF });
        var tn = _fixture.GetTableName("sample-rk");
        // Seed some rows
        var entries = Enumerable.Range(0, 100).Select(i =>
            Mutations.CreateEntry($"srk-{i:D4}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))).ToArray();
        await _fixture.Client.MutateRowsAsync(tn, entries);
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private TableName TN => _fixture.GetTableName("sample-rk");

    [Fact]
    public async Task SampleRowKeys_returns_at_least_one_entry()
    {
        var responses = new List<SampleRowKeysResponse>();
        var stream = _fixture.ServiceApiClient.SampleRowKeys(TN);
        await foreach (var response in stream.GetResponseStream())
            responses.Add(response);

        // Should have at least one response (the final empty-key entry)
        responses.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SampleRowKeys_last_entry_is_empty_key()
    {
        // The last entry has an empty row key indicating the end
        var responses = new List<SampleRowKeysResponse>();
        var stream = _fixture.ServiceApiClient.SampleRowKeys(TN);
        await foreach (var response in stream.GetResponseStream())
            responses.Add(response);

        responses.Last().RowKey.Should().BeEmpty();
    }

    [Fact]
    public async Task SampleRowKeys_offset_bytes_non_negative()
    {
        var responses = new List<SampleRowKeysResponse>();
        var stream = _fixture.ServiceApiClient.SampleRowKeys(TN);
        await foreach (var response in stream.GetResponseStream())
            responses.Add(response);

        foreach (var response in responses)
            response.OffsetBytes.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task SampleRowKeys_on_empty_table()
    {
        await _fixture.CreateTableAsync("sample-rk-empty", new[] { CF });
        var emptyTn = _fixture.GetTableName("sample-rk-empty");

        var responses = new List<SampleRowKeysResponse>();
        var stream = _fixture.ServiceApiClient.SampleRowKeys(emptyTn);
        await foreach (var response in stream.GetResponseStream())
            responses.Add(response);

        // Even empty table should return at least one entry (final empty key)
        responses.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SampleRowKeys_nonexistent_table_throws()
    {
        var fakeTn = _fixture.GetTableName("sample-rk-no-such");
        var act = async () =>
        {
            var stream = _fixture.ServiceApiClient.SampleRowKeys(fakeTn);
            await foreach (var _ in stream.GetResponseStream()) { }
        };
        await act.Should().ThrowAsync<RpcException>();
    }

    [Fact]
    public async Task SampleRowKeys_offsets_are_monotonically_increasing()
    {
        var responses = new List<SampleRowKeysResponse>();
        var stream = _fixture.ServiceApiClient.SampleRowKeys(TN);
        await foreach (var response in stream.GetResponseStream())
            responses.Add(response);

        for (int i = 1; i < responses.Count; i++)
            responses[i].OffsetBytes.Should().BeGreaterThanOrEqualTo(responses[i - 1].OffsetBytes);
    }
}

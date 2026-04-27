using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for SampleRowKeys and PingAndWarm operations.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#samplerowkeysrequest
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#pingandwarmrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class SampleRowKeysAndPingTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public SampleRowKeysAndPingTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync("srk-test", new[] { CF });
        // Seed 100 rows
        for (int batch = 0; batch < 10; batch++)
        {
            var entries = Enumerable.Range(batch * 10, 10).Select(i =>
                Mutations.CreateEntry($"srk-{i:D6}",
                    Mutations.SetCell(CF, "val", $"data-{i}", new BigtableVersion(1000)))).ToArray();
            await _fixture.Client.MutateRowsAsync(TN, entries);
        }
    }
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private BigtableServiceApiClient ServiceClient => _fixture.ServiceApiClient;
    private TableName TN => _fixture.GetTableName("srk-test");

    #region SampleRowKeys

    [Fact]
    public async Task SampleRowKeys_returns_at_least_one_sample()
    {
        var request = new SampleRowKeysRequest { TableName = TN.ToString() };
        var stream = ServiceClient.SampleRowKeys(request);
        var samples = new List<SampleRowKeysResponse>();
        var responseStream = stream.GetResponseStream();
        while (await responseStream.MoveNextAsync())
            samples.Add(responseStream.Current);
        samples.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SampleRowKeys_last_entry_has_empty_key()
    {
        // The last sample in the response always has an empty row key and represents the end
        var request = new SampleRowKeysRequest { TableName = TN.ToString() };
        var stream = ServiceClient.SampleRowKeys(request);
        var samples = new List<SampleRowKeysResponse>();
        var responseStream = stream.GetResponseStream();
        while (await responseStream.MoveNextAsync())
            samples.Add(responseStream.Current);
        samples.Should().NotBeEmpty();
        samples.Last().RowKey.Should().BeEmpty();
    }

    [Fact]
    public async Task SampleRowKeys_offset_bytes_are_nondecreasing()
    {
        var request = new SampleRowKeysRequest { TableName = TN.ToString() };
        var stream = ServiceClient.SampleRowKeys(request);
        var samples = new List<SampleRowKeysResponse>();
        var responseStream = stream.GetResponseStream();
        while (await responseStream.MoveNextAsync())
            samples.Add(responseStream.Current);

        for (int i = 1; i < samples.Count; i++)
            samples[i].OffsetBytes.Should().BeGreaterThanOrEqualTo(samples[i - 1].OffsetBytes);
    }

    [Fact]
    public async Task SampleRowKeys_keys_are_valid_row_keys()
    {
        var request = new SampleRowKeysRequest { TableName = TN.ToString() };
        var stream = ServiceClient.SampleRowKeys(request);
        var samples = new List<SampleRowKeysResponse>();
        var responseStream = stream.GetResponseStream();
        while (await responseStream.MoveNextAsync())
            samples.Add(responseStream.Current);

        // All non-empty keys should be readable as row keys
        foreach (var s in samples.Where(s => !s.RowKey.IsEmpty))
        {
            var key = s.RowKey.ToStringUtf8();
            key.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task SampleRowKeys_empty_table_returns_end_marker()
    {
        // Create a separate empty table
        await _fixture.CreateTableAsync("srk-empty", new[] { CF });
        var emptyTN = _fixture.GetTableName("srk-empty");
        var request = new SampleRowKeysRequest { TableName = emptyTN.ToString() };
        var stream = ServiceClient.SampleRowKeys(request);
        var samples = new List<SampleRowKeysResponse>();
        var responseStream = stream.GetResponseStream();
        while (await responseStream.MoveNextAsync())
            samples.Add(responseStream.Current);
        // Even empty table should return the end marker
        samples.Should().NotBeEmpty();
        samples.Last().RowKey.Should().BeEmpty();
    }

    #endregion

    #region SampleRowKeys with nonexistent table

    [Fact]
    public async Task SampleRowKeys_nonexistent_table_throws()
    {
        var badTN = _fixture.GetTableName("nonexistent-srk-table");
        var request = new SampleRowKeysRequest { TableName = badTN.ToString() };
        var act = async () =>
        {
            var stream = ServiceClient.SampleRowKeys(request);
            var responseStream = stream.GetResponseStream();
            while (await responseStream.MoveNextAsync()) { }
        };
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    #endregion

    #region ReadRows after SampleRowKeys

    [Fact]
    public async Task SampleRowKeys_can_be_used_for_parallel_scan()
    {
        // Get sample keys
        var request = new SampleRowKeysRequest { TableName = TN.ToString() };
        var stream = ServiceClient.SampleRowKeys(request);
        var samples = new List<SampleRowKeysResponse>();
        var responseStream = stream.GetResponseStream();
        while (await responseStream.MoveNextAsync())
            samples.Add(responseStream.Current);

        // Use sample keys to define scan ranges
        var allRows = new List<Row>();
        for (int i = 0; i < samples.Count; i++)
        {
            var rowSet = new RowSet();
            var startKey = i == 0 ? "" : samples[i - 1].RowKey.ToStringUtf8();
            var endKey = samples[i].RowKey.IsEmpty ? "" : samples[i].RowKey.ToStringUtf8();

            if (string.IsNullOrEmpty(startKey) && string.IsNullOrEmpty(endKey))
            {
                // Full scan for last segment (or only segment)
                await foreach (var row in Client.ReadRows(TN))
                    allRows.Add(row);
            }
            else if (string.IsNullOrEmpty(endKey))
            {
                // Open-ended range
                rowSet.RowRanges.Add(new RowRange
                {
                    StartKeyClosed = ByteString.CopyFromUtf8(startKey)
                });
                await foreach (var row in Client.ReadRows(TN, rowSet))
                    allRows.Add(row);
            }
            else if (string.IsNullOrEmpty(startKey))
            {
                rowSet.RowRanges.Add(new RowRange
                {
                    EndKeyOpen = ByteString.CopyFromUtf8(endKey)
                });
                await foreach (var row in Client.ReadRows(TN, rowSet))
                    allRows.Add(row);
            }
            else
            {
                rowSet.RowRanges.Add(RowRange.ClosedOpen(startKey, endKey));
                await foreach (var row in Client.ReadRows(TN, rowSet))
                    allRows.Add(row);
            }
        }
        // We should get all 100 rows
        allRows.Should().HaveCountGreaterThanOrEqualTo(100);
    }

    #endregion
}

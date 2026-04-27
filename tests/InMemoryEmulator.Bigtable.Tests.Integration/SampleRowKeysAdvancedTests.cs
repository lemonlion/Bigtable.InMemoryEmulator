using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for SampleRowKeys RPC: response format, distribution, and edge cases.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#samplerowkeysrequest
///   "Returns a sample of row keys in the table. The returned row keys will delimit contiguous
///    sections of the table of approximately equal size."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class SampleRowKeysAdvancedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "sample-adv";

    public SampleRowKeysAdvancedTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        var tn = TN;
        var entries = Enumerable.Range(0, 200).Select(i =>
            Mutations.CreateEntry($"sk-{i:D4}",
                Mutations.SetCell(CF, "c", new string('X', 100), new BigtableVersion(1000)))).ToArray();
        // Write in batches of 100
        await _fixture.Client.MutateRowsAsync(tn, entries.Take(100).ToArray());
        await _fixture.Client.MutateRowsAsync(tn, entries.Skip(100).ToArray());
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private BigtableServiceApiClient Api => _fixture.ServiceApiClient;
    private TableName TN => _fixture.GetTableName(Table);

    #region Basic sample

    [Fact]
    public async Task SampleRowKeys_returns_responses()
    {
        var samples = new List<SampleRowKeysResponse>();
        var stream = Api.SampleRowKeys(new SampleRowKeysRequest { TableNameAsTableName = TN });
        await foreach (var resp in stream.GetResponseStream())
            samples.Add(resp);
        samples.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SampleRowKeys_last_entry_has_empty_key()
    {
        // Ref: The last sample row key will be the empty string (""),
        // indicating the end of the table
        var samples = new List<SampleRowKeysResponse>();
        var stream = Api.SampleRowKeys(new SampleRowKeysRequest { TableNameAsTableName = TN });
        await foreach (var resp in stream.GetResponseStream())
            samples.Add(resp);
        samples.Last().RowKey.Should().BeEmpty();
    }

    [Fact]
    public async Task SampleRowKeys_offset_bytes_nonnegative()
    {
        var stream = Api.SampleRowKeys(new SampleRowKeysRequest { TableNameAsTableName = TN });
        await foreach (var resp in stream.GetResponseStream())
            resp.OffsetBytes.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task SampleRowKeys_offset_bytes_monotonically_increasing()
    {
        var offsets = new List<long>();
        var stream = Api.SampleRowKeys(new SampleRowKeysRequest { TableNameAsTableName = TN });
        await foreach (var resp in stream.GetResponseStream())
            offsets.Add(resp.OffsetBytes);
        for (int i = 1; i < offsets.Count; i++)
            offsets[i].Should().BeGreaterThanOrEqualTo(offsets[i - 1]);
    }

    #endregion

    #region Empty table

    [Fact]
    public async Task SampleRowKeys_empty_table()
    {
        var emptyTable = $"sample-empty-{Guid.NewGuid():N}".Substring(0, 28);
        await _fixture.CreateTableAsync(emptyTable, new[] { CF });
        var tn = _fixture.GetTableName(emptyTable);
        var samples = new List<SampleRowKeysResponse>();
        var stream = Api.SampleRowKeys(new SampleRowKeysRequest { TableNameAsTableName = tn });
        await foreach (var resp in stream.GetResponseStream())
            samples.Add(resp);
        // Even empty table should return at least the sentinel empty key
        samples.Should().NotBeEmpty();
    }

    #endregion

    #region Streaming convenience

    [Fact]
    public async Task SampleRowKeys_via_stream_returns_keys()
    {
        var samples = new List<SampleRowKeysResponse>();
        var stream = Api.SampleRowKeys(new SampleRowKeysRequest { TableNameAsTableName = TN });
        await foreach (var resp in stream.GetResponseStream())
            samples.Add(resp);
        samples.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SampleRowKeys_keys_are_valid()
    {
        var samples = new List<SampleRowKeysResponse>();
        var stream = Api.SampleRowKeys(new SampleRowKeysRequest { TableNameAsTableName = TN });
        await foreach (var resp in stream.GetResponseStream())
            samples.Add(resp);
        // Most sample keys should be valid row keys from the table
        // (except the last empty one)
        var nonEmpty = samples.Where(s => s.RowKey.Length > 0).ToList();
        foreach (var sample in nonEmpty)
        {
            var key = sample.RowKey.ToStringUtf8();
            key.Should().StartWith("sk-");
        }
    }

    #endregion

    #region Nonexistent table

    [Fact]
    public async Task SampleRowKeys_nonexistent_table_throws()
    {
        var act = async () =>
        {
            var stream = Api.SampleRowKeys(new SampleRowKeysRequest
            {
                TableNameAsTableName = _fixture.GetTableName("nonexistent-table-srk")
            });
            await foreach (var _ in stream.GetResponseStream()) { }
        };
        await act.Should().ThrowAsync<Grpc.Core.RpcException>()
            .Where(e => e.StatusCode == Grpc.Core.StatusCode.NotFound);
    }

    #endregion
}

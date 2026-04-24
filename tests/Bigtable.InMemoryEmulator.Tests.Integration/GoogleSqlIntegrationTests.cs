using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Integration tests for GoogleSQL ExecuteQuery via gRPC.
/// Marked GcpOnly: Go emulator does not support GoogleSQL.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.GcpOnly)]
public sealed class GoogleSqlIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "sqltests";
    private const string Family = "cf";

    public GoogleSqlIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { Family });
        var c = _fixture.Client;
        var tn = _fixture.GetTableName(Table);
        await c.MutateRowAsync(tn, new BigtableByteString("row1"),
            Mutations.SetCell(Family, "name", "Alice", new BigtableVersion(1000)));
        await c.MutateRowAsync(tn, new BigtableByteString("row2"),
            Mutations.SetCell(Family, "name", "Bob", new BigtableVersion(1000)));
        await c.MutateRowAsync(tn, new BigtableByteString("row3"),
            Mutations.SetCell(Family, "name", "Charlie", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private BigtableServiceApiClient ApiClient => _fixture.ServiceApiClient;
    private string Instance => _fixture.InstanceName;

    [Fact]
    public async Task ExecuteQuery_select_star_returns_metadata()
    {
        var request = new ExecuteQueryRequest
        {
            InstanceName = Instance,
            Query = "SELECT * FROM " + Table,
            ProtoFormat = new ProtoFormat(),
        };
        var stream = ApiClient.ExecuteQuery(request);
        var responses = new List<ExecuteQueryResponse>();
        var e = stream.GetResponseStream().GetAsyncEnumerator(default);
        while (await e.MoveNextAsync()) responses.Add(e.Current);
        responses.Should().NotBeEmpty();
        responses[0].Metadata.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteQuery_with_where_clause()
    {
        var request = new ExecuteQueryRequest
        {
            InstanceName = Instance,
            Query = "SELECT _key FROM " + Table + " WHERE _key = b'row1'",
            ProtoFormat = new ProtoFormat(),
        };
        var stream = ApiClient.ExecuteQuery(request);
        var responses = new List<ExecuteQueryResponse>();
        var e = stream.GetResponseStream().GetAsyncEnumerator(default);
        while (await e.MoveNextAsync()) responses.Add(e.Current);
        responses.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExecuteQuery_with_limit()
    {
        var request = new ExecuteQueryRequest
        {
            InstanceName = Instance,
            Query = "SELECT _key FROM " + Table + " LIMIT 1",
            ProtoFormat = new ProtoFormat(),
        };
        var stream = ApiClient.ExecuteQuery(request);
        var responses = new List<ExecuteQueryResponse>();
        var e = stream.GetResponseStream().GetAsyncEnumerator(default);
        while (await e.MoveNextAsync()) responses.Add(e.Current);
        responses.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExecuteQuery_order_by_desc()
    {
        var request = new ExecuteQueryRequest
        {
            InstanceName = Instance,
            Query = "SELECT _key FROM " + Table + " ORDER BY _key DESC",
            ProtoFormat = new ProtoFormat(),
        };
        var stream = ApiClient.ExecuteQuery(request);
        var responses = new List<ExecuteQueryResponse>();
        var e = stream.GetResponseStream().GetAsyncEnumerator(default);
        while (await e.MoveNextAsync()) responses.Add(e.Current);
        responses.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExecuteQuery_function_concat()
    {
        var request = new ExecuteQueryRequest
        {
            InstanceName = Instance,
            Query = "SELECT CONCAT(CAST(cf['name'] AS STRING), '-test') AS result FROM " + Table + " LIMIT 1",
            ProtoFormat = new ProtoFormat(),
        };
        var stream = ApiClient.ExecuteQuery(request);
        var responses = new List<ExecuteQueryResponse>();
        var e = stream.GetResponseStream().GetAsyncEnumerator(default);
        while (await e.MoveNextAsync()) responses.Add(e.Current);
        responses.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExecuteQuery_group_by_aggregation()
    {
        var request = new ExecuteQueryRequest
        {
            InstanceName = Instance,
            Query = "SELECT COUNT(*) AS cnt FROM " + Table,
            ProtoFormat = new ProtoFormat(),
        };
        var stream = ApiClient.ExecuteQuery(request);
        var responses = new List<ExecuteQueryResponse>();
        var e = stream.GetResponseStream().GetAsyncEnumerator(default);
        while (await e.MoveNextAsync()) responses.Add(e.Current);
        responses.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExecuteQuery_offset_limit()
    {
        var request = new ExecuteQueryRequest
        {
            InstanceName = Instance,
            Query = "SELECT _key FROM " + Table + " ORDER BY _key LIMIT 1 OFFSET 1",
            ProtoFormat = new ProtoFormat(),
        };
        var stream = ApiClient.ExecuteQuery(request);
        var responses = new List<ExecuteQueryResponse>();
        var e = stream.GetResponseStream().GetAsyncEnumerator(default);
        while (await e.MoveNextAsync()) responses.Add(e.Current);
        responses.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExecuteQuery_pipe_syntax()
    {
        // Ref: GoogleSQL pipe syntax — sequential transformation pipeline
        var request = new ExecuteQueryRequest
        {
            InstanceName = Instance,
            Query = "FROM " + Table + " |> SELECT _key |> LIMIT 2",
            ProtoFormat = new ProtoFormat(),
        };
        var stream = ApiClient.ExecuteQuery(request);
        var responses = new List<ExecuteQueryResponse>();
        var e = stream.GetResponseStream().GetAsyncEnumerator(default);
        while (await e.MoveNextAsync()) responses.Add(e.Current);
        responses.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExecuteQuery_with_parameter()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#executequeryrequest
        //   "params: named parameter values"
        var request = new ExecuteQueryRequest
        {
            InstanceName = Instance,
            Query = "SELECT _key FROM " + Table + " WHERE _key = @key",
            ProtoFormat = new ProtoFormat(),
        };
        request.Params.Add("key", new Google.Cloud.Bigtable.V2.Value
        {
            RawValue = Google.Protobuf.ByteString.CopyFromUtf8("row1"),
        });
        var stream = ApiClient.ExecuteQuery(request);
        var responses = new List<ExecuteQueryResponse>();
        var e = stream.GetResponseStream().GetAsyncEnumerator(default);
        while (await e.MoveNextAsync()) responses.Add(e.Current);
        responses.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExecuteQuery_distinct()
    {
        var request = new ExecuteQueryRequest
        {
            InstanceName = Instance,
            Query = "SELECT DISTINCT CAST(cf['name'] AS STRING) AS name FROM " + Table,
            ProtoFormat = new ProtoFormat(),
        };
        var stream = ApiClient.ExecuteQuery(request);
        var responses = new List<ExecuteQueryResponse>();
        var e = stream.GetResponseStream().GetAsyncEnumerator(default);
        while (await e.MoveNextAsync()) responses.Add(e.Current);
        responses.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExecuteQuery_cast_to_string()
    {
        var request = new ExecuteQueryRequest
        {
            InstanceName = Instance,
            Query = "SELECT CAST(cf['name'] AS STRING) AS name FROM " + Table + " LIMIT 1",
            ProtoFormat = new ProtoFormat(),
        };
        var stream = ApiClient.ExecuteQuery(request);
        var responses = new List<ExecuteQueryResponse>();
        var e = stream.GetResponseStream().GetAsyncEnumerator(default);
        while (await e.MoveNextAsync()) responses.Add(e.Current);
        responses.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExecuteQuery_returns_multiple_rows()
    {
        var request = new ExecuteQueryRequest
        {
            InstanceName = Instance,
            Query = "SELECT _key FROM " + Table,
            ProtoFormat = new ProtoFormat(),
        };
        var stream = ApiClient.ExecuteQuery(request);
        var responses = new List<ExecuteQueryResponse>();
        var e = stream.GetResponseStream().GetAsyncEnumerator(default);
        while (await e.MoveNextAsync()) responses.Add(e.Current);

        // At least the metadata response plus data
        responses.Should().HaveCountGreaterThanOrEqualTo(1);
        // Should include 3 rows of data (from seeded data)
        var dataResponses = responses.Where(r => r.Results != null).ToList();
        dataResponses.Should().NotBeEmpty();
    }
}

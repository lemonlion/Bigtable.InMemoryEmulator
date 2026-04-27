using InMemoryEmulator.Bigtable;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests GoogleSQL parsing, execution, and the ExecuteQuery gRPC endpoint.
/// </summary>
public class GoogleSqlTests : IDisposable
{
    private readonly InMemoryBigtableServer _server;
    private readonly BigtableClient _client;
    private readonly TableName _tableName;
    private readonly BigtableServiceApiClient _serviceApiClient;
    private const string Table = "sql_test";
    private const string Family = "cf1";

    public GoogleSqlTests()
    {
        var store = new InMemoryBigtableStore();
        store.CreateTable(Table, [Family]);
        _server = InMemoryBigtableServer.Create(store);
        _client = _server.Client;
        _tableName = new TableName("test-project", "test-instance", Table);
        _serviceApiClient = new BigtableServiceApiClientBuilder
        {
            CallInvoker = _server.Channel.CreateCallInvoker()
        }.Build();
    }

    public void Dispose() => _server.Dispose();

    private async Task SeedRow(string rowKey, string qualifier, string value)
    {
        await _client.MutateRowAsync(_tableName, rowKey,
            Mutations.SetCell(Family, qualifier, value, new BigtableVersion(1000)));
    }

    private async Task SeedRow(string rowKey, string qualifier, long value)
    {
        var bytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        await _client.MutateRowAsync(_tableName, rowKey,
            Mutations.SetCell(Family, qualifier, ByteString.CopyFrom(bytes), new BigtableVersion(1000)));
    }

    private async Task<(ResultSetMetadata? Metadata, List<PartialResultSet> Results)> ExecuteQueryAsync(
        string sql, Dictionary<string, Google.Cloud.Bigtable.V2.Value>? parameters = null)
    {
        var request = new ExecuteQueryRequest
        {
            InstanceName = "projects/test-project/instances/test-instance",
            Query = sql,
            ProtoFormat = new ProtoFormat(),
        };
        if (parameters != null)
        {
            foreach (var (key, val) in parameters)
            {
                request.Params[key] = val;
            }
        }

        ResultSetMetadata? metadata = null;
        var results = new List<PartialResultSet>();

        var stream = _serviceApiClient.ExecuteQuery(request);
        var enumerator = stream.GetResponseStream().GetAsyncEnumerator(default);
        while (await enumerator.MoveNextAsync())
        {
            var response = enumerator.Current;
            if (response.Metadata != null)
                metadata = response.Metadata;
            if (response.Results != null)
                results.Add(response.Results);
        }

        return (metadata, results);
    }

    private static List<Google.Cloud.Bigtable.V2.Value> DecodeResults(
        ResultSetMetadata metadata, List<PartialResultSet> results)
    {
        var values = new List<Google.Cloud.Bigtable.V2.Value>();
        foreach (var partialResult in results)
        {
            if (partialResult.ProtoRowsBatch != null)
            {
                var protoRows = ProtoRows.Parser.ParseFrom(partialResult.ProtoRowsBatch.BatchData);
                values.AddRange(protoRows.Values);
            }
        }
        return values;
    }

    // ==================== Parser Tests ====================

    [Fact]
    public void Parser_parses_simple_select()
    {
        var query = GoogleSqlParser.ParseQuery("SELECT _key FROM mytable");
        query.FromTable.Should().Be("mytable");
        query.Columns.Should().HaveCount(1);
        query.Columns[0].Expression.Should().BeOfType<ColumnRefExpression>()
            .Which.Name.Should().Be("_key");
    }

    [Fact]
    public void Parser_parses_where_clause()
    {
        var query = GoogleSqlParser.ParseQuery(
            "SELECT _key FROM mytable WHERE _key = 'abc'");
        query.Where.Should().NotBeNull();
        query.Where.Should().BeOfType<BinaryExpression>();
    }

    [Fact]
    public void Parser_parses_cast_expression()
    {
        var query = GoogleSqlParser.ParseQuery(
            "SELECT CAST(cf1['col'] AS STRING) FROM mytable");
        query.Columns[0].Expression.Should().BeOfType<CastExpression>()
            .Which.TargetType.Should().Be(SqlType.String);
    }

    [Fact]
    public void Parser_parses_map_subscript()
    {
        var query = GoogleSqlParser.ParseQuery(
            "SELECT cf1['qualifier'] FROM mytable");
        query.Columns[0].Expression.Should().BeOfType<MapSubscriptExpression>();
    }

    [Fact]
    public void Parser_parses_limit_offset()
    {
        var query = GoogleSqlParser.ParseQuery(
            "SELECT _key FROM mytable LIMIT 10 OFFSET 5");
        query.Limit.Should().Be(10);
        query.Offset.Should().Be(5);
    }

    [Fact]
    public void Parser_parses_order_by()
    {
        var query = GoogleSqlParser.ParseQuery(
            "SELECT _key FROM mytable ORDER BY _key DESC");
        query.OrderBy.Should().HaveCount(1);
        query.OrderBy![0].Descending.Should().BeTrue();
    }

    [Fact]
    public void Parser_parses_group_by_with_having()
    {
        var query = GoogleSqlParser.ParseQuery(
            "SELECT cf1['type'] t, COUNT(*) cnt FROM mytable GROUP BY cf1['type'] HAVING COUNT(*) > 1");
        query.GroupBy.Should().HaveCount(1);
        query.Having.Should().NotBeNull();
    }

    [Fact]
    public void Parser_parses_distinct()
    {
        var query = GoogleSqlParser.ParseQuery("SELECT DISTINCT _key FROM mytable");
        query.Distinct.Should().BeTrue();
    }

    [Fact]
    public void Parser_parses_case_expression()
    {
        var query = GoogleSqlParser.ParseQuery(
            "SELECT CASE WHEN _key = 'a' THEN 1 ELSE 0 END FROM mytable");
        query.Columns[0].Expression.Should().BeOfType<CaseExpression>();
    }

    [Fact]
    public void Parser_parses_parameter_reference()
    {
        var query = GoogleSqlParser.ParseQuery(
            "SELECT _key FROM mytable WHERE _key = @rowKey");
        query.Where.Should().BeOfType<BinaryExpression>()
            .Which.Right.Should().BeOfType<ParameterRefExpression>()
            .Which.Name.Should().Be("rowKey");
    }

    // ==================== End-to-end ExecuteQuery Tests ====================

    [Fact]
    public async Task ExecuteQuery_returns_metadata_and_results()
    {
        await SeedRow("row1", "name", "Alice");
        await SeedRow("row2", "name", "Bob");

        var (metadata, results) = await ExecuteQueryAsync(
            $"SELECT _key FROM {Table}");

        metadata.Should().NotBeNull();
        metadata!.ProtoSchema.Columns.Should().HaveCount(1);
        metadata.ProtoSchema.Columns[0].Name.Should().Be("_key");
    }

    [Fact]
    public async Task ExecuteQuery_select_star_returns_all_columns()
    {
        await SeedRow("row1", "name", "Alice");

        var (metadata, results) = await ExecuteQueryAsync(
            $"SELECT * FROM {Table}");

        metadata.Should().NotBeNull();
        // Should include _key and cf1 columns
        metadata!.ProtoSchema.Columns.Count.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task ExecuteQuery_cast_bytes_to_string()
    {
        await SeedRow("row1", "name", "Alice");

        var (metadata, results) = await ExecuteQueryAsync(
            $"SELECT CAST({Family}['name'] AS STRING) name FROM {Table}");

        metadata.Should().NotBeNull();
        var values = DecodeResults(metadata!, results);
        values.Should().HaveCount(1);
        values[0].StringValue.Should().Be("Alice");
    }

    [Fact]
    public async Task ExecuteQuery_cast_bytes_to_int64()
    {
        await SeedRow("row1", "age", 42L);

        var (metadata, results) = await ExecuteQueryAsync(
            $"SELECT CAST({Family}['age'] AS INT64) age FROM {Table}");

        metadata.Should().NotBeNull();
        var values = DecodeResults(metadata!, results);
        values.Should().HaveCount(1);
        values[0].IntValue.Should().Be(42);
    }

    [Fact]
    public async Task ExecuteQuery_where_filter()
    {
        await SeedRow("row1", "name", "Alice");
        await SeedRow("row2", "name", "Bob");

        var (metadata, results) = await ExecuteQueryAsync(
            $"SELECT CAST({Family}['name'] AS STRING) name FROM {Table} WHERE CAST({Family}['name'] AS STRING) = 'Alice'");

        var values = DecodeResults(metadata!, results);
        values.Should().HaveCount(1);
        values[0].StringValue.Should().Be("Alice");
    }

    [Fact]
    public async Task ExecuteQuery_with_parameter()
    {
        await SeedRow("row1", "name", "Alice");
        await SeedRow("row2", "name", "Bob");

        var (metadata, results) = await ExecuteQueryAsync(
            $"SELECT CAST({Family}['name'] AS STRING) name FROM {Table} WHERE CAST({Family}['name'] AS STRING) = @target",
            new Dictionary<string, Google.Cloud.Bigtable.V2.Value>
            {
                ["target"] = new() { StringValue = "Bob" }
            });

        var values = DecodeResults(metadata!, results);
        values.Should().HaveCount(1);
        values[0].StringValue.Should().Be("Bob");
    }

    [Fact]
    public async Task ExecuteQuery_limit()
    {
        await SeedRow("row1", "name", "Alice");
        await SeedRow("row2", "name", "Bob");
        await SeedRow("row3", "name", "Charlie");

        var (metadata, results) = await ExecuteQueryAsync(
            $"SELECT _key FROM {Table} LIMIT 2");

        var values = DecodeResults(metadata!, results);
        values.Should().HaveCount(2);
    }

    [Fact]
    public async Task ExecuteQuery_order_by_desc()
    {
        await SeedRow("row1", "name", "Alice");
        await SeedRow("row2", "name", "Bob");

        var (metadata, results) = await ExecuteQueryAsync(
            $"SELECT CAST({Family}['name'] AS STRING) name FROM {Table} ORDER BY name DESC");

        var values = DecodeResults(metadata!, results);
        values.Should().HaveCount(2);
        values[0].StringValue.Should().Be("Bob");
        values[1].StringValue.Should().Be("Alice");
    }

    [Fact]
    public async Task ExecuteQuery_invalid_sql_returns_error()
    {
        var act = () => ExecuteQueryAsync("THIS IS NOT SQL");
        await act.Should().ThrowAsync<Grpc.Core.RpcException>()
            .Where(e => e.StatusCode == Grpc.Core.StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task ExecuteQuery_function_concat()
    {
        await SeedRow("row1", "first", "Alice");
        await SeedRow("row1", "last", "Smith");

        var (metadata, results) = await ExecuteQueryAsync(
            $"SELECT CONCAT(CAST({Family}['first'] AS STRING), ' ', CAST({Family}['last'] AS STRING)) fullname FROM {Table}");

        var values = DecodeResults(metadata!, results);
        values.Should().HaveCount(1);
        values[0].StringValue.Should().Be("Alice Smith");
    }

    [Fact]
    public async Task ExecuteQuery_no_results_returns_empty()
    {
        // Don't seed any data
        var (metadata, results) = await ExecuteQueryAsync(
            $"SELECT _key FROM {Table}");

        metadata.Should().NotBeNull();
        var values = DecodeResults(metadata!, results);
        values.Should().BeEmpty();
    }

    // ==================== Pipe Syntax Tests ====================

    [Fact]
    public void Parser_parses_from_pipe_where()
    {
        // Ref: GoogleSQL pipe syntax — FROM table |> WHERE condition
        var query = GoogleSqlParser.ParseQuery($"FROM {Table} |> WHERE _key = b'row1'");
        query.FromTable.Should().Be(Table);
        query.Where.Should().NotBeNull();
    }

    [Fact]
    public void Parser_parses_from_pipe_select()
    {
        // Ref: GoogleSQL pipe syntax — FROM table |> SELECT columns
        var query = GoogleSqlParser.ParseQuery($"FROM {Table} |> SELECT _key");
        query.FromTable.Should().Be(Table);
        query.Columns.Should().HaveCount(1);
        query.Columns[0].Expression.Should().BeOfType<ColumnRefExpression>()
            .Which.Name.Should().Be("_key");
    }

    [Fact]
    public void Parser_parses_from_pipe_order_by()
    {
        var query = GoogleSqlParser.ParseQuery($"FROM {Table} |> ORDER BY _key DESC");
        query.FromTable.Should().Be(Table);
        query.OrderBy.Should().HaveCount(1);
        query.OrderBy![0].Descending.Should().BeTrue();
    }

    [Fact]
    public void Parser_parses_from_pipe_limit()
    {
        var query = GoogleSqlParser.ParseQuery($"FROM {Table} |> LIMIT 5");
        query.FromTable.Should().Be(Table);
        query.Limit.Should().Be(5);
    }

    [Fact]
    public void Parser_parses_chained_pipes()
    {
        // Ref: GoogleSQL pipe syntax — multiple |> operations chained
        var query = GoogleSqlParser.ParseQuery(
            $"FROM {Table} |> WHERE _key = b'row1' |> SELECT _key |> ORDER BY _key DESC |> LIMIT 10");
        query.FromTable.Should().Be(Table);
        query.Where.Should().NotBeNull();
        query.Columns.Should().HaveCount(1);
        query.OrderBy.Should().HaveCount(1);
        query.Limit.Should().Be(10);
    }

    [Fact]
    public async Task ExecuteQuery_pipe_from_where()
    {
        // Ref: GoogleSQL pipe syntax — end-to-end execution
        await SeedRow("p1", "name", "Alice");
        await SeedRow("p2", "name", "Bob");

        var (metadata, results) = await ExecuteQueryAsync(
            $"FROM {Table} |> WHERE CAST({Family}['name'] AS STRING) = 'Alice' |> SELECT _key");

        metadata.Should().NotBeNull();
        var values = DecodeResults(metadata!, results);
        values.Should().HaveCount(1);
    }

    [Fact]
    public async Task ExecuteQuery_pipe_limit()
    {
        await SeedRow("pl1", "name", "Alice");
        await SeedRow("pl2", "name", "Bob");
        await SeedRow("pl3", "name", "Charlie");

        var (metadata, results) = await ExecuteQueryAsync(
            $"FROM {Table} |> SELECT _key |> LIMIT 2");

        metadata.Should().NotBeNull();
        var values = DecodeResults(metadata!, results);
        values.Should().HaveCount(2);
    }
}

using InMemoryEmulator.Bigtable;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests the in-process gRPC integration layer.
/// Verifies end-to-end: real BigtableClient → gRPC pipeline → InMemoryBigtableStore.
/// This exercises the real SDK's row assembly (CellChunk → Row), streaming, etc.
/// </summary>
public class BigtableGrpcServiceTests : IDisposable
{
    private readonly InMemoryBigtableServer _server;
    private readonly BigtableClient _client;
    private readonly TableName _tableName;
    private const string ProjectId = "test-project";
    private const string InstanceId = "test-instance";
    private const string Table = "test-table";
    private const string Family = "cf1";
    private const string Family2 = "cf2";

    public BigtableGrpcServiceTests()
    {
        var store = new InMemoryBigtableStore();
        store.CreateTable(Table, [Family, Family2]);
        _server = InMemoryBigtableServer.Create(store);
        _client = _server.Client;
        _tableName = new TableName(ProjectId, InstanceId, Table);
    }

    public void Dispose() => _server.Dispose();

    private static async Task<List<Row>> ReadAllRowsAsync(ReadRowsStream stream)
    {
        var rows = new List<Row>();
        var enumerator = stream.GetAsyncEnumerator(default);
        while (await enumerator.MoveNextAsync())
        {
            rows.Add(enumerator.Current);
        }
        return rows;
    }

    #region MutateRow

    [Fact]
    public async Task MutateRow_stores_cell()
    {
        var rowKey = new BigtableByteString("row1");
        var mutations = Mutations.SetCell(Family, "col1", "value1", new BigtableVersion(1000));

        await _client.MutateRowAsync(_tableName, rowKey, mutations);

        // Verify via ReadRow
        var row = await _client.ReadRowAsync(_tableName, rowKey);
        row.Should().NotBeNull();
        row!.Key.ToStringUtf8().Should().Be("row1");
        row.Families.Should().HaveCount(1);
        row.Families[0].Name.Should().Be(Family);
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("col1");
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("value1");
    }

    [Fact]
    public async Task MutateRow_with_server_timestamp()
    {
        var rowKey = new BigtableByteString("row-ts");
        // Timestamp -1 = server-assigned
        var mutations = Mutations.SetCell(Family, "col", "val", new BigtableVersion(-1));

        await _client.MutateRowAsync(_tableName, rowKey, mutations);

        var row = await _client.ReadRowAsync(_tableName, rowKey);
        row.Should().NotBeNull();
        // Server timestamp should be non-zero and ms-aligned
        var ts = row!.Families[0].Columns[0].Cells[0].TimestampMicros;
        ts.Should().BeGreaterThan(0);
        (ts % 1000).Should().Be(0);
    }

    [Fact]
    public async Task MutateRow_empty_key_throws()
    {
        var mutations = Mutations.SetCell(Family, "col", "val", new BigtableVersion(1000));

        // The SDK validates empty keys client-side with ArgumentException
        // (real Bigtable would return INVALID_ARGUMENT via gRPC)
        var act = async () => await _client.MutateRowAsync(_tableName, new BigtableByteString(ByteString.Empty), mutations);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task MutateRow_multiple_mutations_atomic()
    {
        var rowKey = new BigtableByteString("row-multi");
        var mutations = new[]
        {
            Mutations.SetCell(Family, "col1", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(Family, "col2", "v2", new BigtableVersion(1000)),
            Mutations.SetCell(Family2, "col3", "v3", new BigtableVersion(1000)),
        };

        await _client.MutateRowAsync(_tableName, rowKey, mutations);

        var row = await _client.ReadRowAsync(_tableName, rowKey);
        row.Should().NotBeNull();
        // Should have 2 families with total 3 cells
        row!.Families.Should().HaveCount(2);
    }

    #endregion

    #region ReadRow / ReadRows

    [Fact]
    public async Task ReadRow_nonexistent_returns_null()
    {
        var row = await _client.ReadRowAsync(_tableName, new BigtableByteString("nonexistent"));
        row.Should().BeNull();
    }

    [Fact]
    public async Task ReadRows_returns_all_rows_in_order()
    {
        // Seed rows
        await _client.MutateRowAsync(_tableName, new BigtableByteString("c"), Mutations.SetCell(Family, "col", "vc", new BigtableVersion(1000)));
        await _client.MutateRowAsync(_tableName, new BigtableByteString("a"), Mutations.SetCell(Family, "col", "va", new BigtableVersion(1000)));
        await _client.MutateRowAsync(_tableName, new BigtableByteString("b"), Mutations.SetCell(Family, "col", "vb", new BigtableVersion(1000)));

        // Read all rows
        var stream = _client.ReadRows(_tableName);
        var rows = await ReadAllRowsAsync(stream);

        rows.Should().HaveCount(3);
        rows[0].Key.ToStringUtf8().Should().Be("a");
        rows[1].Key.ToStringUtf8().Should().Be("b");
        rows[2].Key.ToStringUtf8().Should().Be("c");
    }

    [Fact]
    public async Task ReadRows_with_row_filter_filters_cells()
    {
        await _client.MutateRowAsync(_tableName, new BigtableByteString("row1"),
            Mutations.SetCell(Family, "keep", "yes", new BigtableVersion(1000)),
            Mutations.SetCell(Family, "drop", "no", new BigtableVersion(1000)));

        // Filter to only columns matching "keep"
        var filter = RowFilters.ColumnQualifierRegex("keep");
        var stream = _client.ReadRows(_tableName, filter: filter);
        var rows = await ReadAllRowsAsync(stream);

        rows.Should().HaveCount(1);
        rows[0].Families[0].Columns.Should().HaveCount(1);
        rows[0].Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("keep");
    }

    [Fact]
    public async Task ReadRows_with_rows_limit()
    {
        await _client.MutateRowAsync(_tableName, new BigtableByteString("a"), Mutations.SetCell(Family, "c", "v", new BigtableVersion(1000)));
        await _client.MutateRowAsync(_tableName, new BigtableByteString("b"), Mutations.SetCell(Family, "c", "v", new BigtableVersion(1000)));
        await _client.MutateRowAsync(_tableName, new BigtableByteString("c"), Mutations.SetCell(Family, "c", "v", new BigtableVersion(1000)));

        var stream = _client.ReadRows(_tableName, rowsLimit: 2);
        var rows = await ReadAllRowsAsync(stream);

        rows.Should().HaveCount(2);
        rows[0].Key.ToStringUtf8().Should().Be("a");
        rows[1].Key.ToStringUtf8().Should().Be("b");
    }

    [Fact]
    public async Task ReadRows_by_specific_keys()
    {
        await _client.MutateRowAsync(_tableName, new BigtableByteString("a"), Mutations.SetCell(Family, "c", "va", new BigtableVersion(1000)));
        await _client.MutateRowAsync(_tableName, new BigtableByteString("b"), Mutations.SetCell(Family, "c", "vb", new BigtableVersion(1000)));
        await _client.MutateRowAsync(_tableName, new BigtableByteString("c"), Mutations.SetCell(Family, "c", "vc", new BigtableVersion(1000)));

        var rows = new RowSet();
        rows.RowKeys.Add(ByteString.CopyFromUtf8("a"));
        rows.RowKeys.Add(ByteString.CopyFromUtf8("c"));

        var stream = _client.ReadRows(_tableName, rows: rows);
        var result = await ReadAllRowsAsync(stream);

        result.Should().HaveCount(2);
        result[0].Key.ToStringUtf8().Should().Be("a");
        result[1].Key.ToStringUtf8().Should().Be("c");
    }

    [Fact]
    public async Task ReadRows_with_range()
    {
        await _client.MutateRowAsync(_tableName, new BigtableByteString("a"), Mutations.SetCell(Family, "c", "v", new BigtableVersion(1000)));
        await _client.MutateRowAsync(_tableName, new BigtableByteString("b"), Mutations.SetCell(Family, "c", "v", new BigtableVersion(1000)));
        await _client.MutateRowAsync(_tableName, new BigtableByteString("c"), Mutations.SetCell(Family, "c", "v", new BigtableVersion(1000)));
        await _client.MutateRowAsync(_tableName, new BigtableByteString("d"), Mutations.SetCell(Family, "c", "v", new BigtableVersion(1000)));

        var rows = new RowSet();
        rows.RowRanges.Add(new Google.Cloud.Bigtable.V2.RowRange
        {
            StartKeyClosed = ByteString.CopyFromUtf8("b"),
            EndKeyOpen = ByteString.CopyFromUtf8("d"),
        });

        var stream = _client.ReadRows(_tableName, rows: rows);
        var result = await ReadAllRowsAsync(stream);

        result.Should().HaveCount(2);
        result[0].Key.ToStringUtf8().Should().Be("b");
        result[1].Key.ToStringUtf8().Should().Be("c");
    }

    [Fact]
    public async Task ReadRows_multiple_versions_ordered_desc()
    {
        var rowKey = new BigtableByteString("row-versions");
        await _client.MutateRowAsync(_tableName, rowKey, Mutations.SetCell(Family, "col", "v1", new BigtableVersion(1000)));
        await _client.MutateRowAsync(_tableName, rowKey, Mutations.SetCell(Family, "col", "v2", new BigtableVersion(2000)));
        await _client.MutateRowAsync(_tableName, rowKey, Mutations.SetCell(Family, "col", "v3", new BigtableVersion(3000)));

        var row = await _client.ReadRowAsync(_tableName, rowKey);
        row.Should().NotBeNull();
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(3);
        // Newest first (descending timestamp)
        cells[0].Value.ToStringUtf8().Should().Be("v3");
        cells[1].Value.ToStringUtf8().Should().Be("v2");
        cells[2].Value.ToStringUtf8().Should().Be("v1");
    }

    #endregion

    #region Delete mutations

    [Fact]
    public async Task DeleteFromColumn_removes_cells()
    {
        var rowKey = new BigtableByteString("row-del");
        await _client.MutateRowAsync(_tableName, rowKey,
            Mutations.SetCell(Family, "col1", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(Family, "col2", "v2", new BigtableVersion(1000)));

        await _client.MutateRowAsync(_tableName, rowKey,
            Mutations.DeleteFromColumn(Family, "col1"));

        var row = await _client.ReadRowAsync(_tableName, rowKey);
        row.Should().NotBeNull();
        row!.Families[0].Columns.Should().HaveCount(1);
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("col2");
    }

    [Fact]
    public async Task DeleteFromFamily_removes_all_cells_in_family()
    {
        var rowKey = new BigtableByteString("row-delfam");
        await _client.MutateRowAsync(_tableName, rowKey,
            Mutations.SetCell(Family, "col1", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(Family2, "col2", "v2", new BigtableVersion(1000)));

        await _client.MutateRowAsync(_tableName, rowKey,
            Mutations.DeleteFromFamily(Family));

        var row = await _client.ReadRowAsync(_tableName, rowKey);
        row.Should().NotBeNull();
        row!.Families.Should().HaveCount(1);
        row.Families[0].Name.Should().Be(Family2);
    }

    [Fact]
    public async Task DeleteFromRow_removes_entire_row()
    {
        var rowKey = new BigtableByteString("row-delrow");
        await _client.MutateRowAsync(_tableName, rowKey,
            Mutations.SetCell(Family, "col1", "v1", new BigtableVersion(1000)));

        await _client.MutateRowAsync(_tableName, rowKey, Mutations.DeleteFromRow());

        var row = await _client.ReadRowAsync(_tableName, rowKey);
        row.Should().BeNull();
    }

    #endregion

    #region CheckAndMutateRow

    [Fact]
    public async Task CheckAndMutateRow_applies_true_mutations_when_predicate_matches()
    {
        var rowKey = new BigtableByteString("row-cam");
        await _client.MutateRowAsync(_tableName, rowKey,
            Mutations.SetCell(Family, "status", "active", new BigtableVersion(1000)));

        var response = await _client.CheckAndMutateRowAsync(
            _tableName,
            rowKey,
            RowFilters.ValueRegex("active"),
            trueMutations: [Mutations.SetCell(Family, "status", "locked", new BigtableVersion(2000))],
            falseMutations: [Mutations.SetCell(Family, "status", "ignored", new BigtableVersion(2000))]);

        response.PredicateMatched.Should().BeTrue();

        var row = await _client.ReadRowAsync(_tableName, rowKey);
        // Should have 2 versions: newest "locked" and older "active"
        var cells = row!.Families[0].Columns[0].Cells;
        cells[0].Value.ToStringUtf8().Should().Be("locked");
    }

    [Fact]
    public async Task CheckAndMutateRow_applies_false_mutations_when_predicate_fails()
    {
        var rowKey = new BigtableByteString("row-cam2");
        await _client.MutateRowAsync(_tableName, rowKey,
            Mutations.SetCell(Family, "status", "active", new BigtableVersion(1000)));

        var response = await _client.CheckAndMutateRowAsync(
            _tableName,
            rowKey,
            RowFilters.ValueRegex("nonexistent"),
            trueMutations: [Mutations.SetCell(Family, "status", "ignored", new BigtableVersion(2000))],
            falseMutations: [Mutations.SetCell(Family, "status", "default", new BigtableVersion(2000))]);

        response.PredicateMatched.Should().BeFalse();

        var row = await _client.ReadRowAsync(_tableName, rowKey);
        var cells = row!.Families[0].Columns[0].Cells;
        cells[0].Value.ToStringUtf8().Should().Be("default");
    }

    #endregion

    #region ReadModifyWriteRow

    [Fact]
    public async Task ReadModifyWriteRow_increments_value()
    {
        var rowKey = new BigtableByteString("row-rmw");

        var response = await _client.ReadModifyWriteRowAsync(
            _tableName,
            rowKey,
            ReadModifyWriteRules.Increment(Family, "counter", 42));

        response.Should().NotBeNull();
        var cell = response.Row.Families[0].Columns[0].Cells[0];
        var value = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(cell.Value.Span);
        value.Should().Be(42);
    }

    [Fact]
    public async Task ReadModifyWriteRow_appends_value()
    {
        var rowKey = new BigtableByteString("row-rmw-append");

        await _client.ReadModifyWriteRowAsync(
            _tableName, rowKey,
            ReadModifyWriteRules.Append(Family, "data", "hello"));

        var response = await _client.ReadModifyWriteRowAsync(
            _tableName, rowKey,
            ReadModifyWriteRules.Append(Family, "data", " world"));

        var cell = response.Row.Families[0].Columns[0].Cells[0];
        cell.Value.ToStringUtf8().Should().Be("hello world");
    }

    #endregion

    #region MutateRows (batch)

    [Fact]
    public async Task MutateRows_batch_succeeds()
    {
        var entries = new MutateRowsRequest.Types.Entry[]
        {
            Mutations.CreateEntry(new BigtableByteString("batch-a"), Mutations.SetCell(Family, "c", "va", new BigtableVersion(1000))),
            Mutations.CreateEntry(new BigtableByteString("batch-b"), Mutations.SetCell(Family, "c", "vb", new BigtableVersion(1000))),
        };

        await _client.MutateRowsAsync(_tableName, entries);

        var rowA = await _client.ReadRowAsync(_tableName, new BigtableByteString("batch-a"));
        var rowB = await _client.ReadRowAsync(_tableName, new BigtableByteString("batch-b"));
        rowA.Should().NotBeNull();
        rowB.Should().NotBeNull();
        rowA!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("va");
        rowB!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("vb");
    }

    #endregion

    #region PingAndWarm

    [Fact]
    public async Task PingAndWarm_returns_successfully()
    {
        // PingAndWarm is a no-op, but shouldn't throw
        // We invoke it through the raw gRPC channel
        var grpcClient = new Google.Cloud.Bigtable.V2.Bigtable.BigtableClient(_server.Channel.CreateCallInvoker());
        var response = await grpcClient.PingAndWarmAsync(new PingAndWarmRequest
        {
            Name = $"projects/{ProjectId}/instances/{InstanceId}",
        });
        response.Should().NotBeNull();
    }

    #endregion

    #region SampleRowKeys

    [Fact]
    public async Task SampleRowKeys_returns_response()
    {
        var grpcClient = new Google.Cloud.Bigtable.V2.Bigtable.BigtableClient(_server.Channel.CreateCallInvoker());
        var stream = grpcClient.SampleRowKeys(new SampleRowKeysRequest
        {
            TableName = _tableName.ToString(),
        });

        var responses = new List<SampleRowKeysResponse>();
        while (await stream.ResponseStream.MoveNext(default))
        {
            responses.Add(stream.ResponseStream.Current);
        }

        responses.Should().HaveCount(1);
        responses[0].RowKey.IsEmpty.Should().BeTrue();
    }

    #endregion

    #region RequestStats

    [Fact]
    public async Task ReadRows_request_stats_full_returns_stats()
    {
        // Seed data
        await _client.MutateRowAsync(_tableName, "stats-r1",
            Mutations.SetCell(Family, "col", "v1", new BigtableVersion(1000)));
        await _client.MutateRowAsync(_tableName, "stats-r2",
            Mutations.SetCell(Family, "col", "v2", new BigtableVersion(1000)));

        // Use low-level API to set RequestStatsView
        // Ref: ReadRowsRequest.request_stats_view — when REQUEST_STATS_FULL, include stats
        var serviceApiClient = new BigtableServiceApiClientBuilder
        {
            CallInvoker = _server.Channel.CreateCallInvoker()
        }.Build();

        var request = new ReadRowsRequest
        {
            TableName = _tableName.ToString(),
            RequestStatsView = ReadRowsRequest.Types.RequestStatsView.RequestStatsFull,
        };

        var stream = serviceApiClient.ReadRows(request);
        var responses = new List<ReadRowsResponse>();
        var enumerator = stream.GetResponseStream().GetAsyncEnumerator(default);
        while (await enumerator.MoveNextAsync())
        {
            responses.Add(enumerator.Current);
        }

        // The last response should contain stats
        var statsResponse = responses.Last();
        statsResponse.RequestStats.Should().NotBeNull();
        statsResponse.RequestStats.FullReadStatsView.Should().NotBeNull();
        statsResponse.RequestStats.FullReadStatsView.ReadIterationStats.RowsReturnedCount.Should().Be(2);
    }

    #endregion

    #region GC MaxAge On Reads

    [Fact]
    public async Task ReadRows_filters_MaxAge_expired_cells_at_read_time()
    {
        // Create a table with MaxAge GC rule
        // Ref: https://cloud.google.com/bigtable/docs/garbage-collection
        var gcRules = new Dictionary<string, Google.Cloud.Bigtable.Admin.V2.GcRule?>
        {
            ["gcf"] = new Google.Cloud.Bigtable.Admin.V2.GcRule
            {
                MaxAge = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromHours(1))
            }
        };
        _server.Store.CreateTable("gc-age-table", ["gcf"], gcRules);
        var gcServer = InMemoryBigtableServer.Create(_server.Store);
        var gcClient = gcServer.Client;
        var gcTableName = new TableName(ProjectId, InstanceId, "gc-age-table");

        // Write a cell with a very old timestamp (expired by MaxAge)
        var oldTimestamp = new BigtableVersion(
            DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds());
        await gcClient.MutateRowAsync(gcTableName, "r1",
            Mutations.SetCell("gcf", "col", "old-value", oldTimestamp));

        // Write a cell with a recent timestamp (not expired)
        var recentTimestamp = new BigtableVersion(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await gcClient.MutateRowAsync(gcTableName, "r1",
            Mutations.SetCell("gcf", "col", "new-value", recentTimestamp));

        // Read — expired cell should be filtered out
        var row = await gcClient.ReadRowAsync(gcTableName, "r1");
        row.Should().NotBeNull();
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(1);
        cells[0].Value.ToStringUtf8().Should().Be("new-value");

        gcServer.Dispose();
    }

    #endregion
}

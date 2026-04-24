using Bigtable.InMemoryEmulator;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for ReadChangeStream and GenerateInitialChangeStreamPartitions.
///
/// Note: The Go emulator does NOT support ReadChangeStream.
/// These tests are tagged InMemoryOnly and GcpOnly.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readchangestreamrequest
/// </summary>
[Trait(TestTraits.Target, TestTraits.InMemoryOnly)]
public class ChangeStreamTests : IDisposable
{
    private readonly InMemoryBigtableServer _server;
    private readonly BigtableClient _client;
    private readonly TableName _tableName;
    private readonly BigtableServiceApiClient _serviceApiClient;

    public ChangeStreamTests()
    {
        var store = new InMemoryBigtableStore();
        store.CreateTable("change-test", ["cf1", "cf2"]);
        _server = InMemoryBigtableServer.Create(store);
        _client = _server.Client;
        _tableName = new TableName("test-project", "test-instance", "change-test");

        // Need the low-level service API client for ReadChangeStream (BigtableClient doesn't expose it)
        _serviceApiClient = new BigtableServiceApiClientBuilder
        {
            CallInvoker = _server.Channel.CreateCallInvoker()
        }.Build();
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    [Fact]
    public async Task GenerateInitialChangeStreamPartitions_returns_single_partition()
    {
        var request = new GenerateInitialChangeStreamPartitionsRequest
        {
            TableNameAsTableName = _tableName,
        };

        var stream = _serviceApiClient.GenerateInitialChangeStreamPartitions(request);
        var responses = new List<GenerateInitialChangeStreamPartitionsResponse>();
        var enumerator = stream.GetResponseStream().GetAsyncEnumerator(default);
        while (await enumerator.MoveNextAsync())
        {
            responses.Add(enumerator.Current);
        }

        responses.Should().HaveCount(1);
        var partition = responses[0].Partition;
        partition.Should().NotBeNull();
        partition.RowRange.StartKeyClosed.Should().BeEmpty();
        partition.RowRange.EndKeyOpen.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadChangeStream_emits_DataChange_for_MutateRow()
    {
        // Perform a mutation first
        await _client.MutateRowAsync(_tableName, new BigtableByteString("row1"),
            Mutations.SetCell("cf1", "col", "value1", new BigtableVersion(1000)));

        // Read the change stream with end_time in the future
        var responses = await ReadChangeStreamWithTimeout(endTimeInFuture: true);

        // Should have at least one DataChange
        var dataChanges = responses
            .Where(r => r.StreamRecordCase == ReadChangeStreamResponse.StreamRecordOneofCase.DataChange)
            .ToList();

        dataChanges.Should().HaveCount(1);
        var change = dataChanges[0].DataChange;
        change.RowKey.ToStringUtf8().Should().Be("row1");
        change.Done.Should().BeTrue();
        change.Type.Should().Be(ReadChangeStreamResponse.Types.DataChange.Types.Type.User);
        change.Chunks.Should().HaveCount(1);
        change.Chunks[0].Mutation.SetCell.FamilyName.Should().Be("cf1");
    }

    [Fact]
    public async Task ReadChangeStream_emits_DataChange_for_multiple_mutations()
    {
        await _client.MutateRowAsync(_tableName, new BigtableByteString("row1"),
            Mutations.SetCell("cf1", "col", "v1", new BigtableVersion(1000)));
        await _client.MutateRowAsync(_tableName, new BigtableByteString("row2"),
            Mutations.SetCell("cf1", "col", "v2", new BigtableVersion(2000)));
        await _client.MutateRowAsync(_tableName, new BigtableByteString("row3"),
            Mutations.SetCell("cf2", "col", "v3", new BigtableVersion(3000)));

        var responses = await ReadChangeStreamWithTimeout(endTimeInFuture: true);
        var dataChanges = responses
            .Where(r => r.StreamRecordCase == ReadChangeStreamResponse.StreamRecordOneofCase.DataChange)
            .ToList();

        dataChanges.Should().HaveCount(3);
        dataChanges[0].DataChange.RowKey.ToStringUtf8().Should().Be("row1");
        dataChanges[1].DataChange.RowKey.ToStringUtf8().Should().Be("row2");
        dataChanges[2].DataChange.RowKey.ToStringUtf8().Should().Be("row3");
    }

    [Fact]
    public async Task ReadChangeStream_captures_delete_mutations()
    {
        await _client.MutateRowAsync(_tableName, new BigtableByteString("row1"),
            Mutations.SetCell("cf1", "col", "value", new BigtableVersion(1000)));
        await _client.MutateRowAsync(_tableName, new BigtableByteString("row1"),
            Mutations.DeleteFromRow());

        var responses = await ReadChangeStreamWithTimeout(endTimeInFuture: true);
        var dataChanges = responses
            .Where(r => r.StreamRecordCase == ReadChangeStreamResponse.StreamRecordOneofCase.DataChange)
            .ToList();

        dataChanges.Should().HaveCount(2);
        // Second change is the delete
        var deleteChange = dataChanges[1].DataChange;
        deleteChange.RowKey.ToStringUtf8().Should().Be("row1");
        deleteChange.Chunks[0].Mutation.DeleteFromRow.Should().NotBeNull();
    }

    [Fact]
    public async Task ReadChangeStream_captures_CheckAndMutateRow()
    {
        await _client.MutateRowAsync(_tableName, new BigtableByteString("row1"),
            Mutations.SetCell("cf1", "col", "old", new BigtableVersion(1000)));

        await _client.CheckAndMutateRowAsync(_tableName, new BigtableByteString("row1"),
            RowFilters.PassAllFilter(),
            new[] { Mutations.SetCell("cf1", "col", "new", new BigtableVersion(2000)) },
            null);

        var responses = await ReadChangeStreamWithTimeout(endTimeInFuture: true);
        var dataChanges = responses
            .Where(r => r.StreamRecordCase == ReadChangeStreamResponse.StreamRecordOneofCase.DataChange)
            .ToList();

        // Two changes: initial mutate + check-and-mutate
        dataChanges.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReadChangeStream_captures_ReadModifyWriteRow()
    {
        await _client.ReadModifyWriteRowAsync(_tableName, new BigtableByteString("row1"),
            ReadModifyWriteRules.Append("cf1", "col", "hello"));

        var responses = await ReadChangeStreamWithTimeout(endTimeInFuture: true);
        var dataChanges = responses
            .Where(r => r.StreamRecordCase == ReadChangeStreamResponse.StreamRecordOneofCase.DataChange)
            .ToList();

        dataChanges.Should().HaveCount(1);
        var change = dataChanges[0].DataChange;
        change.RowKey.ToStringUtf8().Should().Be("row1");
        // ReadModifyWriteRow is recorded as a synthetic SetCell mutation
        change.Chunks[0].Mutation.SetCell.Should().NotBeNull();
    }

    [Fact]
    public async Task ReadChangeStream_captures_MutateRows_batch()
    {
        var entries = new MutateRowsRequest.Types.Entry[]
        {
            Mutations.CreateEntry(new BigtableByteString("batch1"),
                Mutations.SetCell("cf1", "col", "a", new BigtableVersion(1000))),
            Mutations.CreateEntry(new BigtableByteString("batch2"),
                Mutations.SetCell("cf1", "col", "b", new BigtableVersion(2000))),
        };

        await _client.MutateRowsAsync(_tableName, entries);

        var responses = await ReadChangeStreamWithTimeout(endTimeInFuture: true);
        var dataChanges = responses
            .Where(r => r.StreamRecordCase == ReadChangeStreamResponse.StreamRecordOneofCase.DataChange)
            .ToList();

        // Each batch entry produces its own log entry
        dataChanges.Should().HaveCount(2);
        dataChanges[0].DataChange.RowKey.ToStringUtf8().Should().Be("batch1");
        dataChanges[1].DataChange.RowKey.ToStringUtf8().Should().Be("batch2");
    }

    [Fact]
    public async Task ReadChangeStream_continuation_token_resumes_correctly()
    {
        // Write 3 mutations
        await _client.MutateRowAsync(_tableName, new BigtableByteString("row1"),
            Mutations.SetCell("cf1", "col", "v1", new BigtableVersion(1000)));
        await _client.MutateRowAsync(_tableName, new BigtableByteString("row2"),
            Mutations.SetCell("cf1", "col", "v2", new BigtableVersion(2000)));
        await _client.MutateRowAsync(_tableName, new BigtableByteString("row3"),
            Mutations.SetCell("cf1", "col", "v3", new BigtableVersion(3000)));

        // Read all to get tokens
        var responses1 = await ReadChangeStreamWithTimeout(endTimeInFuture: true);
        var dataChanges1 = responses1
            .Where(r => r.StreamRecordCase == ReadChangeStreamResponse.StreamRecordOneofCase.DataChange)
            .ToList();
        dataChanges1.Should().HaveCount(3);

        // Get the token from the first change (should be "1" — skips entry 0)
        var resumeToken = dataChanges1[0].DataChange.Token;

        // Resume from that token
        var responses2 = await ReadChangeStreamWithTimeout(
            endTimeInFuture: true,
            continuationToken: resumeToken);
        var dataChanges2 = responses2
            .Where(r => r.StreamRecordCase == ReadChangeStreamResponse.StreamRecordOneofCase.DataChange)
            .ToList();

        // Should get entries 2 and 3 (skipping entry 1 which was already consumed)
        dataChanges2.Should().HaveCount(2);
        dataChanges2[0].DataChange.RowKey.ToStringUtf8().Should().Be("row2");
        dataChanges2[1].DataChange.RowKey.ToStringUtf8().Should().Be("row3");
    }

    [Fact]
    public async Task ReadChangeStream_multi_mutation_row_has_all_chunks()
    {
        // A single MutateRow with multiple mutations
        await _client.MutateRowAsync(_tableName, new BigtableByteString("row1"),
            Mutations.SetCell("cf1", "col1", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf1", "col2", "v2", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "col3", "v3", new BigtableVersion(1000)));

        var responses = await ReadChangeStreamWithTimeout(endTimeInFuture: true);
        var dataChanges = responses
            .Where(r => r.StreamRecordCase == ReadChangeStreamResponse.StreamRecordOneofCase.DataChange)
            .ToList();

        dataChanges.Should().HaveCount(1);
        var change = dataChanges[0].DataChange;
        change.Chunks.Should().HaveCount(3);
    }

    [Fact]
    public async Task ReadChangeStream_with_end_time_closes_stream()
    {
        // Write some data
        await _client.MutateRowAsync(_tableName, new BigtableByteString("row1"),
            Mutations.SetCell("cf1", "col", "v1", new BigtableVersion(1000)));

        // Request with an end_time in the past — should close quickly
        var endTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(-1));

        var request = new ReadChangeStreamRequest
        {
            TableNameAsTableName = _tableName,
            EndTime = endTime,
        };

        var stream = _serviceApiClient.ReadChangeStream(request);
        var responses = new List<ReadChangeStreamResponse>();
        var enumerator = stream.GetResponseStream().GetAsyncEnumerator(default);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (await enumerator.MoveNextAsync())
            {
                responses.Add(enumerator.Current);
                if (enumerator.Current.StreamRecordCase == ReadChangeStreamResponse.StreamRecordOneofCase.CloseStream)
                    break;
                cts.Token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) { }

        // Should have a CloseStream response
        responses.Should().Contain(r =>
            r.StreamRecordCase == ReadChangeStreamResponse.StreamRecordOneofCase.CloseStream);
    }

    [Fact]
    public async Task ReadChangeStream_heartbeat_has_continuation_token()
    {
        // Read empty stream — should get heartbeat
        var request = new ReadChangeStreamRequest
        {
            TableNameAsTableName = _tableName,
            HeartbeatDuration = Duration.FromTimeSpan(TimeSpan.FromMilliseconds(100)),
        };

        var stream = _serviceApiClient.ReadChangeStream(request);
        var responses = new List<ReadChangeStreamResponse>();
        var enumerator = stream.GetResponseStream().GetAsyncEnumerator(default);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            while (await enumerator.MoveNextAsync())
            {
                responses.Add(enumerator.Current);
                if (responses.Count >= 1) break;
                cts.Token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) { }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) { }

        // Should have at least one heartbeat
        var heartbeats = responses
            .Where(r => r.StreamRecordCase == ReadChangeStreamResponse.StreamRecordOneofCase.Heartbeat)
            .ToList();
        heartbeats.Should().NotBeEmpty();
        heartbeats[0].Heartbeat.ContinuationToken.Should().NotBeNull();
        heartbeats[0].Heartbeat.ContinuationToken.Token.Should().NotBeNullOrEmpty();
        heartbeats[0].Heartbeat.EstimatedLowWatermark.Should().NotBeNull();
    }

    [Fact]
    public async Task ReadChangeStream_data_change_has_commit_timestamp()
    {
        var before = DateTimeOffset.UtcNow;

        await _client.MutateRowAsync(_tableName, new BigtableByteString("row1"),
            Mutations.SetCell("cf1", "col", "v1", new BigtableVersion(1000)));

        var after = DateTimeOffset.UtcNow;

        var responses = await ReadChangeStreamWithTimeout(endTimeInFuture: true);
        var dataChange = responses
            .First(r => r.StreamRecordCase == ReadChangeStreamResponse.StreamRecordOneofCase.DataChange)
            .DataChange;

        dataChange.CommitTimestamp.Should().NotBeNull();
        var commitTime = dataChange.CommitTimestamp.ToDateTimeOffset();
        commitTime.Should().BeOnOrAfter(before);
        commitTime.Should().BeOnOrBefore(after.AddSeconds(1));
    }

    /// <summary>
    /// Helper: Reads the change stream with a timeout, collecting all responses.
    /// Uses end_time slightly in the future so the stream completes after draining existing entries.
    /// </summary>
    private async Task<List<ReadChangeStreamResponse>> ReadChangeStreamWithTimeout(
        bool endTimeInFuture = false,
        string? continuationToken = null)
    {
        var request = new ReadChangeStreamRequest
        {
            TableNameAsTableName = _tableName,
        };

        if (endTimeInFuture)
        {
            // End time slightly in the future so stream drains existing entries then closes
            request.EndTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMilliseconds(200));
        }

        if (continuationToken != null)
        {
            var partition = new StreamPartition
            {
                RowRange = new Google.Cloud.Bigtable.V2.RowRange
                {
                    StartKeyClosed = ByteString.Empty,
                    EndKeyOpen = ByteString.Empty,
                }
            };
            request.ContinuationTokens = new StreamContinuationTokens();
            request.ContinuationTokens.Tokens.Add(new StreamContinuationToken
            {
                Partition = partition,
                Token = continuationToken,
            });
        }

        var stream = _serviceApiClient.ReadChangeStream(request);
        var responses = new List<ReadChangeStreamResponse>();
        var enumerator = stream.GetResponseStream().GetAsyncEnumerator(default);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (await enumerator.MoveNextAsync())
            {
                responses.Add(enumerator.Current);
                if (enumerator.Current.StreamRecordCase == ReadChangeStreamResponse.StreamRecordOneofCase.CloseStream)
                    break;
                cts.Token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) { }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) { }

        return responses;
    }
}

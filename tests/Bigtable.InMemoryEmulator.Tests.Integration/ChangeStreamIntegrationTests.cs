using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Integration tests for ReadChangeStream and GenerateInitialChangeStreamPartitions.
///
/// Note: The Go emulator does NOT support ReadChangeStream (it panics).
/// These tests use GcpOnly trait — runs against in-memory and real GCP, but not Go emulator.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readchangestreamrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.GcpOnly)]
public sealed class ChangeStreamIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "cs-tests";
    private const string Family = "cf1";
    private const string Family2 = "cf2";

    public ChangeStreamIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { Family, Family2 });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private BigtableClient Client => _fixture.Client;
    private BigtableServiceApiClient ServiceApiClient => _fixture.ServiceApiClient;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task GenerateInitialChangeStreamPartitions_returns_single_partition()
    {
        var request = new GenerateInitialChangeStreamPartitionsRequest
        {
            TableNameAsTableName = TN,
        };

        var stream = ServiceApiClient.GenerateInitialChangeStreamPartitions(request);
        var responses = new List<GenerateInitialChangeStreamPartitionsResponse>();
        var enumerator = stream.GetResponseStream().GetAsyncEnumerator(default);
        while (await enumerator.MoveNextAsync())
        {
            responses.Add(enumerator.Current);
        }

        responses.Should().HaveCountGreaterThanOrEqualTo(1);
        var partition = responses[0].Partition;
        partition.Should().NotBeNull();
    }

    [Fact]
    public async Task ReadChangeStream_emits_DataChange_for_MutateRow()
    {
        await Client.MutateRowAsync(TN, new BigtableByteString("cs-r1"),
            Mutations.SetCell(Family, "col", "value1", new BigtableVersion(1000)));

        var responses = await ReadChangeStreamWithTimeout(endTimeInFuture: true);

        var dataChanges = responses
            .Where(r => r.StreamRecordCase == ReadChangeStreamResponse.StreamRecordOneofCase.DataChange)
            .ToList();

        dataChanges.Should().HaveCountGreaterThanOrEqualTo(1);
        var change = dataChanges.First(d => d.DataChange.RowKey.ToStringUtf8() == "cs-r1").DataChange;
        change.Done.Should().BeTrue();
        change.Type.Should().Be(ReadChangeStreamResponse.Types.DataChange.Types.Type.User);
        change.Chunks.Should().HaveCountGreaterThanOrEqualTo(1);
        change.Chunks[0].Mutation.SetCell.FamilyName.Should().Be(Family);
    }

    [Fact]
    public async Task ReadChangeStream_emits_DataChange_for_multiple_mutations()
    {
        await Client.MutateRowAsync(TN, new BigtableByteString("cs-m1"),
            Mutations.SetCell(Family, "col", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, new BigtableByteString("cs-m2"),
            Mutations.SetCell(Family, "col", "v2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, new BigtableByteString("cs-m3"),
            Mutations.SetCell(Family2, "col", "v3", new BigtableVersion(3000)));

        var responses = await ReadChangeStreamWithTimeout(endTimeInFuture: true);
        var dataChanges = responses
            .Where(r => r.StreamRecordCase == ReadChangeStreamResponse.StreamRecordOneofCase.DataChange)
            .ToList();

        dataChanges.Should().HaveCountGreaterThanOrEqualTo(3);
        var keys = dataChanges.Select(d => d.DataChange.RowKey.ToStringUtf8()).ToList();
        keys.Should().Contain("cs-m1");
        keys.Should().Contain("cs-m2");
        keys.Should().Contain("cs-m3");
    }

    [Fact]
    public async Task ReadChangeStream_captures_delete_mutations()
    {
        await Client.MutateRowAsync(TN, new BigtableByteString("cs-del"),
            Mutations.SetCell(Family, "col", "value", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, new BigtableByteString("cs-del"),
            Mutations.DeleteFromRow());

        var responses = await ReadChangeStreamWithTimeout(endTimeInFuture: true);
        var dataChanges = responses
            .Where(r => r.StreamRecordCase == ReadChangeStreamResponse.StreamRecordOneofCase.DataChange)
            .Where(r => r.DataChange.RowKey.ToStringUtf8() == "cs-del")
            .ToList();

        dataChanges.Should().HaveCount(2);
        // Second change is the delete
        var deleteChange = dataChanges[1].DataChange;
        deleteChange.Chunks[0].Mutation.DeleteFromRow.Should().NotBeNull();
    }

    [Fact]
    public async Task ReadChangeStream_captures_CheckAndMutateRow()
    {
        await Client.MutateRowAsync(TN, new BigtableByteString("cs-cam"),
            Mutations.SetCell(Family, "col", "old", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, new BigtableByteString("cs-cam"),
            RowFilters.PassAllFilter(),
            new[] { Mutations.SetCell(Family, "col", "new", new BigtableVersion(2000)) },
            null);

        var responses = await ReadChangeStreamWithTimeout(endTimeInFuture: true);
        var dataChanges = responses
            .Where(r => r.StreamRecordCase == ReadChangeStreamResponse.StreamRecordOneofCase.DataChange)
            .Where(r => r.DataChange.RowKey.ToStringUtf8() == "cs-cam")
            .ToList();

        dataChanges.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReadChangeStream_captures_ReadModifyWriteRow()
    {
        await Client.ReadModifyWriteRowAsync(TN, new BigtableByteString("cs-rmw"),
            ReadModifyWriteRules.Append(Family, "col", "hello"));

        var responses = await ReadChangeStreamWithTimeout(endTimeInFuture: true);
        var dataChanges = responses
            .Where(r => r.StreamRecordCase == ReadChangeStreamResponse.StreamRecordOneofCase.DataChange)
            .Where(r => r.DataChange.RowKey.ToStringUtf8() == "cs-rmw")
            .ToList();

        dataChanges.Should().HaveCount(1);
        var change = dataChanges[0].DataChange;
        // ReadModifyWriteRow is recorded as a synthetic SetCell mutation
        change.Chunks[0].Mutation.SetCell.Should().NotBeNull();
    }

    [Fact]
    public async Task ReadChangeStream_captures_MutateRows_batch()
    {
        var entries = new MutateRowsRequest.Types.Entry[]
        {
            Mutations.CreateEntry(new BigtableByteString("cs-b1"),
                Mutations.SetCell(Family, "col", "a", new BigtableVersion(1000))),
            Mutations.CreateEntry(new BigtableByteString("cs-b2"),
                Mutations.SetCell(Family, "col", "b", new BigtableVersion(2000))),
        };

        await Client.MutateRowsAsync(TN, entries);

        var responses = await ReadChangeStreamWithTimeout(endTimeInFuture: true);
        var dataChanges = responses
            .Where(r => r.StreamRecordCase == ReadChangeStreamResponse.StreamRecordOneofCase.DataChange)
            .ToList();

        var keys = dataChanges.Select(d => d.DataChange.RowKey.ToStringUtf8()).ToList();
        keys.Should().Contain("cs-b1");
        keys.Should().Contain("cs-b2");
    }

    [Fact]
    public async Task ReadChangeStream_continuation_token_resumes_correctly()
    {
        // Write 3 mutations
        await Client.MutateRowAsync(TN, new BigtableByteString("cs-ct1"),
            Mutations.SetCell(Family, "col", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, new BigtableByteString("cs-ct2"),
            Mutations.SetCell(Family, "col", "v2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, new BigtableByteString("cs-ct3"),
            Mutations.SetCell(Family, "col", "v3", new BigtableVersion(3000)));

        // Read all to get tokens
        var responses1 = await ReadChangeStreamWithTimeout(endTimeInFuture: true);
        var dataChanges1 = responses1
            .Where(r => r.StreamRecordCase == ReadChangeStreamResponse.StreamRecordOneofCase.DataChange)
            .Where(r => r.DataChange.RowKey.ToStringUtf8().StartsWith("cs-ct"))
            .ToList();
        dataChanges1.Should().HaveCount(3);

        // Get the token from the second change — resuming should skip it and get only the third
        var resumeToken = dataChanges1[1].DataChange.Token;

        // Resume from that token
        var responses2 = await ReadChangeStreamWithTimeout(
            endTimeInFuture: true,
            continuationToken: resumeToken);
        var dataChanges2 = responses2
            .Where(r => r.StreamRecordCase == ReadChangeStreamResponse.StreamRecordOneofCase.DataChange)
            .Where(r => r.DataChange.RowKey.ToStringUtf8().StartsWith("cs-ct"))
            .ToList();

        // Should get only entry 3 (skipping entries 1 and 2)
        dataChanges2.Should().HaveCount(1);
        dataChanges2[0].DataChange.RowKey.ToStringUtf8().Should().Be("cs-ct3");
    }

    [Fact]
    public async Task ReadChangeStream_with_end_time_closes_stream()
    {
        await Client.MutateRowAsync(TN, new BigtableByteString("cs-end"),
            Mutations.SetCell(Family, "col", "v1", new BigtableVersion(1000)));

        // Request with an end_time in the past — should close quickly
        var endTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(-1));

        var request = new ReadChangeStreamRequest
        {
            TableNameAsTableName = TN,
            EndTime = endTime,
        };

        var stream = ServiceApiClient.ReadChangeStream(request);
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
    public async Task ReadChangeStream_data_change_has_commit_timestamp()
    {
        var before = DateTimeOffset.UtcNow;

        await Client.MutateRowAsync(TN, new BigtableByteString("cs-ts"),
            Mutations.SetCell(Family, "col", "v1", new BigtableVersion(1000)));

        var after = DateTimeOffset.UtcNow;

        var responses = await ReadChangeStreamWithTimeout(endTimeInFuture: true);
        var dataChange = responses
            .Where(r => r.StreamRecordCase == ReadChangeStreamResponse.StreamRecordOneofCase.DataChange)
            .First(r => r.DataChange.RowKey.ToStringUtf8() == "cs-ts")
            .DataChange;

        dataChange.CommitTimestamp.Should().NotBeNull();
        var commitTime = dataChange.CommitTimestamp.ToDateTimeOffset();
        commitTime.Should().BeOnOrAfter(before);
        commitTime.Should().BeOnOrBefore(after.AddSeconds(1));
    }

    [Fact]
    public async Task ReadChangeStream_multi_mutation_row_has_all_chunks()
    {
        await Client.MutateRowAsync(TN, new BigtableByteString("cs-mc"),
            Mutations.SetCell(Family, "col1", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(Family, "col2", "v2", new BigtableVersion(1000)),
            Mutations.SetCell(Family2, "col3", "v3", new BigtableVersion(1000)));

        var responses = await ReadChangeStreamWithTimeout(endTimeInFuture: true);
        var dataChanges = responses
            .Where(r => r.StreamRecordCase == ReadChangeStreamResponse.StreamRecordOneofCase.DataChange)
            .Where(r => r.DataChange.RowKey.ToStringUtf8() == "cs-mc")
            .ToList();

        dataChanges.Should().HaveCount(1);
        var change = dataChanges[0].DataChange;
        change.Chunks.Should().HaveCount(3);
    }

    /// <summary>
    /// Helper: Reads the change stream with a timeout, collecting all responses.
    /// </summary>
    private async Task<List<ReadChangeStreamResponse>> ReadChangeStreamWithTimeout(
        bool endTimeInFuture = false,
        string? continuationToken = null)
    {
        var request = new ReadChangeStreamRequest
        {
            TableNameAsTableName = TN,
        };

        if (endTimeInFuture)
        {
            request.EndTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMilliseconds(500));
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

        var stream = ServiceApiClient.ReadChangeStream(request);
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

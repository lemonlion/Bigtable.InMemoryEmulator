using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace InMemoryEmulator.Bigtable;

/// <summary>
/// In-process gRPC service implementing the Bigtable Data API.
/// This is the "FakeCosmosHandler" equivalent — the real SDK's BigtableClientImpl
/// sits between the user and this service, providing row assembly (CellChunk → Row),
/// retry logic, and AppProfileId propagation.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.Bigtable
/// </summary>
internal sealed class BigtableGrpcService : Google.Cloud.Bigtable.V2.Bigtable.BigtableBase
{
    private readonly InMemoryBigtableStore _store;
    private readonly FaultInjector? _faultInjector;
    private readonly RpcLog? _rpcLog;
    private readonly QueryLog? _queryLog;

    public BigtableGrpcService(InMemoryBigtableStore store,
        FaultInjector faultInjector,
        RpcLog rpcLog,
        QueryLog queryLog)
    {
        _store = store;
        _faultInjector = faultInjector;
        _rpcLog = rpcLog;
        _queryLog = queryLog;
    }

    /// <summary>
    /// Checks whether a fault should be injected for this RPC. Throws RpcException if so.
    /// Also records the RPC in the log.
    /// </summary>
    private void CheckFaultAndLog(ServerCallContext context, string? tableName = null, string? rowKey = null)
    {
        var method = context.Method;
        if (_faultInjector != null)
        {
            var faultStatus = _faultInjector.Check(new FaultContext
            {
                Method = method,
                TableName = tableName,
                RowKey = rowKey,
            });
            if (faultStatus.HasValue)
            {
                _rpcLog?.Record(new RpcLogEntry
                {
                    Method = method,
                    TableName = tableName,
                    Succeeded = false,
                    StatusCode = faultStatus.Value.StatusCode,
                });
                throw new RpcException(faultStatus.Value);
            }
        }

        // Record the RPC call (success path — will be logged before execution)
        _rpcLog?.Record(new RpcLogEntry
        {
            Method = method,
            TableName = tableName,
            Succeeded = true,
            StatusCode = StatusCode.OK,
        });
    }

    /// <summary>
    /// Rejects requests that specify an authorized_view_name, which is not supported.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
    ///   "authorized_view_name" is an optional field on all Data API requests.
    ///   The in-memory emulator does not support AuthorizedViews; return UNIMPLEMENTED.
    /// </summary>
    private static void RejectAuthorizedView(string? authorizedViewName)
    {
        if (!string.IsNullOrEmpty(authorizedViewName))
        {
            throw new RpcException(new Status(StatusCode.Unimplemented,
                "AuthorizedViews are not supported by the in-memory emulator."));
        }
    }

    /// <summary>
    /// Adds synthetic gRPC trailing metadata to the response, mimicking the real Bigtable service.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#responseparams
    ///   The real service returns zone_id, cluster_id in ResponseParams via trailing metadata.
    ///   We stub these with synthetic values for diagnostics fidelity.
    /// </summary>
    private static void AddTrailingMetadata(ServerCallContext context)
    {
        context.ResponseTrailers.Add("server-timing", "gfet4t7; dur=0");
        context.ResponseTrailers.Add("x-goog-ext-425905942-bin",
            Google.Protobuf.MessageExtensions.ToByteArray(new ResponseParams
            {
                ZoneId = "inmemory-zone",
                ClusterId = "inmemory-cluster",
            }));
    }

    /// <summary>
    /// Streams back the contents of all requested rows in key order, optionally applying a RowFilter.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
    /// </summary>
    public override async Task ReadRows(
        ReadRowsRequest request,
        IServerStreamWriter<ReadRowsResponse> responseStream,
        ServerCallContext context)
    {
        var tableName = ExtractTableName(request.TableName);
        RejectAuthorizedView(request.AuthorizedViewName);
        CheckFaultAndLog(context, tableName);
        var table = _store.GetTable(tableName);

        // Validate RowFilter if present
        if (request.Filter != null)
        {
            RowFilterEvaluator.Validate(request.Filter);
        }

        // Build row keys and ranges from the request
        IReadOnlyList<ByteString>? rowKeys = null;
        IReadOnlyList<RowRange>? rowRanges = null;

        if (request.Rows != null)
        {
            if (request.Rows.RowKeys.Count > 0)
            {
                rowKeys = request.Rows.RowKeys.ToList();
            }
            if (request.Rows.RowRanges.Count > 0)
            {
                rowRanges = request.Rows.RowRanges
                    .Select(RowRange.FromProto)
                    .ToList();
            }
        }

        var rows = table.ReadRows(rowKeys, rowRanges, reversed: request.Reversed);

        long rowsSeen = 0;
        long rowsReturned = 0;
        long cellsSeen = 0;
        long cellsReturned = 0;

        foreach (var row in rows)
        {
            if (context.CancellationToken.IsCancellationRequested)
                break;

            // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
            //   "rows_limit: The read will return no more rows than this value."
            //   Limit applies to returned (post-filter) rows, not scanned rows.
            if (request.RowsLimit > 0 && rowsReturned >= request.RowsLimit)
                break;

            rowsSeen++;

            // Get cells, filter out GC-expired cells at read time, apply user filter
            var cells = table.FilterCellsByGcRules(row.GetCells());
            cellsSeen += cells.Count;

            IReadOnlyList<CellData> filteredCells;
            if (request.Filter != null)
            {
                filteredCells = RowFilterEvaluator.Apply(request.Filter, cells, row.Key);
            }
            else
            {
                filteredCells = cells;
            }

            if (filteredCells.Count == 0)
                continue;

            // Ensure cells are in canonical order (family ASC, qualifier ASC, timestamp DESC).
            // Filters like Interleave can produce cells out of order, but the SDK's CellChunk
            // reader expects families and columns to appear in sorted order without repetition.
            // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsresponse
            filteredCells = filteredCells
                .OrderBy(c => c.Family, StringComparer.Ordinal)
                .ThenBy(c => c.Qualifier, ByteStringComparer.Instance)
                .ThenByDescending(c => c.TimestampMicros)
                .ToList();

            rowsReturned++;
            cellsReturned += filteredCells.Count;

            // Emit CellChunks for this row
            var response = new ReadRowsResponse();

            for (int i = 0; i < filteredCells.Count; i++)
            {
                var cell = filteredCells[i];
                var chunk = new ReadRowsResponse.Types.CellChunk
                {
                    RowKey = row.Key,
                    FamilyName = cell.Family,
                    Qualifier = cell.Qualifier,
                    TimestampMicros = cell.TimestampMicros,
                    Value = cell.Value,
                };

                if (cell.Labels.Count > 0)
                {
                    chunk.Labels.AddRange(cell.Labels);
                }

                // Last cell in the row gets commit_row = true
                if (i == filteredCells.Count - 1)
                {
                    chunk.CommitRow = true;
                }

                response.Chunks.Add(chunk);
            }

            // Set last_scanned_row_key for retry optimization
            response.LastScannedRowKey = row.Key;

            await responseStream.WriteAsync(response);
        }

        // If request_stats_view is FULL, emit stats in a final response
        if (request.RequestStatsView == ReadRowsRequest.Types.RequestStatsView.RequestStatsFull)
        {
            var statsResponse = new ReadRowsResponse
            {
                RequestStats = new RequestStats
                {
                    FullReadStatsView = new FullReadStatsView
                    {
                        ReadIterationStats = new ReadIterationStats
                        {
                            RowsSeenCount = rowsSeen,
                            RowsReturnedCount = rowsReturned,
                            CellsSeenCount = cellsSeen,
                            CellsReturnedCount = cellsReturned,
                        }
                    }
                }
            };
            await responseStream.WriteAsync(statsResponse);
        }
    }

    /// <summary>
    /// Returns a sample of row keys in the table.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#samplerowkeysrequest
    /// For a single in-memory "tablet", return one entry: ("", 0).
    /// </summary>
    public override async Task SampleRowKeys(
        SampleRowKeysRequest request,
        IServerStreamWriter<SampleRowKeysResponse> responseStream,
        ServerCallContext context)
    {
        var tableName = ExtractTableName(request.TableName);
        RejectAuthorizedView(request.AuthorizedViewName);
        _store.GetTable(tableName); // Verify table exists

        // Single tablet — return one entry representing the end boundary
        await responseStream.WriteAsync(new SampleRowKeysResponse
        {
            RowKey = ByteString.Empty,
            OffsetBytes = 0,
        });
    }

    /// <summary>
    /// Mutates a row atomically.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowrequest
    /// </summary>
    public override Task<MutateRowResponse> MutateRow(
        MutateRowRequest request,
        ServerCallContext context)
    {
        var tableName = ExtractTableName(request.TableName);
        RejectAuthorizedView(request.AuthorizedViewName);
        CheckFaultAndLog(context, tableName, request.RowKey.ToStringUtf8());
        var table = _store.GetTable(tableName);
        table.MutateRow(request.RowKey, request.Mutations);
        AddTrailingMetadata(context);
        return Task.FromResult(new MutateRowResponse());
    }

    /// <summary>
    /// Mutates multiple rows in a batch. Each entry is independent.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowsrequest
    /// </summary>
    public override async Task MutateRows(
        MutateRowsRequest request,
        IServerStreamWriter<MutateRowsResponse> responseStream,
        ServerCallContext context)
    {
        var tableName = ExtractTableName(request.TableName);
        RejectAuthorizedView(request.AuthorizedViewName);
        CheckFaultAndLog(context, tableName);

        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowsrequest
        //   "entries: Required. The key/mutation pairs to apply in bulk."
        if (request.Entries.Count == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "MutateRowsRequest must contain at least one entry."));
        }

        foreach (var entry in request.Entries)
        {
            if (entry.Mutations.Count == 0)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    "Each MutateRowsRequest entry must contain at least one mutation."));
            }
        }

        var table = _store.GetTable(tableName);
        var results = table.MutateRows(request.Entries.ToList());

        var response = new MutateRowsResponse();
        foreach (var (index, status) in results)
        {
            response.Entries.Add(new MutateRowsResponse.Types.Entry
            {
                Index = index,
                Status = new Google.Rpc.Status
                {
                    Code = (int)status.StatusCode,
                    Message = status.Detail ?? "",
                },
            });
        }

        await responseStream.WriteAsync(response);
    }

    /// <summary>
    /// Atomic compare-and-swap.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#checkandmutaterowrequest
    /// </summary>
    public override Task<CheckAndMutateRowResponse> CheckAndMutateRow(
        CheckAndMutateRowRequest request,
        ServerCallContext context)
    {
        var tableName = ExtractTableName(request.TableName);
        RejectAuthorizedView(request.AuthorizedViewName);
        var table = _store.GetTable(tableName);

        // Create predicate evaluator from the filter
        Func<RowData, bool>? predicateFilter = null;
        if (request.PredicateFilter != null)
        {
            var filter = request.PredicateFilter;
            predicateFilter = row =>
            {
                var cells = row.GetCells();
                return RowFilterEvaluator.Matches(filter, cells, row.Key);
            };
        }

        var matched = table.CheckAndMutateRow(
            request.RowKey,
            predicateFilter,
            request.TrueMutations,
            request.FalseMutations);

        AddTrailingMetadata(context);
        return Task.FromResult(new CheckAndMutateRowResponse
        {
            PredicateMatched = matched,
        });
    }

    /// <summary>
    /// Atomic read-modify-write.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readmodifywriterowrequest
    /// </summary>
    public override Task<ReadModifyWriteRowResponse> ReadModifyWriteRow(
        ReadModifyWriteRowRequest request,
        ServerCallContext context)
    {
        var tableName = ExtractTableName(request.TableName);
        RejectAuthorizedView(request.AuthorizedViewName);
        var table = _store.GetTable(tableName);

        var modifiedCells = table.ReadModifyWriteRow(request.RowKey, request.Rules);

        // Build response row
        var row = new Row { Key = request.RowKey };
        var familyGroups = modifiedCells.GroupBy(c => c.Family);
        foreach (var familyGroup in familyGroups)
        {
            var family = new Family { Name = familyGroup.Key };
            var columnGroups = familyGroup.GroupBy(c => c.Qualifier, ByteStringEqualityComparer.Instance);
            foreach (var colGroup in columnGroups)
            {
                var column = new Column { Qualifier = colGroup.Key };
                foreach (var cell in colGroup.OrderByDescending(c => c.TimestampMicros))
                {
                    column.Cells.Add(new Cell
                    {
                        TimestampMicros = cell.TimestampMicros,
                        Value = cell.Value,
                    });
                }
                family.Columns.Add(column);
            }
            row.Families.Add(family);
        }
AddTrailingMetadata(context);
        
        return Task.FromResult(new ReadModifyWriteRowResponse { Row = row });
    }

    /// <summary>
    /// No-op stub — exists so the SDK's connection warming doesn't throw.
    /// Ref: Phase 4-SDK plan item 8: "Trivial no-op stub"
    /// </summary>
    public override Task<PingAndWarmResponse> PingAndWarm(
        PingAndWarmRequest request,
        ServerCallContext context)
    {AddTrailingMetadata(context);
        
        return Task.FromResult(new PingAndWarmResponse());
    }

    // Note: PrepareQuery RPC is defined in the Bigtable V2 proto
    // (https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#preparequeryrequest)
    // but the types are not yet exposed in Google.Cloud.Bigtable.V2 NuGet v3.15.0.
    // Will implement when the SDK adds PrepareQueryRequest/PrepareQueryResponse types.

    /// <summary>
    /// Streams change feed entries for a table.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readchangestreamrequest
    ///
    /// Note: The Go emulator does NOT support ReadChangeStream (it panics).
    /// Tests for this are tagged InMemoryOnly or GcpOnly.
    /// </summary>
    public override async Task ReadChangeStream(
        ReadChangeStreamRequest request,
        IServerStreamWriter<ReadChangeStreamResponse> responseStream,
        ServerCallContext context)
    {
        var tableName = ExtractTableName(request.TableName);
        var table = _store.GetTable(tableName);

        // Determine starting position in the mutation log
        long startSequence = 0;

        if (request.StartFromCase == ReadChangeStreamRequest.StartFromOneofCase.ContinuationTokens)
        {
            // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readchangestreamrequest
            //   "If a single token is provided, the token's partition must exactly match the
            //    request's partition. [...] Otherwise, INVALID_ARGUMENT will be returned."
            foreach (var tokenEntry in request.ContinuationTokens.Tokens)
            {
                if (tokenEntry.Partition != null && tokenEntry.Partition.RowRange != null)
                {
                    var tokenRange = tokenEntry.Partition.RowRange;
                    // The full-table partition is ["", "") — both empty ByteStrings
                    bool isFullTable = (tokenRange.StartKeyClosed.IsEmpty || tokenRange.StartKeyClosed.Length == 0)
                        && (tokenRange.EndKeyOpen.IsEmpty || tokenRange.EndKeyOpen.Length == 0);
                    if (!isFullTable)
                    {
                        throw new RpcException(new Status(StatusCode.InvalidArgument,
                            "Continuation token partition does not match the table partition. " +
                            "Expected full-table partition [\"\", \"\")."));
                    }
                }
            }

            // Resume from continuation token — token is the sequence number
            if (request.ContinuationTokens.Tokens.Count > 0)
            {
                var token = request.ContinuationTokens.Tokens[0].Token;
                if (long.TryParse(token, out var seq))
                {
                    startSequence = seq;
                }
            }
        }
        else if (request.StartFromCase == ReadChangeStreamRequest.StartFromOneofCase.StartTime)
        {
            // Find the first log entry at or after start_time
            var startTimeMicros = request.StartTime.Seconds * 1_000_000L + request.StartTime.Nanos / 1000L;
            var entries = table.GetLogEntries(0);
            startSequence = entries.Count; // default to end if no match
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].CommitTimestampMicros >= startTimeMicros)
                {
                    startSequence = entries[i].SequenceNumber;
                    break;
                }
            }
        }

        // Determine end time if specified
        long? endTimeMicros = null;
        if (request.EndTime != null && (request.EndTime.Seconds > 0 || request.EndTime.Nanos > 0))
        {
            endTimeMicros = request.EndTime.Seconds * 1_000_000L + request.EndTime.Nanos / 1000L;
        }

        // Heartbeat interval (default 5 seconds)
        var heartbeatInterval = TimeSpan.FromSeconds(5);
        if (request.HeartbeatDuration != null && request.HeartbeatDuration.Seconds > 0)
        {
            heartbeatInterval = request.HeartbeatDuration.ToTimeSpan();
        }

        // The partition covering the entire table
        var fullPartition = new StreamPartition
        {
            RowRange = new Google.Cloud.Bigtable.V2.RowRange
            {
                StartKeyClosed = ByteString.Empty,
                EndKeyOpen = ByteString.Empty,
            }
        };

        var currentSequence = startSequence;

        while (!context.CancellationToken.IsCancellationRequested)
        {
            var entries = table.GetLogEntries(currentSequence);

            if (entries.Count == 0)
            {
                // Check if we've passed end_time
                if (endTimeMicros.HasValue)
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
                    if (now >= endTimeMicros.Value)
                    {
                        // Stream ends with CloseStream
                        await responseStream.WriteAsync(new ReadChangeStreamResponse
                        {
                            CloseStream = new ReadChangeStreamResponse.Types.CloseStream
                            {
                                Status = new Google.Rpc.Status { Code = 0, Message = "OK" },
                            }
                        });
                        return;
                    }
                }

                // Send heartbeat
                var heartbeatToken = new StreamContinuationToken
                {
                    Partition = fullPartition,
                    Token = currentSequence.ToString(),
                };
                var watermark = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
                await responseStream.WriteAsync(new ReadChangeStreamResponse
                {
                    Heartbeat = new ReadChangeStreamResponse.Types.Heartbeat
                    {
                        ContinuationToken = heartbeatToken,
                        EstimatedLowWatermark = watermark,
                    }
                });

                // Wait for new entries or heartbeat interval
                var hasNew = table.WaitForLogEntries(currentSequence, heartbeatInterval, context.CancellationToken);
                if (!hasNew) continue;

                entries = table.GetLogEntries(currentSequence);
                if (entries.Count == 0) continue;
            }

            foreach (var entry in entries)
            {
                if (context.CancellationToken.IsCancellationRequested)
                    break;

                // Check end_time — stop if entry is past end
                if (endTimeMicros.HasValue && entry.CommitTimestampMicros > endTimeMicros.Value)
                {
                    await responseStream.WriteAsync(new ReadChangeStreamResponse
                    {
                        CloseStream = new ReadChangeStreamResponse.Types.CloseStream
                        {
                            Status = new Google.Rpc.Status { Code = 0, Message = "OK" },
                        }
                    });
                    return;
                }

                // Build DataChange response
                var dataChange = new ReadChangeStreamResponse.Types.DataChange
                {
                    RowKey = entry.RowKey,
                    Type = entry.ChangeType,
                    CommitTimestamp = Timestamp.FromDateTimeOffset(
                        DateTimeOffset.FromUnixTimeMilliseconds(entry.CommitTimestampMicros / 1000)),
                    Token = (entry.SequenceNumber + 1).ToString(),
                    Done = true,
                    Tiebreaker = 0,
                    EstimatedLowWatermark = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                };

                // Emit each mutation as a MutationChunk (no chunking needed — in-memory, no size pressure)
                foreach (var mutation in entry.Mutations)
                {
                    dataChange.Chunks.Add(new ReadChangeStreamResponse.Types.MutationChunk
                    {
                        Mutation = mutation,
                    });
                }

                await responseStream.WriteAsync(new ReadChangeStreamResponse
                {
                    DataChange = dataChange,
                });

                currentSequence = entry.SequenceNumber + 1;
            }
        }
    }

    /// <summary>
    /// Returns the initial set of change stream partitions for a table.
    /// For a single in-memory "tablet", returns one partition covering the entire key space.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#generateinitialchangestreampartitionsrequest
    /// </summary>
    public override async Task GenerateInitialChangeStreamPartitions(
        GenerateInitialChangeStreamPartitionsRequest request,
        IServerStreamWriter<GenerateInitialChangeStreamPartitionsResponse> responseStream,
        ServerCallContext context)
    {
        var tableName = ExtractTableName(request.TableName);
        _store.GetTable(tableName); // Verify table exists

        // Single partition covering entire key space ["", "")
        await responseStream.WriteAsync(new GenerateInitialChangeStreamPartitionsResponse
        {
            Partition = new StreamPartition
            {
                RowRange = new Google.Cloud.Bigtable.V2.RowRange
                {
                    StartKeyClosed = ByteString.Empty,
                    EndKeyOpen = ByteString.Empty,
                }
            }
        });
    }

    /// <summary>
    /// Executes a GoogleSQL query against a table. Server-streaming RPC.
    /// First response message contains ResultSetMetadata (column schema).
    /// Subsequent messages contain PartialResultSet (ProtoRowsBatch with data).
    ///
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#executequeryrequestusing
    /// </summary>
    public override async Task ExecuteQuery(
        ExecuteQueryRequest request,
        IServerStreamWriter<ExecuteQueryResponse> responseStream,
        ServerCallContext context)
    {
        _queryLog?.Record(new QueryLogEntry { Sql = request.Query });

        // Parse the SQL query
        SelectQuery query;
        try
        {
            query = GoogleSqlParser.ParseQuery(request.Query);
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Failed to parse SQL query: {ex.Message}"));
        }

        // Resolve the table — from the FROM clause or the instance context
        var tableName = query.FromTable;
        if (string.IsNullOrEmpty(tableName))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "Query must include a FROM clause specifying the table name."));
        }

        var table = _store.GetTable(tableName);

        // Execute the query
        IReadOnlyDictionary<string, Google.Cloud.Bigtable.V2.Value>? parameters = null;
        if (request.Params.Count > 0)
        {
            parameters = request.Params.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }
        var executor = new GoogleSqlExecutor(table, parameters);
        var results = executor.Execute(query);

        // Determine column names and types from the first row (or the query columns)
        var columnNames = new List<string>();
        var columnTypes = new List<Google.Cloud.Bigtable.V2.Type>();

        if (results.Count > 0)
        {
            foreach (var (key, value) in results[0])
            {
                columnNames.Add(key);
                columnTypes.Add(InferColumnType(value));
            }
        }
        else
        {
            // No results — infer column names from query columns
            foreach (var col in query.Columns)
            {
                if (col.Expression is StarExpression)
                {
                    columnNames.Add("*");
                    columnTypes.Add(new Google.Cloud.Bigtable.V2.Type { BytesType = new Google.Cloud.Bigtable.V2.Type.Types.Bytes() });
                }
                else
                {
                    var name = col.Alias ?? InferColumnName(col.Expression);
                    columnNames.Add(name);
                    columnTypes.Add(new Google.Cloud.Bigtable.V2.Type { BytesType = new Google.Cloud.Bigtable.V2.Type.Types.Bytes() });
                }
            }
        }

        // Send metadata first
        var protoSchema = new ProtoSchema();
        for (int i = 0; i < columnNames.Count; i++)
        {
            protoSchema.Columns.Add(new ColumnMetadata
            {
                Name = columnNames[i],
                Type = columnTypes[i],
            });
        }

        await responseStream.WriteAsync(new ExecuteQueryResponse
        {
            Metadata = new ResultSetMetadata
            {
                ProtoSchema = protoSchema,
            }
        });

        // Send results in batches
        if (results.Count > 0)
        {
            var protoRows = new ProtoRows();
            foreach (var row in results)
            {
                foreach (var colName in columnNames)
                {
                    row.TryGetValue(colName, out var value);
                    protoRows.Values.Add(ToProtoValue(value));
                }
            }

            var batchData = protoRows.ToByteString();
            await responseStream.WriteAsync(new ExecuteQueryResponse
            {
                Results = new PartialResultSet
                {
                    ProtoRowsBatch = new ProtoRowsBatch
                    {
                        BatchData = batchData,
                    },
                    EstimatedBatchSize = batchData.Length,
                }
            });
        }
    }

    private static Google.Cloud.Bigtable.V2.Type InferColumnType(object? value) => value switch
    {
        long => new Google.Cloud.Bigtable.V2.Type { Int64Type = new Google.Cloud.Bigtable.V2.Type.Types.Int64() },
        double => new Google.Cloud.Bigtable.V2.Type { Float64Type = new Google.Cloud.Bigtable.V2.Type.Types.Float64() },
        float => new Google.Cloud.Bigtable.V2.Type { Float32Type = new Google.Cloud.Bigtable.V2.Type.Types.Float32() },
        bool => new Google.Cloud.Bigtable.V2.Type { BoolType = new Google.Cloud.Bigtable.V2.Type.Types.Bool() },
        string => new Google.Cloud.Bigtable.V2.Type { StringType = new Google.Cloud.Bigtable.V2.Type.Types.String() },
        byte[] => new Google.Cloud.Bigtable.V2.Type { BytesType = new Google.Cloud.Bigtable.V2.Type.Types.Bytes() },
        Dictionary<string, byte[]> => new Google.Cloud.Bigtable.V2.Type
        {
            MapType = new Google.Cloud.Bigtable.V2.Type.Types.Map
            {
                KeyType = new Google.Cloud.Bigtable.V2.Type { BytesType = new Google.Cloud.Bigtable.V2.Type.Types.Bytes() },
                ValueType = new Google.Cloud.Bigtable.V2.Type { BytesType = new Google.Cloud.Bigtable.V2.Type.Types.Bytes() },
            },
        },
        _ => new Google.Cloud.Bigtable.V2.Type { BytesType = new Google.Cloud.Bigtable.V2.Type.Types.Bytes() },
    };

    private static Google.Cloud.Bigtable.V2.Value ToProtoValue(object? value) => value switch
    {
        null => new Google.Cloud.Bigtable.V2.Value(),
        long l => new Google.Cloud.Bigtable.V2.Value { IntValue = l },
        double d => new Google.Cloud.Bigtable.V2.Value { FloatValue = d },
        float f => new Google.Cloud.Bigtable.V2.Value { FloatValue = f },
        bool b => new Google.Cloud.Bigtable.V2.Value { BoolValue = b },
        string s => new Google.Cloud.Bigtable.V2.Value { StringValue = s },
        byte[] bytes => new Google.Cloud.Bigtable.V2.Value { BytesValue = ByteString.CopyFrom(bytes) },
        _ => new Google.Cloud.Bigtable.V2.Value { StringValue = value.ToString() ?? "" },
    };

    private static string InferColumnName(SqlExpression expr) => expr switch
    {
        ColumnRefExpression col => col.Name,
        FunctionCallExpression func => func.Name,
        MapSubscriptExpression => "value",
        CastExpression cast => InferColumnName(cast.Operand),
        _ => "expr",
    };

    /// <summary>
    /// Extracts the short table name from a fully-qualified resource name.
    /// Delegates to shared Superpower-based ResourceNameParser.
    /// </summary>
    private static string ExtractTableName(string resourceName)
        => ResourceNameParser.ExtractTableName(resourceName);
}

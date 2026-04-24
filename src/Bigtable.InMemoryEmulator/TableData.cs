using System.Buffers.Binary;
using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace Bigtable.InMemoryEmulator;

/// <summary>
/// Per-table storage and mutation engine.
/// Uses SortedDictionary for lexicographic row key ordering (required for range scans).
/// Thread-safe via ReaderWriterLockSlim (table-level) + per-row locks.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2
/// </summary>
internal sealed class TableData : IDisposable
{
    private readonly SortedDictionary<ByteString, RowData> _rows;
    private readonly ReaderWriterLockSlim _rwLock = new();

    // Append-only mutation log for ReadChangeStream.
    // Protected by its own lock (independent of _rwLock) for minimal contention.
    private readonly List<MutationLogEntry> _mutationLog = new();
    private readonly object _logLock = new();
    private long _logSequence;

    public TableConfig Config { get; }

    public TableData(TableConfig config)
    {
        Config = config;
        _rows = new SortedDictionary<ByteString, RowData>(ByteStringComparer.Instance);
    }

    /// <summary>
    /// Number of rows in the table.
    /// </summary>
    public int RowCount
    {
        get
        {
            _rwLock.EnterReadLock();
            try
            {
                return _rows.Count;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }
    }

    /// <summary>
    /// Gets or creates a row for the specified key. Holds write lock if creating.
    /// Returns the row under read lock still held — caller must NOT call this inside a write lock.
    /// </summary>
    private RowData GetOrCreateRow(ByteString key)
    {
        // First try under read lock
        _rwLock.EnterReadLock();
        try
        {
            if (_rows.TryGetValue(key, out var existing))
            {
                return existing;
            }
        }
        finally
        {
            _rwLock.ExitReadLock();
        }

        // Need to create — upgrade to write lock
        _rwLock.EnterWriteLock();
        try
        {
            // Double-check after acquiring write lock
            if (_rows.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var row = new RowData { Key = key };
            _rows[key] = row;
            return row;
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Applies a list of mutations atomically to a single row.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowrequest
    ///   "Mutates a row atomically."
    /// </summary>
    public void MutateRow(ByteString rowKey, IEnumerable<Google.Cloud.Bigtable.V2.Mutation> mutations)
    {
        ValidateRowKey(rowKey);
        var mutationList = mutations.ToList();
        ValidateMutations(mutationList);

        var row = GetOrCreateRow(rowKey);
        lock (row.Lock)
        {
            foreach (var mutation in mutationList)
            {
                ApplyMutation(row, mutation);
            }
            ValidateRowSize(row);
        }

        CleanupEmptyRow(rowKey);

        // Record in mutation log for change feed
        AppendToLog(rowKey, mutationList);
    }

    /// <summary>
    /// Applies mutations to multiple rows. Each entry is independent — failures are per-entry.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowsrequest
    ///   "Mutates multiple rows in a batch."
    /// Returns a list of (index, status) for each entry.
    /// </summary>
    public List<(int Index, Status Status)> MutateRows(
        IList<Google.Cloud.Bigtable.V2.MutateRowsRequest.Types.Entry> entries)
    {
        var results = new List<(int Index, Status Status)>();

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            try
            {
                ValidateRowKey(entry.RowKey);
                var mutationList = entry.Mutations.ToList();
                ValidateMutations(mutationList);

                var row = GetOrCreateRow(entry.RowKey);
                lock (row.Lock)
                {
                    foreach (var mutation in mutationList)
                    {
                        ApplyMutation(row, mutation);
                    }
                }

                CleanupEmptyRow(entry.RowKey);
                results.Add((i, Status.DefaultSuccess));

                // Record in mutation log for change feed
                AppendToLog(entry.RowKey, mutationList);
            }
            catch (RpcException ex)
            {
                results.Add((i, ex.Status));
            }
        }

        return results;
    }

    /// <summary>
    /// Atomic compare-and-swap: evaluate predicate filter, apply true or false mutations.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#checkandmutaterowrequest
    ///   "Mutates a row atomically based on the output of a predicate Reader filter."
    /// Returns true if the predicate matched (true_mutations applied).
    /// </summary>
    public bool CheckAndMutateRow(
        ByteString rowKey,
        Func<RowData, bool>? predicateFilter,
        IEnumerable<Google.Cloud.Bigtable.V2.Mutation>? trueMutations,
        IEnumerable<Google.Cloud.Bigtable.V2.Mutation>? falseMutations)
    {
        ValidateRowKey(rowKey);

        var row = GetOrCreateRow(rowKey);
        lock (row.Lock)
        {
            bool predicateMatched = predicateFilter?.Invoke(row) ?? false;
            var mutations = predicateMatched
                ? trueMutations?.ToList() ?? []
                : falseMutations?.ToList() ?? [];

            foreach (var mutation in mutations)
            {
                ApplyMutation(row, mutation);
            }

            CleanupEmptyRow(rowKey);

            // Record in mutation log for change feed (only if mutations were applied)
            if (mutations.Count > 0)
            {
                AppendToLog(rowKey, mutations);
            }

            return predicateMatched;
        }
    }

    /// <summary>
    /// Atomic read-modify-write: reads the latest cell value, applies the rule, writes back.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readmodifywriterowrequest
    ///   "Modifies a row atomically on the server."
    /// Returns the modified row data.
    /// </summary>
    public IReadOnlyList<CellData> ReadModifyWriteRow(
        ByteString rowKey,
        IEnumerable<Google.Cloud.Bigtable.V2.ReadModifyWriteRule> rules)
    {
        ValidateRowKey(rowKey);
        var ruleList = rules.ToList();
        if (ruleList.Count == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "ReadModifyWriteRow requires at least one rule."));
        }

        var row = GetOrCreateRow(rowKey);
        var modifiedCells = new List<CellData>();

        lock (row.Lock)
        {
            var now = DateTimeOffset.UtcNow;
            // Ref: Timestamp is server-assigned, rounded to milliseconds
            var serverTimestamp = (now.ToUnixTimeMilliseconds()) * 1000;
            var syntheticMutations = new List<Google.Cloud.Bigtable.V2.Mutation>();

            foreach (var rule in ruleList)
            {
                var family = rule.FamilyName;
                var qualifier = rule.ColumnQualifier;

                ValidateFamilyExists(family);

                // Get existing cells for this column, sorted by timestamp descending
                var existingCells = row.GetCellsForColumn(family, qualifier);
                var latestCell = existingCells.FirstOrDefault();

                // Determine new timestamp
                // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readmodifywriterule
                //   "The new value for the timestamp is max of the existing timestamp, and the current server time."
                var newTimestamp = latestCell != null
                    ? Math.Max(latestCell.TimestampMicros, serverTimestamp)
                    : serverTimestamp;

                ByteString newValue;
                switch (rule.RuleCase)
                {
                    case Google.Cloud.Bigtable.V2.ReadModifyWriteRule.RuleOneofCase.IncrementAmount:
                        // Ref: "Rule specifying that `increment_amount` be added to the existing value.
                        //   If the targeted cell is unset, it is treated as containing a zero."
                        var existingInt = latestCell != null ? ReadBigEndianInt64(latestCell.Value) : 0;
                        var incrementedValue = existingInt + rule.IncrementAmount;
                        newValue = WriteBigEndianInt64(incrementedValue);
                        break;

                    case Google.Cloud.Bigtable.V2.ReadModifyWriteRule.RuleOneofCase.AppendValue:
                        // Ref: "Rule specifying that `append_value` be appended to the existing value."
                        var existingBytes = latestCell?.Value ?? ByteString.Empty;
                        newValue = ByteString.CopyFrom(
                            existingBytes.Span.ToArray().Concat(rule.AppendValue.Span.ToArray()).ToArray());
                        break;

                    default:
                        throw new RpcException(new Status(StatusCode.InvalidArgument,
                            "ReadModifyWriteRule must specify either increment_amount or append_value."));
                }

                row.SetCell(family, qualifier, newTimestamp, newValue);
                modifiedCells.Add(new CellData
                {
                    Family = family,
                    Qualifier = qualifier,
                    TimestampMicros = newTimestamp,
                    Value = newValue
                });

                // Build a synthetic SetCell mutation for the log
                syntheticMutations.Add(new Google.Cloud.Bigtable.V2.Mutation
                {
                    SetCell = new Google.Cloud.Bigtable.V2.Mutation.Types.SetCell
                    {
                        FamilyName = family,
                        ColumnQualifier = qualifier,
                        TimestampMicros = newTimestamp,
                        Value = newValue,
                    }
                });
            }

            // Record in mutation log for change feed
            if (syntheticMutations.Count > 0)
            {
                AppendToLog(rowKey, syntheticMutations);
            }
        }

        return modifiedCells;
    }

    /// <summary>
    /// Reads rows matching the specified criteria.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
    /// </summary>
    public IEnumerable<RowData> ReadRows(
        IReadOnlyList<ByteString>? rowKeys = null,
        IReadOnlyList<RowRange>? rowRanges = null,
        long rowsLimit = 0,
        bool reversed = false)
    {
        _rwLock.EnterReadLock();
        try
        {
            IEnumerable<KeyValuePair<ByteString, RowData>> rows = _rows;

            if (reversed)
            {
                // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
                //   "Return rows in lexicographical descending order of the row keys."
                rows = _rows.Reverse();
            }

            // Filter by row keys and/or ranges
            if (rowKeys != null || rowRanges != null)
            {
                var keySet = rowKeys != null ? new HashSet<ByteString>(rowKeys, ByteStringEqualityComparer.Instance) : null;

                rows = rows.Where(kvp =>
                {
                    if (keySet != null && keySet.Contains(kvp.Key))
                        return true;

                    if (rowRanges != null)
                    {
                        foreach (var range in rowRanges)
                        {
                            if (IsKeyInRange(kvp.Key, range, reversed))
                                return true;
                        }
                    }

                    // If no keys and no ranges specified, include all rows
                    return keySet == null && rowRanges == null;
                });
            }

            // Apply rows_limit
            if (rowsLimit > 0)
            {
                rows = rows.Take((int)rowsLimit);
            }

            // Only return non-empty rows
            foreach (var kvp in rows)
            {
                if (!kvp.Value.IsEmpty)
                {
                    yield return kvp.Value;
                }
            }
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets a single row by key. Returns null if not found or empty.
    /// </summary>
    public RowData? GetRow(ByteString rowKey)
    {
        _rwLock.EnterReadLock();
        try
        {
            if (_rows.TryGetValue(rowKey, out var row) && !row.IsEmpty)
            {
                return row;
            }
            return null;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Clears all rows from the table.
    /// </summary>
    public void ClearRows()
    {
        _rwLock.EnterWriteLock();
        try
        {
            _rows.Clear();
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Applies a single mutation to a row. Must be called while holding the row lock.
    /// </summary>
    private void ApplyMutation(RowData row, Google.Cloud.Bigtable.V2.Mutation mutation)
    {
        switch (mutation.MutationCase)
        {
            case Google.Cloud.Bigtable.V2.Mutation.MutationOneofCase.SetCell:
                ApplySetCell(row, mutation.SetCell);
                break;

            case Google.Cloud.Bigtable.V2.Mutation.MutationOneofCase.DeleteFromColumn:
                ApplyDeleteFromColumn(row, mutation.DeleteFromColumn);
                break;

            case Google.Cloud.Bigtable.V2.Mutation.MutationOneofCase.DeleteFromFamily:
                ApplyDeleteFromFamily(row, mutation.DeleteFromFamily);
                break;

            case Google.Cloud.Bigtable.V2.Mutation.MutationOneofCase.DeleteFromRow:
                row.DeleteAllCells();
                break;

            case Google.Cloud.Bigtable.V2.Mutation.MutationOneofCase.AddToCell:
                ApplyAddToCell(row, mutation.AddToCell);
                break;

            case Google.Cloud.Bigtable.V2.Mutation.MutationOneofCase.MergeToCell:
                ApplyMergeToCell(row, mutation.MergeToCell);
                break;

            default:
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Unsupported mutation type: {mutation.MutationCase}"));
        }
    }

    /// <summary>
    /// Applies a SetCell mutation.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
    ///   "A Mutation which sets the value of the specified cell."
    /// </summary>
    private void ApplySetCell(RowData row, Google.Cloud.Bigtable.V2.Mutation.Types.SetCell setCell)
    {
        var family = setCell.FamilyName;
        ValidateFamilyExists(family);

        // Ref: Regular SetCell mutations to an Aggregate family are rejected with INVALID_ARGUMENT
        if (Config.IsAggregateFamily(family))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Cannot use SetCell on aggregate family '{family}'. Use AddToCell or MergeToCell instead."));
        }

        ValidateColumnQualifier(setCell.ColumnQualifier);
        ValidateCellValue(setCell.Value);

        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
        //   "timestamp_micros: -1 means server-assigned timestamp. Must be >= -1."
        //   "The timestamp must be microsecond-aligned (i.e., timestamp_micros % 1000 == 0 for
        //    tables that have `MILLIS` granularity)."
        long timestampMicros = setCell.TimestampMicros;
        if (timestampMicros == -1)
        {
            // Server-assigned: current time rounded to milliseconds
            timestampMicros = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        }
        else
        {
            if (timestampMicros < -1)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    "timestamp_micros must be >= -1."));
            }

            // Ref: timestamp must be ms-aligned for MILLIS granularity tables
            if (timestampMicros % 1000 != 0)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"timestamp_micros ({timestampMicros}) is not millisecond-aligned."));
            }
        }

        row.SetCell(family, setCell.ColumnQualifier, timestampMicros, setCell.Value);

        // Apply GC rules (eager eviction for MaxVersions)
        ApplyGcRulesForColumn(row, family, setCell.ColumnQualifier);
    }

    /// <summary>
    /// Applies a DeleteFromColumn mutation.
    /// </summary>
    private void ApplyDeleteFromColumn(RowData row, Google.Cloud.Bigtable.V2.Mutation.Types.DeleteFromColumn deleteFromColumn)
    {
        var family = deleteFromColumn.FamilyName;
        ValidateFamilyExists(family);

        long? startTs = null;
        long? endTs = null;

        if (deleteFromColumn.TimeRange != null)
        {
            // Ref: TimestampRange: start_timestamp_micros is inclusive, end_timestamp_micros is exclusive
            if (deleteFromColumn.TimeRange.StartTimestampMicros != 0)
                startTs = deleteFromColumn.TimeRange.StartTimestampMicros;
            if (deleteFromColumn.TimeRange.EndTimestampMicros != 0)
                endTs = deleteFromColumn.TimeRange.EndTimestampMicros;
        }

        row.DeleteFromColumn(family, deleteFromColumn.ColumnQualifier, startTs, endTs);
    }

    /// <summary>
    /// Applies a DeleteFromFamily mutation.
    /// </summary>
    private void ApplyDeleteFromFamily(RowData row, Google.Cloud.Bigtable.V2.Mutation.Types.DeleteFromFamily deleteFromFamily)
    {
        ValidateFamilyExists(deleteFromFamily.FamilyName);
        row.DeleteFromFamily(deleteFromFamily.FamilyName);
    }

    /// <summary>
    /// Applies an AddToCell mutation for aggregate families.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
    ///   "Incrementally updates a cell in an Aggregate family."
    /// Semantics: merge the input into the accumulated state for the cell.
    /// </summary>
    private void ApplyAddToCell(RowData row, Google.Cloud.Bigtable.V2.Mutation.Types.AddToCell addToCell)
    {
        var family = addToCell.FamilyName;
        ValidateFamilyExists(family);

        // Ref: "INVALID_ARGUMENT if family is not an Aggregate family"
        var aggConfig = Config.GetAggregateConfig(family);
        if (aggConfig == null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Cannot use AddToCell on non-aggregate family '{family}'. Use SetCell instead."));
        }

        // Ref: HyperLogLogPlusPlusUniqueCount deferred — return UNIMPLEMENTED
        if (aggConfig.Aggregator == AggregatorType.HllppUniqueCount)
        {
            throw new RpcException(new Status(StatusCode.Unimplemented,
                "HyperLogLogPlusPlusUniqueCount aggregator is not yet implemented."));
        }

        var qualifier = ExtractRawBytes(addToCell.ColumnQualifier);
        var timestamp = ExtractTimestamp(addToCell.Timestamp);
        var inputValue = ExtractInt64Input(addToCell.Input);

        ApplyAggregation(row, family, qualifier, timestamp, inputValue, aggConfig);
    }

    /// <summary>
    /// Applies a MergeToCell mutation for aggregate families.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
    ///   "Merges accumulated state to an Aggregate cell."
    /// Semantics: same as AddToCell — merge pre-computed state into the cell.
    /// </summary>
    private void ApplyMergeToCell(RowData row, Google.Cloud.Bigtable.V2.Mutation.Types.MergeToCell mergeToCell)
    {
        var family = mergeToCell.FamilyName;
        ValidateFamilyExists(family);

        // Ref: "INVALID_ARGUMENT if family is not an Aggregate family"
        var aggConfig = Config.GetAggregateConfig(family);
        if (aggConfig == null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Cannot use MergeToCell on non-aggregate family '{family}'. Use SetCell instead."));
        }

        if (aggConfig.Aggregator == AggregatorType.HllppUniqueCount)
        {
            throw new RpcException(new Status(StatusCode.Unimplemented,
                "HyperLogLogPlusPlusUniqueCount aggregator is not yet implemented."));
        }

        var qualifier = ExtractRawBytes(mergeToCell.ColumnQualifier);
        var timestamp = ExtractTimestamp(mergeToCell.Timestamp);
        var inputValue = ExtractInt64Input(mergeToCell.Input);

        ApplyAggregation(row, family, qualifier, timestamp, inputValue, aggConfig);
    }

    /// <summary>
    /// Applies aggregation logic to a cell. Reads existing value, applies the aggregator, writes back.
    /// </summary>
    private void ApplyAggregation(RowData row, string family, ByteString qualifier,
        long timestampMicros, long inputValue, AggregateConfig aggConfig)
    {
        var existingCells = row.GetCellsForColumn(family, qualifier)
            .Where(c => c.TimestampMicros == timestampMicros)
            .ToList();

        long newValue;
        if (existingCells.Count == 0)
        {
            // No prior state — initialize with input
            newValue = inputValue;
        }
        else
        {
            var existingValue = ReadBigEndianInt64(existingCells[0].Value);
            newValue = aggConfig.Aggregator switch
            {
                AggregatorType.Sum => existingValue + inputValue,
                AggregatorType.Min => Math.Min(existingValue, inputValue),
                AggregatorType.Max => Math.Max(existingValue, inputValue),
                _ => throw new RpcException(new Status(StatusCode.Unimplemented,
                    $"Aggregator type '{aggConfig.Aggregator}' is not supported.")),
            };
        }

        row.SetCell(family, qualifier, timestampMicros, WriteBigEndianInt64(newValue));
    }

    /// <summary>
    /// Extracts raw bytes from a Value proto (used by AddToCell/MergeToCell for qualifier).
    /// </summary>
    private static ByteString ExtractRawBytes(Google.Cloud.Bigtable.V2.Value? value)
    {
        if (value == null) return ByteString.Empty;
        return value.KindCase switch
        {
            Google.Cloud.Bigtable.V2.Value.KindOneofCase.RawValue => value.RawValue,
            Google.Cloud.Bigtable.V2.Value.KindOneofCase.BytesValue => value.BytesValue,
            Google.Cloud.Bigtable.V2.Value.KindOneofCase.StringValue => ByteString.CopyFromUtf8(value.StringValue),
            _ => ByteString.Empty,
        };
    }

    /// <summary>
    /// Extracts a timestamp from a Value proto (used by AddToCell/MergeToCell).
    /// RawTimestampMicros is the expected kind. 0 = server-assigned.
    /// </summary>
    private static long ExtractTimestamp(Google.Cloud.Bigtable.V2.Value? value)
    {
        if (value == null) return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

        if (value.KindCase == Google.Cloud.Bigtable.V2.Value.KindOneofCase.RawTimestampMicros)
        {
            if (value.RawTimestampMicros == 0)
            {
                // Server-assigned timestamp
                return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
            }
            return value.RawTimestampMicros;
        }

        // Default to server-assigned
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
    }

    /// <summary>
    /// Extracts an Int64 value from a Value proto (used by AddToCell/MergeToCell input).
    /// </summary>
    private static long ExtractInt64Input(Google.Cloud.Bigtable.V2.Value? value)
    {
        if (value == null) return 0;
        return value.KindCase switch
        {
            Google.Cloud.Bigtable.V2.Value.KindOneofCase.IntValue => value.IntValue,
            Google.Cloud.Bigtable.V2.Value.KindOneofCase.RawValue when value.RawValue.Length >= 8 =>
                ReadBigEndianInt64(value.RawValue),
            _ => 0,
        };
    }

    /// <summary>
    /// Applies GC rules for a specific column after a SetCell.
    /// Currently supports MaxNumVersions (eager eviction).
    /// </summary>
    private void ApplyGcRulesForColumn(RowData row, string family, ByteString qualifier)
    {
        if (!Config.ColumnFamilies.TryGetValue(family, out var gcRule) || gcRule == null)
            return;

        ApplyGcRule(row, family, qualifier, gcRule);
    }

    private void ApplyGcRule(RowData row, string family, ByteString qualifier, GcRule gcRule)
    {
        switch (gcRule.RuleCase)
        {
            case GcRule.RuleOneofCase.MaxNumVersions:
                var cells = row.GetCellsForColumn(family, qualifier);
                if (cells.Count > gcRule.MaxNumVersions)
                {
                    // Keep only the newest N versions, delete older ones
                    var toDelete = cells.Skip(gcRule.MaxNumVersions).ToList();
                    foreach (var cell in toDelete)
                    {
                        row.DeleteFromColumn(family, qualifier, cell.TimestampMicros, cell.TimestampMicros + 1);
                    }
                }
                break;

            case GcRule.RuleOneofCase.MaxAge:
                var maxAge = gcRule.MaxAge.ToTimeSpan();
                var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000 - (long)maxAge.TotalMicroseconds;
                row.DeleteFromColumn(family, qualifier, null, cutoff);
                break;

            case GcRule.RuleOneofCase.Intersection:
                // Intersection: delete only when ALL rules agree
                // For simplicity, apply each rule and intersect
                // In practice, this is complex — for now, just apply all rules
                foreach (var subRule in gcRule.Intersection.Rules)
                {
                    ApplyGcRule(row, family, qualifier, subRule);
                }
                break;

            case GcRule.RuleOneofCase.Union:
                // Union: delete when ANY rule triggers
                foreach (var subRule in gcRule.Union.Rules)
                {
                    ApplyGcRule(row, family, qualifier, subRule);
                }
                break;
        }
    }

    /// <summary>
    /// Removes empty rows from the dictionary.
    /// </summary>
    private void CleanupEmptyRow(ByteString rowKey)
    {
        _rwLock.EnterReadLock();
        try
        {
            if (_rows.TryGetValue(rowKey, out var row) && row.IsEmpty)
            {
                _rwLock.ExitReadLock();
                _rwLock.EnterWriteLock();
                try
                {
                    // Double-check under write lock
                    if (_rows.TryGetValue(rowKey, out row) && row.IsEmpty)
                    {
                        _rows.Remove(rowKey);
                    }
                }
                finally
                {
                    _rwLock.ExitWriteLock();
                }
                return;
            }
        }
        finally
        {
            if (_rwLock.IsReadLockHeld)
                _rwLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Validates a row key.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#row
    ///   "Row key: up to 4KiB in length"
    /// </summary>
    private static void ValidateRowKey(ByteString rowKey)
    {
        if (rowKey == null || rowKey.IsEmpty)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "Row key must not be empty."));
        }

        // Ref: Row.key doc: "up to 4KiB in length"
        if (rowKey.Length > 4096)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Row key exceeds maximum length of 4096 bytes (got {rowKey.Length})."));
        }
    }

    /// <summary>
    /// Validates a column qualifier.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#column
    ///   "qualifier: up to 16kiB in length"
    /// </summary>
    private static void ValidateColumnQualifier(ByteString qualifier)
    {
        // Ref: Column.qualifier doc: "up to 16kiB in length"
        if (qualifier != null && qualifier.Length > 16384)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Column qualifier exceeds maximum length of 16384 bytes (got {qualifier.Length})."));
        }
    }

    /// <summary>
    /// Validates a cell value.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#cell
    ///   "value: up to 100MiB in length"
    /// </summary>
    private static void ValidateCellValue(ByteString value)
    {
        // Ref: Cell.value doc: "up to 100MiB in length"
        if (value != null && value.Length > 100 * 1024 * 1024)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Cell value exceeds maximum length of 100 MiB (got {value.Length})."));
        }
    }

    /// <summary>
    /// Validates that a row does not exceed the 256 MiB total size limit.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#row
    ///   "Rows which exceed 256MiB in size cannot be read in full."
    /// </summary>
    private static void ValidateRowSize(RowData row)
    {
        long totalSize = row.Key.Length;
        foreach (var cell in row.GetCells())
        {
            totalSize += cell.Family.Length + cell.Qualifier.Length + 8 /* timestamp */ + cell.Value.Length;
            if (totalSize > 256L * 1024 * 1024)
            {
                throw new RpcException(new Status(StatusCode.ResourceExhausted,
                    $"Row exceeds maximum size of 256 MiB."));
            }
        }
    }

    /// <summary>
    /// Validates that a mutation set is within bounds.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowrequest
    ///   "mutations: Must contain at least one entry and at most 100000."
    /// </summary>
    private static void ValidateMutations(IList<Google.Cloud.Bigtable.V2.Mutation> mutations)
    {
        if (mutations.Count == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "Mutations list must contain at least one entry."));
        }

        if (mutations.Count > 100_000)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Mutations list exceeds maximum of 100,000 entries (got {mutations.Count})."));
        }
    }

    /// <summary>
    /// Validates that a family exists on this table.
    /// </summary>
    private void ValidateFamilyExists(string familyName)
    {
        if (!Config.HasFamily(familyName))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Column family '{familyName}' does not exist in table '{Config.Name}'."));
        }
    }

    /// <summary>
    /// Checks if a key falls within a row range.
    /// </summary>
    private static bool IsKeyInRange(ByteString key, RowRange range, bool reversed)
    {
        var cmp = ByteStringComparer.Instance;

        // Start bound
        if (range.StartKey != null && !range.StartKey.IsEmpty)
        {
            bool startInclusive = range.StartKeyCase == RowRange.StartKeyOneofCase.StartKeyClosed;
            int startCmp = cmp.Compare(key, range.StartKey);
            if (startInclusive && startCmp < 0) return false;
            if (!startInclusive && startCmp <= 0) return false;
        }

        // End bound
        if (range.EndKey != null && !range.EndKey.IsEmpty)
        {
            bool endInclusive = range.EndKeyCase == RowRange.EndKeyOneofCase.EndKeyClosed;
            int endCmp = cmp.Compare(key, range.EndKey);
            if (endInclusive && endCmp > 0) return false;
            if (!endInclusive && endCmp >= 0) return false;
        }

        return true;
    }

    /// <summary>
    /// Reads a big-endian int64 from a ByteString.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readmodifywriterule
    ///   "The value is treated as a 64-bit big-endian signed integer."
    /// </summary>
    private static long ReadBigEndianInt64(ByteString value)
    {
        if (value.IsEmpty || value.Length == 0) return 0;
        if (value.Length < 8)
        {
            // Pad with leading zeros (or 0xFF for negative, but spec says zero-extend)
            var padded = new byte[8];
            value.Span.CopyTo(padded.AsSpan(8 - value.Length));
            return BinaryPrimitives.ReadInt64BigEndian(padded);
        }
        return BinaryPrimitives.ReadInt64BigEndian(value.Span[..8]);
    }

    /// <summary>
    /// Writes a big-endian int64 to a ByteString.
    /// </summary>
    private static ByteString WriteBigEndianInt64(long value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return ByteString.CopyFrom(bytes);
    }

    public void Dispose()
    {
        _rwLock.Dispose();
    }

    /// <summary>
    /// Appends a mutation log entry for ReadChangeStream.
    /// Thread-safe via dedicated _logLock.
    /// </summary>
    private void AppendToLog(
        ByteString rowKey,
        IReadOnlyList<Google.Cloud.Bigtable.V2.Mutation> mutations,
        ReadChangeStreamResponse.Types.DataChange.Types.Type changeType
            = ReadChangeStreamResponse.Types.DataChange.Types.Type.User)
    {
        lock (_logLock)
        {
            _mutationLog.Add(new MutationLogEntry
            {
                SequenceNumber = _logSequence++,
                RowKey = rowKey,
                Mutations = mutations,
                CommitTimestampMicros = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000,
                ChangeType = changeType,
            });

            // Wake any waiting change stream readers
            Monitor.PulseAll(_logLock);
        }
    }

    /// <summary>
    /// Returns mutation log entries starting from the given sequence number.
    /// Used by ReadChangeStream to consume changes.
    /// </summary>
    internal IReadOnlyList<MutationLogEntry> GetLogEntries(long fromSequence)
    {
        lock (_logLock)
        {
            if (fromSequence >= _mutationLog.Count)
                return Array.Empty<MutationLogEntry>();

            return _mutationLog.Skip((int)fromSequence).ToList();
        }
    }

    /// <summary>
    /// Returns the current log sequence number (one past the last entry).
    /// </summary>
    internal long CurrentLogSequence
    {
        get
        {
            lock (_logLock)
            {
                return _logSequence;
            }
        }
    }

    /// <summary>
    /// Waits for new log entries to appear after the given sequence.
    /// Returns true if new entries are available, false if cancelled/timed out.
    /// </summary>
    internal bool WaitForLogEntries(long afterSequence, TimeSpan timeout, CancellationToken cancellationToken)
    {
        lock (_logLock)
        {
            if (_logSequence > afterSequence)
                return true;

            // Wait with timeout + cancellation
            var deadline = DateTime.UtcNow + timeout;
            while (_logSequence <= afterSequence)
            {
                if (cancellationToken.IsCancellationRequested)
                    return false;

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    return false;

                Monitor.Wait(_logLock, remaining);
            }
            return true;
        }
    }
}

/// <summary>
/// Represents a row key range for ReadRows queries.
/// Wraps the protobuf RowRange type for easier construction.
/// </summary>
internal sealed class RowRange
{
    public ByteString? StartKey { get; init; }
    public StartKeyOneofCase StartKeyCase { get; init; }
    public ByteString? EndKey { get; init; }
    public EndKeyOneofCase EndKeyCase { get; init; }

    public enum StartKeyOneofCase
    {
        None = 0,
        StartKeyClosed = 1,
        StartKeyOpen = 2,
    }

    public enum EndKeyOneofCase
    {
        None = 0,
        EndKeyClosed = 1,
        EndKeyOpen = 2,
    }

    public static RowRange FromProto(Google.Cloud.Bigtable.V2.RowRange proto)
    {
        ByteString? startKey = null;
        StartKeyOneofCase startCase = StartKeyOneofCase.None;
        ByteString? endKey = null;
        EndKeyOneofCase endCase = EndKeyOneofCase.None;

        switch (proto.StartKeyCase)
        {
            case Google.Cloud.Bigtable.V2.RowRange.StartKeyOneofCase.StartKeyClosed:
                startKey = proto.StartKeyClosed;
                startCase = StartKeyOneofCase.StartKeyClosed;
                break;
            case Google.Cloud.Bigtable.V2.RowRange.StartKeyOneofCase.StartKeyOpen:
                startKey = proto.StartKeyOpen;
                startCase = StartKeyOneofCase.StartKeyOpen;
                break;
        }

        switch (proto.EndKeyCase)
        {
            case Google.Cloud.Bigtable.V2.RowRange.EndKeyOneofCase.EndKeyClosed:
                endKey = proto.EndKeyClosed;
                endCase = EndKeyOneofCase.EndKeyClosed;
                break;
            case Google.Cloud.Bigtable.V2.RowRange.EndKeyOneofCase.EndKeyOpen:
                endKey = proto.EndKeyOpen;
                endCase = EndKeyOneofCase.EndKeyOpen;
                break;
        }

        return new RowRange
        {
            StartKey = startKey,
            StartKeyCase = startCase,
            EndKey = endKey,
            EndKeyCase = endCase,
        };
    }
}

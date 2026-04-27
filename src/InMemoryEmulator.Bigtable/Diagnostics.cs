namespace InMemoryEmulator.Bigtable;

/// <summary>
/// Records gRPC requests for test inspection.
///
/// Usage:
///   var log = result.RpcLog;
///   log.Entries.Should().ContainSingle(e => e.Method.Contains("MutateRow"));
///
/// Ref: Phase 5 plan — "RpcLog / QueryLog: diagnostics/request logging"
/// </summary>
public sealed class RpcLog
{
    private readonly List<RpcLogEntry> _entries = [];
    private readonly object _lock = new();

    /// <summary>
    /// All recorded RPC entries.
    /// </summary>
    public IReadOnlyList<RpcLogEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToList();
            }
        }
    }

    /// <summary>
    /// Clears all recorded entries.
    /// </summary>
    public void Clear()
    {
        lock (_lock) { _entries.Clear(); }
    }

    internal void Record(RpcLogEntry entry)
    {
        lock (_lock) { _entries.Add(entry); }
    }
}

/// <summary>
/// A single recorded gRPC request.
/// </summary>
public sealed class RpcLogEntry
{
    /// <summary>
    /// The gRPC method name (e.g., "/google.bigtable.v2.Bigtable/MutateRow").
    /// </summary>
    public required string Method { get; init; }

    /// <summary>
    /// The table name extracted from the request, if available.
    /// </summary>
    public string? TableName { get; init; }

    /// <summary>
    /// When the request was received.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Whether the request succeeded.
    /// </summary>
    public bool Succeeded { get; init; }

    /// <summary>
    /// The gRPC status code of the response.
    /// </summary>
    public Grpc.Core.StatusCode StatusCode { get; init; }
}

/// <summary>
/// Records SQL queries executed via ExecuteQuery for test inspection.
///
/// Usage:
///   var log = result.QueryLog;
///   log.Entries.Should().ContainSingle(e => e.Sql.Contains("SELECT"));
/// </summary>
public sealed class QueryLog
{
    private readonly List<QueryLogEntry> _entries = [];
    private readonly object _lock = new();

    /// <summary>
    /// All recorded query entries.
    /// </summary>
    public IReadOnlyList<QueryLogEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToList();
            }
        }
    }

    /// <summary>
    /// Clears all recorded entries.
    /// </summary>
    public void Clear()
    {
        lock (_lock) { _entries.Clear(); }
    }

    internal void Record(QueryLogEntry entry)
    {
        lock (_lock) { _entries.Add(entry); }
    }
}

/// <summary>
/// A single recorded SQL query.
/// </summary>
public sealed class QueryLogEntry
{
    /// <summary>
    /// The SQL query string.
    /// </summary>
    public required string Sql { get; init; }

    /// <summary>
    /// When the query was executed.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Number of result rows returned.
    /// </summary>
    public int ResultCount { get; init; }
}

using Grpc.Core;

namespace InMemoryEmulator.Bigtable;

/// <summary>
/// Context passed to the fault injector delegate.
/// Contains information about the incoming gRPC request.
/// </summary>
public sealed class FaultContext
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
    /// The row key from the request, if applicable (hex-encoded).
    /// </summary>
    public string? RowKey { get; init; }

    /// <summary>
    /// Number of times this RPC has been attempted (starts at 1).
    /// </summary>
    public int AttemptNumber { get; init; } = 1;
}

/// <summary>
/// Holds the fault injection delegate and invocation tracking for the gRPC service.
/// Thread-safe.
///
/// Usage:
///   var injector = new FaultInjector();
///   injector.SetFault(ctx => ctx.Method.Contains("MutateRow")
///       ? new Status(StatusCode.Unavailable, "Simulated error")
///       : null);
///
/// Ref: Phase 5 plan — "Fault injection (Layer 2/3 only)"
/// </summary>
public sealed class FaultInjector
{
    private volatile Func<FaultContext, Status?>? _faultFunc;

    /// <summary>
    /// Sets a fault injection function. Return a non-null Status to inject that error.
    /// Return null to allow the request to proceed normally.
    /// </summary>
    public void SetFault(Func<FaultContext, Status?> faultFunc)
    {
        _faultFunc = faultFunc;
    }

    /// <summary>
    /// Clears the fault injector — all requests proceed normally.
    /// </summary>
    public void Clear()
    {
        _faultFunc = null;
    }

    /// <summary>
    /// Checks if a fault should be injected for the given context.
    /// Returns the error status, or null if the request should proceed.
    /// </summary>
    internal Status? Check(FaultContext context)
    {
        return _faultFunc?.Invoke(context);
    }
}

using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator;

/// <summary>
/// An entry in the append-only mutation log used by ReadChangeStream.
/// Each mutation to a row produces one log entry recording the change.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readchangestreamresponse
/// </summary>
internal sealed class MutationLogEntry
{
    /// <summary>Monotonic sequence number (0-based).</summary>
    public long SequenceNumber { get; init; }

    /// <summary>The row that was mutated.</summary>
    public required ByteString RowKey { get; init; }

    /// <summary>The mutations that were applied.</summary>
    public required IReadOnlyList<Mutation> Mutations { get; init; }

    /// <summary>Server-assigned commit timestamp (microseconds since epoch).</summary>
    public long CommitTimestampMicros { get; init; }

    /// <summary>
    /// The type of change.
    /// Ref: ReadChangeStreamResponse.Types.DataChange.Types.Type
    ///   USER = 1 — user-initiated mutation
    ///   GARBAGE_COLLECTION = 2 — GC-initiated deletion
    ///   CONTINUATION = 3 — continuation of a multi-chunk change
    /// </summary>
    public ReadChangeStreamResponse.Types.DataChange.Types.Type ChangeType { get; init; }
        = ReadChangeStreamResponse.Types.DataChange.Types.Type.User;
}

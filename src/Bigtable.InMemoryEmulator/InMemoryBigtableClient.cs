using Google.Api.Gax.Grpc;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator;

/// <summary>
/// Lightweight in-memory BigtableClient subclass for simple unit tests.
/// Overrides methods that return protobuf types directly.
///
/// LIMITATION: ReadRows() returns ReadRowsStream with an internal constructor —
/// this client CANNOT support ReadRows. Use InMemoryBigtableServer (Layer 3)
/// for production-quality testing where ReadRows is needed.
///
/// Use case: Quick unit tests that only need MutateRow, CheckAndMutateRow,
/// ReadModifyWriteRow, and ReadRow.
///
/// Ref: Phase 4-SDK plan — "Layer 1: InMemoryBigtableClient — LIGHTWEIGHT OPTION"
/// </summary>
public sealed class InMemoryBigtableClient
{
    private readonly InMemoryBigtableStore _store;
    private readonly string _projectId;
    private readonly string _instanceId;

    internal InMemoryBigtableClient(InMemoryBigtableStore store, string projectId = "test-project", string instanceId = "test-instance")
    {
        _store = store;
        _projectId = projectId;
        _instanceId = instanceId;
    }

    /// <summary>
    /// Gets a TableName resource for the specified table.
    /// </summary>
    public TableName GetTableName(string tableName) =>
        new(_projectId, _instanceId, tableName);

    /// <summary>
    /// Mutates a row atomically.
    /// </summary>
    public MutateRowResponse MutateRow(string tableName, ByteString rowKey, params Mutation[] mutations)
    {
        var table = _store.GetTable(tableName);
        table.MutateRow(rowKey, mutations);
        return new MutateRowResponse();
    }

    /// <summary>
    /// Checks a predicate filter and applies true or false mutations accordingly.
    /// Note: predicateFilter is NOT supported in Layer 1 (always treated as no match).
    /// Use Layer 3 (InMemoryBigtableServer) for full filter support.
    /// </summary>
    public CheckAndMutateRowResponse CheckAndMutateRow(
        string tableName,
        ByteString rowKey,
        IEnumerable<Mutation>? trueMutations = null,
        IEnumerable<Mutation>? falseMutations = null)
    {
        var table = _store.GetTable(tableName);
        var matched = table.CheckAndMutateRow(
            rowKey, null, trueMutations?.ToList(), falseMutations?.ToList());
        return new CheckAndMutateRowResponse { PredicateMatched = matched };
    }

    /// <summary>
    /// Atomically reads, modifies, and writes a row.
    /// Note: Returns modified cells as CellData (internal type), not a full Row proto.
    /// </summary>
    public void ReadModifyWriteRow(
        string tableName,
        ByteString rowKey,
        IEnumerable<ReadModifyWriteRule> rules)
    {
        var table = _store.GetTable(tableName);
        table.ReadModifyWriteRow(rowKey, rules.ToList());
    }

    /// <summary>
    /// Gets the backing store for direct access.
    /// </summary>
    internal InMemoryBigtableStore Store => _store;
}

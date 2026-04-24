using System.Text.RegularExpressions;
using Google.Cloud.Bigtable.Admin.V2;
using Google.Protobuf;
using Grpc.Core;

namespace Bigtable.InMemoryEmulator;

/// <summary>
/// In-memory storage engine for Bigtable data.
/// Thread-safe via ReaderWriterLockSlim (store-level) and per-row locks.
///
/// Locking hierarchy (documented invariant for deadlock-freedom):
/// Rule 1: Acquire store-level ReaderWriterLockSlim (read or write mode) BEFORE any row lock.
/// Rule 2: Never hold two row locks simultaneously.
/// Rule 3: ReadRows / range scans hold store read lock only — no row locks.
/// Rule 4: Single-row mutations (MutateRow, CheckAndMutateRow, ReadModifyWriteRow)
///         hold store read lock + one row lock.
/// Rule 5: Store write lock is only needed for structural changes
///         (add/remove rows from the dictionary).
/// Rule 6: MutateRows (batch) processes entries sequentially, each acquiring
///         store read lock + row lock, then releasing before the next entry.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2
/// </summary>
internal sealed class InMemoryBigtableStore : IDisposable
{
    // Regex for valid family names.
    // Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#columnfamily
    //   "Must match the regex `[_a-zA-Z0-9][-_.a-zA-Z0-9]*`"
    private static readonly Regex FamilyNameRegex = new(@"^[_a-zA-Z0-9][-_.a-zA-Z0-9]*$", RegexOptions.Compiled);

    private readonly Dictionary<string, TableData> _tables = new();
    private readonly ReaderWriterLockSlim _tablesLock = new();

    /// <summary>
    /// Creates a table with the specified column families.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#createtablerequest
    /// </summary>
    public void CreateTable(string tableName, IEnumerable<string> columnFamilies, Dictionary<string, GcRule?>? gcRules = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(tableName);

        var families = columnFamilies.ToList();
        foreach (var family in families)
        {
            ValidateFamilyName(family);
        }

        // Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2
        //   Admin API limit: Column families > 100 → INVALID_ARGUMENT
        if (families.Count > 100)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Table cannot have more than 100 column families (got {families.Count})."));
        }

        _tablesLock.EnterWriteLock();
        try
        {
            if (_tables.ContainsKey(tableName))
            {
                throw new RpcException(new Status(StatusCode.AlreadyExists, $"Table '{tableName}' already exists."));
            }

            var config = new TableConfig { Name = tableName };
            foreach (var family in families)
            {
                GcRule? gcRule = null;
                gcRules?.TryGetValue(family, out gcRule);
                config.AddFamily(family, gcRule);
            }

            _tables[tableName] = new TableData(config);
        }
        finally
        {
            _tablesLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Gets table data for the specified table. Throws NOT_FOUND if not registered.
    /// </summary>
    public TableData GetTable(string tableName)
    {
        _tablesLock.EnterReadLock();
        try
        {
            if (!_tables.TryGetValue(tableName, out var table))
            {
                // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2
                //   Any RPC referencing non-existent table returns NOT_FOUND
                throw new RpcException(new Status(StatusCode.NotFound, $"Table '{tableName}' not found."));
            }
            return table;
        }
        finally
        {
            _tablesLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Deletes a table.
    /// </summary>
    public void DeleteTable(string tableName)
    {
        _tablesLock.EnterWriteLock();
        try
        {
            if (!_tables.Remove(tableName, out var table))
            {
                throw new RpcException(new Status(StatusCode.NotFound, $"Table '{tableName}' not found."));
            }
            table.Dispose();
        }
        finally
        {
            _tablesLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Lists all table names.
    /// </summary>
    public IReadOnlyList<string> ListTables()
    {
        _tablesLock.EnterReadLock();
        try
        {
            return _tables.Keys.ToList();
        }
        finally
        {
            _tablesLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Returns true if a table exists.
    /// </summary>
    public bool TableExists(string tableName)
    {
        _tablesLock.EnterReadLock();
        try
        {
            return _tables.ContainsKey(tableName);
        }
        finally
        {
            _tablesLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Creates a table with regular column families and optional aggregate column families.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#createtablerequest
    /// </summary>
    public void CreateTableWithAggregates(
        string tableName,
        IEnumerable<string> regularFamilies,
        Dictionary<string, AggregateConfig> aggregateFamilies)
    {
        ArgumentException.ThrowIfNullOrEmpty(tableName);

        var regular = regularFamilies.ToList();
        foreach (var family in regular)
        {
            ValidateFamilyName(family);
        }
        foreach (var family in aggregateFamilies.Keys)
        {
            ValidateFamilyName(family);
        }

        _tablesLock.EnterWriteLock();
        try
        {
            if (_tables.ContainsKey(tableName))
            {
                throw new RpcException(new Status(StatusCode.AlreadyExists, $"Table '{tableName}' already exists."));
            }

            var config = new TableConfig { Name = tableName };
            foreach (var family in regular)
            {
                config.AddFamily(family);
            }
            foreach (var (family, aggConfig) in aggregateFamilies)
            {
                config.AddAggregateFamily(family, aggConfig);
            }

            _tables[tableName] = new TableData(config);
        }
        finally
        {
            _tablesLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Validates a column family name.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#columnfamily
    ///   "Must match the regex `[_a-zA-Z0-9][-_.a-zA-Z0-9]*`"
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
    ///   "Family name > 64 chars" → INVALID_ARGUMENT
    /// </summary>
    internal static void ValidateFamilyName(string familyName)
    {
        if (string.IsNullOrEmpty(familyName))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Column family name must not be empty."));
        }

        // Ref: Family.name doc: "Must be no greater than 64 characters"
        if (familyName.Length > 64)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Column family name '{familyName}' exceeds maximum length of 64 characters."));
        }

        if (!FamilyNameRegex.IsMatch(familyName))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Column family name '{familyName}' is invalid. Must match [-_.a-zA-Z0-9]+."));
        }
    }

    public void Dispose()
    {
        _tablesLock.EnterWriteLock();
        try
        {
            foreach (var table in _tables.Values)
            {
                table.Dispose();
            }
            _tables.Clear();
        }
        finally
        {
            _tablesLock.ExitWriteLock();
        }
        _tablesLock.Dispose();
    }

    /// <summary>
    /// Modifies column families on a table (create, update GC rule, or drop).
    /// Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#modifycolumnfamiliesrequest
    /// </summary>
    public void ModifyColumnFamilies(string tableName,
        IReadOnlyList<(string FamilyId, ModifyAction Action, GcRule? GcRule)> modifications)
    {
        var table = GetTable(tableName);

        foreach (var (familyId, action, gcRule) in modifications)
        {
            switch (action)
            {
                case ModifyAction.Create:
                    ValidateFamilyName(familyId);
                    if (table.Config.HasFamily(familyId))
                    {
                        throw new RpcException(new Status(StatusCode.AlreadyExists,
                            $"Column family '{familyId}' already exists in table '{tableName}'."));
                    }
                    // Ref: Admin API limit: Column families > 100
                    var totalFamilies = table.Config.ColumnFamilies.Count + table.Config.AggregateFamilies.Count;
                    if (totalFamilies >= 100)
                    {
                        throw new RpcException(new Status(StatusCode.InvalidArgument,
                            $"Table '{tableName}' cannot have more than 100 column families."));
                    }
                    table.Config.AddFamily(familyId, gcRule);
                    break;

                case ModifyAction.Update:
                    if (!table.Config.HasFamily(familyId))
                    {
                        throw new RpcException(new Status(StatusCode.NotFound,
                            $"Column family '{familyId}' does not exist in table '{tableName}'."));
                    }
                    if (table.Config.ColumnFamilies.ContainsKey(familyId))
                    {
                        table.Config.ColumnFamilies[familyId] = gcRule;
                    }
                    break;

                case ModifyAction.Drop:
                    if (!table.Config.RemoveFamily(familyId))
                    {
                        throw new RpcException(new Status(StatusCode.NotFound,
                            $"Column family '{familyId}' does not exist in table '{tableName}'."));
                    }
                    // Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#modifycolumnfamiliesrequest
                    //   "Drop (delete) all cells in the column family."
                    table.DeleteCellsInFamily(familyId);
                    break;
            }
        }
    }

    internal enum ModifyAction { Create, Update, Drop }
}

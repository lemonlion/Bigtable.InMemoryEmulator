using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;

namespace Bigtable.InMemoryEmulator;

/// <summary>
/// Test setup interface for a single table. Provides test-only operations
/// for inspecting and manipulating table state.
///
/// Ref: Phase 5 plan — "ITableTestSetup — Test Setup Interface"
/// </summary>
public interface ITableTestSetup
{
    /// <summary>
    /// Exports the table state as a JSON string.
    /// </summary>
    string ExportState();

    /// <summary>
    /// Imports state from a JSON string. Full replacement of existing data.
    /// </summary>
    void ImportState(string json);

    /// <summary>
    /// Removes all rows from the table.
    /// </summary>
    void ClearRows();

    /// <summary>
    /// Gets the number of rows in the table.
    /// </summary>
    int RowCount { get; }

    /// <summary>
    /// Gets the configured column family names.
    /// </summary>
    IReadOnlyList<string> ColumnFamilies { get; }

    /// <summary>
    /// Gets the GC rules for each column family.
    /// Ref: https://cloud.google.com/bigtable/docs/garbage-collection
    /// </summary>
    IReadOnlyDictionary<string, GcRule?> GcRules { get; }

    /// <summary>
    /// Gets the auto-persistence file path, or null if auto-persist is not configured.
    /// </summary>
    string? StateFilePath { get; }
}

/// <summary>
/// Implementation of ITableTestSetup backed by TableData.
/// </summary>
internal sealed class InMemoryTableTestSetup : ITableTestSetup
{
    private readonly TableData _table;
    private readonly string? _stateFilePath;

    public InMemoryTableTestSetup(TableData table, string? stateFilePath = null)
    {
        _table = table;
        _stateFilePath = stateFilePath;
    }

    public string ExportState() => StatePersistence.ExportTableState(_table);

    public void ImportState(string json) => StatePersistence.ImportTableState(_table, json);

    public void ClearRows() => _table.ClearRows();

    public int RowCount => _table.RowCount;

    public IReadOnlyList<string> ColumnFamilies =>
        _table.Config.ColumnFamilies.Keys
            .Concat(_table.Config.AggregateFamilies.Keys)
            .ToList();

    public IReadOnlyDictionary<string, GcRule?> GcRules =>
        _table.Config.ColumnFamilies;

    public string? StateFilePath => _stateFilePath;
}

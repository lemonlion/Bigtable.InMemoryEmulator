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
}

/// <summary>
/// Implementation of ITableTestSetup backed by TableData.
/// </summary>
internal sealed class InMemoryTableTestSetup : ITableTestSetup
{
    private readonly TableData _table;

    public InMemoryTableTestSetup(TableData table)
    {
        _table = table;
    }

    public string ExportState() => StatePersistence.ExportTableState(_table);

    public void ImportState(string json) => StatePersistence.ImportTableState(_table, json);

    public void ClearRows() => _table.ClearRows();

    public int RowCount => _table.RowCount;

    public IReadOnlyList<string> ColumnFamilies =>
        _table.Config.ColumnFamilies.Keys
            .Concat(_table.Config.AggregateFamilies.Keys)
            .ToList();
}

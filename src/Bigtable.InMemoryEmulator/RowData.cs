using Google.Protobuf;

namespace Bigtable.InMemoryEmulator;

/// <summary>
/// Represents a row in a Bigtable table.
/// A row is a sorted collection of cells keyed by (family, qualifier, timestamp).
/// 
/// Cell ordering within a row follows Bigtable conventions:
/// - Families: unspecified order (we use string sort for determinism)
/// - Qualifiers: ascending within each family (lexicographic byte comparison)
/// - Timestamps: descending within each (family, qualifier) — newest first
/// 
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#row
/// </summary>
internal sealed class RowData
{
    private readonly object _lock = new();
    private readonly List<CellData> _cells = [];

    /// <summary>
    /// The row key.
    /// </summary>
    public required ByteString Key { get; init; }

    /// <summary>
    /// Acquires the row lock for atomic single-row mutations.
    /// Callers must use this in a lock() statement.
    /// </summary>
    public object Lock => _lock;

    /// <summary>
    /// Gets a snapshot of all cells in this row, sorted by (family, qualifier asc, timestamp desc).
    /// </summary>
    public IReadOnlyList<CellData> GetCells()
    {
        lock (_lock)
        {
            return SortCells(_cells).ToList();
        }
    }

    /// <summary>
    /// Gets the number of cells in this row.
    /// </summary>
    public int CellCount
    {
        get
        {
            lock (_lock)
            {
                return _cells.Count;
            }
        }
    }

    /// <summary>
    /// Returns true if the row has no cells.
    /// </summary>
    public bool IsEmpty
    {
        get
        {
            lock (_lock)
            {
                return _cells.Count == 0;
            }
        }
    }

    /// <summary>
    /// Sets a cell value. If a cell with the same (family, qualifier, timestamp) exists, it is overwritten.
    /// Must be called while holding the row lock.
    /// </summary>
    public void SetCell(string family, ByteString qualifier, long timestampMicros, ByteString value)
    {
        // Remove existing cell with same key if present
        _cells.RemoveAll(c =>
            c.Family == family &&
            c.Qualifier == qualifier &&
            c.TimestampMicros == timestampMicros);

        _cells.Add(new CellData
        {
            Family = family,
            Qualifier = qualifier,
            TimestampMicros = timestampMicros,
            Value = value
        });
    }

    /// <summary>
    /// Deletes cells matching the given family and qualifier within the specified timestamp range.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
    ///   "Deletes cells from a column."
    /// </summary>
    public void DeleteFromColumn(string family, ByteString qualifier, long? startTimestampMicros, long? endTimestampMicros)
    {
        _cells.RemoveAll(c =>
            c.Family == family &&
            c.Qualifier == qualifier &&
            (startTimestampMicros == null || c.TimestampMicros >= startTimestampMicros) &&
            (endTimestampMicros == null || c.TimestampMicros < endTimestampMicros));
    }

    /// <summary>
    /// Deletes all cells in the given family.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
    ///   "Deletes cells from an entire family."
    /// </summary>
    public void DeleteFromFamily(string family)
    {
        _cells.RemoveAll(c => c.Family == family);
    }

    /// <summary>
    /// Deletes all cells in this row.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
    ///   "Deletes cells from the entire row."
    /// </summary>
    public void DeleteAllCells()
    {
        _cells.Clear();
    }

    /// <summary>
    /// Gets cells matching the specified family and qualifier, sorted by timestamp descending.
    /// </summary>
    public IReadOnlyList<CellData> GetCellsForColumn(string family, ByteString qualifier)
    {
        lock (_lock)
        {
            return _cells
                .Where(c => c.Family == family && c.Qualifier == qualifier)
                .OrderByDescending(c => c.TimestampMicros)
                .ToList();
        }
    }

    /// <summary>
    /// Sorts cells by (family asc, qualifier asc, timestamp desc).
    /// </summary>
    private static IEnumerable<CellData> SortCells(IEnumerable<CellData> cells)
    {
        return cells
            .OrderBy(c => c.Family, StringComparer.Ordinal)
            .ThenBy(c => c.Qualifier, ByteStringComparer.Instance)
            .ThenByDescending(c => c.TimestampMicros);
    }
}

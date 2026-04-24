using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator;

/// <summary>
/// State persistence for InMemoryBigtableStore.
/// Supports export/import of table data as JSON for test seeding and snapshots.
///
/// JSON format:
/// {
///   "tables": {
///     "table-name": {
///       "rows": [
///         {
///           "key": "base64-encoded-row-key",
///           "cells": [
///             {
///               "family": "cf1",
///               "qualifier": "base64-encoded-qualifier",
///               "timestampMicros": 1000,
///               "value": "base64-encoded-value",
///               "labels": ["label1"]
///             }
///           ]
///         }
///       ]
///     }
///   }
/// }
/// </summary>
internal static class StatePersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    /// <summary>
    /// Exports the state of all tables in the store as a JSON string.
    /// </summary>
    public static string ExportState(InMemoryBigtableStore store)
    {
        var state = new StoreState();

        foreach (var tableName in store.ListTables())
        {
            var table = store.GetTable(tableName);
            var tableState = new TableState();

            foreach (var row in table.ReadRows())
            {
                var rowState = new RowState
                {
                    Key = Convert.ToBase64String(row.Key.Span),
                };

                foreach (var cell in row.GetCells())
                {
                    rowState.Cells.Add(new CellState
                    {
                        Family = cell.Family,
                        Qualifier = Convert.ToBase64String(cell.Qualifier.Span),
                        TimestampMicros = cell.TimestampMicros,
                        Value = Convert.ToBase64String(cell.Value.Span),
                        Labels = cell.Labels.Count > 0 ? cell.Labels.ToList() : null,
                    });
                }

                tableState.Rows.Add(rowState);
            }

            state.Tables[tableName] = tableState;
        }

        return JsonSerializer.Serialize(state, JsonOptions);
    }

    /// <summary>
    /// Exports the state of a single table as a JSON string.
    /// </summary>
    public static string ExportTableState(TableData table)
    {
        var tableState = new TableState();

        foreach (var row in table.ReadRows())
        {
            var rowState = new RowState
            {
                Key = Convert.ToBase64String(row.Key.Span),
            };

            foreach (var cell in row.GetCells())
            {
                rowState.Cells.Add(new CellState
                {
                    Family = cell.Family,
                    Qualifier = Convert.ToBase64String(cell.Qualifier.Span),
                    TimestampMicros = cell.TimestampMicros,
                    Value = Convert.ToBase64String(cell.Value.Span),
                    Labels = cell.Labels.Count > 0 ? cell.Labels.ToList() : null,
                });
            }

            tableState.Rows.Add(rowState);
        }

        return JsonSerializer.Serialize(tableState, JsonOptions);
    }

    /// <summary>
    /// Imports state into a table from a JSON string. This is a full replacement — existing data is cleared.
    /// </summary>
    public static void ImportTableState(TableData table, string json)
    {
        var tableState = JsonSerializer.Deserialize<TableState>(json, JsonOptions)
            ?? throw new ArgumentException("Invalid state JSON.");

        table.ClearRows();
        ImportTableStateInternal(table, tableState);
    }

    /// <summary>
    /// Imports full store state from a JSON string. Tables must already exist.
    /// </summary>
    public static void ImportState(InMemoryBigtableStore store, string json)
    {
        var state = JsonSerializer.Deserialize<StoreState>(json, JsonOptions)
            ?? throw new ArgumentException("Invalid state JSON.");

        foreach (var (tableName, tableState) in state.Tables)
        {
            var table = store.GetTable(tableName);
            table.ClearRows();
            ImportTableStateInternal(table, tableState);
        }
    }

    /// <summary>
    /// Exports state to a file.
    /// </summary>
    public static void ExportStateToFile(InMemoryBigtableStore store, string filePath)
    {
        var json = ExportState(store);
        File.WriteAllText(filePath, json);
    }

    /// <summary>
    /// Imports state from a file.
    /// </summary>
    public static void ImportStateFromFile(InMemoryBigtableStore store, string filePath)
    {
        var json = File.ReadAllText(filePath);
        ImportState(store, json);
    }

    private static void ImportTableStateInternal(TableData table, TableState tableState)
    {
        foreach (var rowState in tableState.Rows)
        {
            var rowKey = ByteString.CopyFrom(Convert.FromBase64String(rowState.Key));

            var mutations = new List<Google.Cloud.Bigtable.V2.Mutation>();
            foreach (var cellState in rowState.Cells)
            {
                var qualifier = ByteString.CopyFrom(Convert.FromBase64String(cellState.Qualifier));
                var value = ByteString.CopyFrom(Convert.FromBase64String(cellState.Value));

                mutations.Add(new Google.Cloud.Bigtable.V2.Mutation
                {
                    SetCell = new Google.Cloud.Bigtable.V2.Mutation.Types.SetCell
                    {
                        FamilyName = cellState.Family,
                        ColumnQualifier = qualifier,
                        TimestampMicros = cellState.TimestampMicros,
                        Value = value,
                    }
                });
            }

            if (mutations.Count > 0)
            {
                table.MutateRow(rowKey, mutations);
            }
        }
    }

    #region State DTOs

    private sealed class StoreState
    {
        public Dictionary<string, TableState> Tables { get; set; } = new();
    }

    private sealed class TableState
    {
        public List<RowState> Rows { get; set; } = [];
    }

    private sealed class RowState
    {
        public string Key { get; set; } = "";
        public List<CellState> Cells { get; set; } = [];
    }

    private sealed class CellState
    {
        public string Family { get; set; } = "";
        public string Qualifier { get; set; } = "";
        public long TimestampMicros { get; set; }
        public string Value { get; set; } = "";
        public List<string>? Labels { get; set; }
    }

    #endregion
}

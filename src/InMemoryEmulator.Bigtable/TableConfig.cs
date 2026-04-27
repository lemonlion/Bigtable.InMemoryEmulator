using Google.Cloud.Bigtable.Admin.V2;

namespace InMemoryEmulator.Bigtable;

/// <summary>
/// Configuration for a single in-memory Bigtable table.
/// </summary>
internal sealed class TableConfig
{
    public required string Name { get; init; }
    public Dictionary<string, GcRule?> ColumnFamilies { get; init; } = new();
    public Dictionary<string, AggregateConfig> AggregateFamilies { get; init; } = new();

    /// <summary>
    /// Returns true if the given family name is registered on this table (regular or aggregate).
    /// </summary>
    public bool HasFamily(string familyName) =>
        ColumnFamilies.ContainsKey(familyName) || AggregateFamilies.ContainsKey(familyName);

    /// <summary>
    /// Returns true if the given family is an aggregate family.
    /// </summary>
    public bool IsAggregateFamily(string familyName) => AggregateFamilies.ContainsKey(familyName);

    /// <summary>
    /// Gets the aggregate config for a family. Returns null if not an aggregate family.
    /// </summary>
    public AggregateConfig? GetAggregateConfig(string familyName) =>
        AggregateFamilies.TryGetValue(familyName, out var config) ? config : null;

    /// <summary>
    /// Adds a column family to this table.
    /// </summary>
    public void AddFamily(string familyName, GcRule? gcRule = null)
    {
        ColumnFamilies[familyName] = gcRule;
    }

    /// <summary>
    /// Adds an aggregate column family to this table.
    /// </summary>
    public void AddAggregateFamily(string familyName, AggregateConfig config)
    {
        AggregateFamilies[familyName] = config;
    }

    /// <summary>
    /// Removes a column family from this table.
    /// </summary>
    public bool RemoveFamily(string familyName) =>
        ColumnFamilies.Remove(familyName) || AggregateFamilies.Remove(familyName);
}

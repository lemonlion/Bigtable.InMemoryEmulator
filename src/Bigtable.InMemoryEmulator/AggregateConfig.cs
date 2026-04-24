namespace Bigtable.InMemoryEmulator;

/// <summary>
/// Defines the aggregation type for a column family.
/// Corresponds to Google.Cloud.Bigtable.V2.Type.Types.Aggregate configuration.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
///   Mutation.AddToCell — "Incrementally updates a cell in an Aggregate family"
/// </summary>
internal sealed class AggregateConfig
{
    public AggregatorType Aggregator { get; init; }

    public static AggregateConfig Sum() => new() { Aggregator = AggregatorType.Sum };
    public static AggregateConfig Min() => new() { Aggregator = AggregatorType.Min };
    public static AggregateConfig Max() => new() { Aggregator = AggregatorType.Max };
    public static AggregateConfig HllppUniqueCount() => new() { Aggregator = AggregatorType.HllppUniqueCount };
}

internal enum AggregatorType
{
    Sum,
    Min,
    Max,
    HllppUniqueCount,
}

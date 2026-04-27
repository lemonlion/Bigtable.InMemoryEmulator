using InMemoryEmulator.Bigtable;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for AddToCell / MergeToCell aggregation mutations.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
///   Mutation.AddToCell — "Incrementally updates a cell in an Aggregate family"
///   Mutation.MergeToCell — "Merges accumulated state to an Aggregate cell"
/// </summary>
public class AggregationCellTests : IDisposable
{
    private readonly InMemoryBigtableStore _store = new();
    private const string TableName = "aggregate-table";
    private const string SumFamily = "sum_cf";
    private const string MinFamily = "min_cf";
    private const string MaxFamily = "max_cf";
    private const string RegularFamily = "regular_cf";

    public AggregationCellTests()
    {
        // Create table with aggregate families (Sum, Min, Max) and a regular family
        var aggregateConfig = new Dictionary<string, AggregateConfig>
        {
            [SumFamily] = AggregateConfig.Sum(),
            [MinFamily] = AggregateConfig.Min(),
            [MaxFamily] = AggregateConfig.Max(),
        };

        _store.CreateTableWithAggregates(
            TableName,
            regularFamilies: [RegularFamily],
            aggregateFamilies: aggregateConfig);
    }

    public void Dispose() => _store.Dispose();

    #region AddToCell — Sum

    [Fact]
    public void AddToCell_Sum_initializes_from_zero()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("counter");

        table.MutateRow(rowKey, [CreateAddToCell(SumFamily, qualifier, 42)]);

        var row = table.GetRow(rowKey);
        row.Should().NotBeNull();
        var cells = row!.GetCells();
        cells.Should().HaveCount(1);
        cells[0].Family.Should().Be(SumFamily);
        ReadInt64(cells[0].Value).Should().Be(42);
    }

    [Fact]
    public void AddToCell_Sum_accumulates_multiple_additions()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("counter");

        table.MutateRow(rowKey, [CreateAddToCell(SumFamily, qualifier, 10)]);
        table.MutateRow(rowKey, [CreateAddToCell(SumFamily, qualifier, 20)]);
        table.MutateRow(rowKey, [CreateAddToCell(SumFamily, qualifier, 5)]);

        var row = table.GetRow(rowKey);
        var cells = row!.GetCells();
        cells.Should().HaveCount(1);
        ReadInt64(cells[0].Value).Should().Be(35);
    }

    [Fact]
    public void AddToCell_Sum_negative_decrements()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("counter");

        table.MutateRow(rowKey, [CreateAddToCell(SumFamily, qualifier, 100)]);
        table.MutateRow(rowKey, [CreateAddToCell(SumFamily, qualifier, -30)]);

        var row = table.GetRow(rowKey);
        ReadInt64(row!.GetCells()[0].Value).Should().Be(70);
    }

    [Fact]
    public void AddToCell_Sum_multiple_qualifiers_independent()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qual1 = ByteString.CopyFromUtf8("counter1");
        var qual2 = ByteString.CopyFromUtf8("counter2");

        table.MutateRow(rowKey, [CreateAddToCell(SumFamily, qual1, 10)]);
        table.MutateRow(rowKey, [CreateAddToCell(SumFamily, qual2, 20)]);

        var row = table.GetRow(rowKey);
        var cells = row!.GetCells();
        cells.Should().HaveCount(2);

        var c1 = cells.First(c => c.Qualifier.ToStringUtf8() == "counter1");
        var c2 = cells.First(c => c.Qualifier.ToStringUtf8() == "counter2");
        ReadInt64(c1.Value).Should().Be(10);
        ReadInt64(c2.Value).Should().Be(20);
    }

    [Fact]
    public void AddToCell_Sum_different_timestamps_are_separate_cells()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("counter");

        table.MutateRow(rowKey, [CreateAddToCellWithTimestamp(SumFamily, qualifier, 10, 1000)]);
        table.MutateRow(rowKey, [CreateAddToCellWithTimestamp(SumFamily, qualifier, 20, 2000)]);

        var row = table.GetRow(rowKey);
        var cells = row!.GetCells();
        // Different timestamps = different aggregate cells
        cells.Should().HaveCount(2);
    }

    [Fact]
    public void AddToCell_Sum_same_timestamp_merges()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("counter");

        table.MutateRow(rowKey, [CreateAddToCellWithTimestamp(SumFamily, qualifier, 10, 1000)]);
        table.MutateRow(rowKey, [CreateAddToCellWithTimestamp(SumFamily, qualifier, 20, 1000)]);

        var row = table.GetRow(rowKey);
        var cells = row!.GetCells();
        cells.Should().HaveCount(1);
        ReadInt64(cells[0].Value).Should().Be(30);
    }

    #endregion

    #region AddToCell — Min

    [Fact]
    public void AddToCell_Min_initializes_with_first_value()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("min_val");

        table.MutateRow(rowKey, [CreateAddToCell(MinFamily, qualifier, 42)]);

        var row = table.GetRow(rowKey);
        ReadInt64(row!.GetCells()[0].Value).Should().Be(42);
    }

    [Fact]
    public void AddToCell_Min_keeps_minimum_across_additions()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("min_val");

        table.MutateRow(rowKey, [CreateAddToCell(MinFamily, qualifier, 50)]);
        table.MutateRow(rowKey, [CreateAddToCell(MinFamily, qualifier, 30)]);
        table.MutateRow(rowKey, [CreateAddToCell(MinFamily, qualifier, 70)]);

        var row = table.GetRow(rowKey);
        ReadInt64(row!.GetCells()[0].Value).Should().Be(30);
    }

    #endregion

    #region AddToCell — Max

    [Fact]
    public void AddToCell_Max_initializes_with_first_value()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("max_val");

        table.MutateRow(rowKey, [CreateAddToCell(MaxFamily, qualifier, 42)]);

        var row = table.GetRow(rowKey);
        ReadInt64(row!.GetCells()[0].Value).Should().Be(42);
    }

    [Fact]
    public void AddToCell_Max_keeps_maximum_across_additions()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("max_val");

        table.MutateRow(rowKey, [CreateAddToCell(MaxFamily, qualifier, 50)]);
        table.MutateRow(rowKey, [CreateAddToCell(MaxFamily, qualifier, 70)]);
        table.MutateRow(rowKey, [CreateAddToCell(MaxFamily, qualifier, 30)]);

        var row = table.GetRow(rowKey);
        ReadInt64(row!.GetCells()[0].Value).Should().Be(70);
    }

    #endregion

    #region MergeToCell

    [Fact]
    public void MergeToCell_Sum_merges_precomputed_state()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("counter");

        table.MutateRow(rowKey, [CreateAddToCell(SumFamily, qualifier, 10)]);
        table.MutateRow(rowKey, [CreateMergeToCell(SumFamily, qualifier, 25)]);

        var row = table.GetRow(rowKey);
        ReadInt64(row!.GetCells()[0].Value).Should().Be(35);
    }

    [Fact]
    public void MergeToCell_Min_merges_minimum()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("min_val");

        table.MutateRow(rowKey, [CreateAddToCell(MinFamily, qualifier, 50)]);
        table.MutateRow(rowKey, [CreateMergeToCell(MinFamily, qualifier, 30)]);

        var row = table.GetRow(rowKey);
        ReadInt64(row!.GetCells()[0].Value).Should().Be(30);
    }

    [Fact]
    public void MergeToCell_Max_merges_maximum()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("max_val");

        table.MutateRow(rowKey, [CreateAddToCell(MaxFamily, qualifier, 50)]);
        table.MutateRow(rowKey, [CreateMergeToCell(MaxFamily, qualifier, 70)]);

        var row = table.GetRow(rowKey);
        ReadInt64(row!.GetCells()[0].Value).Should().Be(70);
    }

    [Fact]
    public void MergeToCell_initializes_from_zero_when_no_prior_state()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("counter");

        table.MutateRow(rowKey, [CreateMergeToCell(SumFamily, qualifier, 42)]);

        var row = table.GetRow(rowKey);
        ReadInt64(row!.GetCells()[0].Value).Should().Be(42);
    }

    #endregion

    #region Validation

    [Fact]
    public void SetCell_on_aggregate_family_throws_InvalidArgument()
    {
        // Ref: "Regular SetCell mutations to an Aggregate family are rejected with INVALID_ARGUMENT"
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var mutation = new Mutation
        {
            SetCell = new Mutation.Types.SetCell
            {
                FamilyName = SumFamily,
                ColumnQualifier = ByteString.CopyFromUtf8("col"),
                TimestampMicros = 1000,
                Value = ByteString.CopyFromUtf8("value"),
            }
        };

        var act = () => table.MutateRow(rowKey, [mutation]);
        act.Should().Throw<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public void AddToCell_on_non_aggregate_family_throws_InvalidArgument()
    {
        // Ref: "INVALID_ARGUMENT if family is not an Aggregate family"
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var mutation = new Mutation
        {
            AddToCell = new Mutation.Types.AddToCell
            {
                FamilyName = RegularFamily,
                ColumnQualifier = new Value { RawValue = ByteString.CopyFromUtf8("col") },
                Timestamp = new Value { RawTimestampMicros = 1000 },
                Input = new Value { IntValue = 10 },
            }
        };

        var act = () => table.MutateRow(rowKey, [mutation]);
        act.Should().Throw<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public void MergeToCell_on_non_aggregate_family_throws_InvalidArgument()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var mutation = new Mutation
        {
            MergeToCell = new Mutation.Types.MergeToCell
            {
                FamilyName = RegularFamily,
                ColumnQualifier = new Value { RawValue = ByteString.CopyFromUtf8("col") },
                Timestamp = new Value { RawTimestampMicros = 1000 },
                Input = new Value { IntValue = 10 },
            }
        };

        var act = () => table.MutateRow(rowKey, [mutation]);
        act.Should().Throw<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public void AddToCell_with_HllppUniqueCount_throws_Unimplemented()
    {
        // HyperLogLogPlusPlusUniqueCount is deferred — return UNIMPLEMENTED
        var hllppConfig = new Dictionary<string, AggregateConfig>
        {
            ["hllpp_cf"] = AggregateConfig.HllppUniqueCount(),
        };

        _store.CreateTableWithAggregates("hllpp-table", [], hllppConfig);
        var table = _store.GetTable("hllpp-table");
        var rowKey = ByteString.CopyFromUtf8("row1");

        var act = () => table.MutateRow(rowKey, [new Mutation
        {
            AddToCell = new Mutation.Types.AddToCell
            {
                FamilyName = "hllpp_cf",
                ColumnQualifier = new Value { RawValue = ByteString.CopyFromUtf8("col") },
                Timestamp = new Value { RawTimestampMicros = 1000 },
                Input = new Value { IntValue = 10 },
            }
        }]);

        act.Should().Throw<RpcException>()
            .Where(e => e.StatusCode == StatusCode.Unimplemented);
    }

    #endregion

    #region Delete operations on aggregate families

    [Fact]
    public void DeleteFromColumn_works_on_aggregate_family()
    {
        // Ref: "Regular DeleteFromColumn/DeleteFromFamily/DeleteFromRow work normally on aggregate families"
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("counter");

        table.MutateRow(rowKey, [CreateAddToCell(SumFamily, qualifier, 42)]);

        var deleteMutation = new Mutation
        {
            DeleteFromColumn = new Mutation.Types.DeleteFromColumn
            {
                FamilyName = SumFamily,
                ColumnQualifier = qualifier,
            }
        };
        table.MutateRow(rowKey, [deleteMutation]);

        var row = table.GetRow(rowKey);
        row.Should().BeNull();
    }

    [Fact]
    public void DeleteFromFamily_works_on_aggregate_family()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");

        table.MutateRow(rowKey, [CreateAddToCell(SumFamily, ByteString.CopyFromUtf8("c1"), 10)]);
        table.MutateRow(rowKey, [CreateAddToCell(SumFamily, ByteString.CopyFromUtf8("c2"), 20)]);

        var deleteMutation = new Mutation
        {
            DeleteFromFamily = new Mutation.Types.DeleteFromFamily { FamilyName = SumFamily }
        };
        table.MutateRow(rowKey, [deleteMutation]);

        var row = table.GetRow(rowKey);
        row.Should().BeNull();
    }

    [Fact]
    public void DeleteFromRow_works_on_aggregate_family()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");

        table.MutateRow(rowKey, [CreateAddToCell(SumFamily, ByteString.CopyFromUtf8("c1"), 10)]);

        var deleteMutation = new Mutation { DeleteFromRow = new Mutation.Types.DeleteFromRow() };
        table.MutateRow(rowKey, [deleteMutation]);

        var row = table.GetRow(rowKey);
        row.Should().BeNull();
    }

    #endregion

    #region Atomicity with regular mutations

    [Fact]
    public void AddToCell_and_regular_mutation_in_same_row_are_atomic()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("col");

        var mutations = new[]
        {
            CreateAddToCell(SumFamily, ByteString.CopyFromUtf8("counter"), 10),
            new Mutation
            {
                SetCell = new Mutation.Types.SetCell
                {
                    FamilyName = RegularFamily,
                    ColumnQualifier = ByteString.CopyFromUtf8("data"),
                    TimestampMicros = 1000,
                    Value = ByteString.CopyFromUtf8("hello"),
                }
            }
        };

        table.MutateRow(rowKey, mutations);

        var row = table.GetRow(rowKey);
        var cells = row!.GetCells();
        cells.Should().HaveCount(2);
    }

    #endregion

    #region ReadRows returns aggregate cells normally

    [Fact]
    public void ReadRows_returns_aggregate_cells()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");

        table.MutateRow(rowKey, [CreateAddToCell(SumFamily, ByteString.CopyFromUtf8("counter"), 42)]);

        var rows = table.ReadRows().ToList();
        rows.Should().HaveCount(1);
        var cells = rows[0].GetCells();
        cells.Should().HaveCount(1);
        ReadInt64(cells[0].Value).Should().Be(42);
    }

    #endregion

    #region Helpers

    private static Mutation CreateAddToCell(string family, ByteString qualifier, long amount)
    {
        return new Mutation
        {
            AddToCell = new Mutation.Types.AddToCell
            {
                FamilyName = family,
                ColumnQualifier = new Value { RawValue = qualifier },
                // Use a fixed timestamp so repeated calls aggregate to the same cell
                Timestamp = new Value { RawTimestampMicros = 1000 },
                Input = new Value { IntValue = amount },
            }
        };
    }

    private static Mutation CreateAddToCellWithTimestamp(string family, ByteString qualifier, long amount, long timestampMicros)
    {
        return new Mutation
        {
            AddToCell = new Mutation.Types.AddToCell
            {
                FamilyName = family,
                ColumnQualifier = new Value { RawValue = qualifier },
                Timestamp = new Value { RawTimestampMicros = timestampMicros },
                Input = new Value { IntValue = amount },
            }
        };
    }

    private static Mutation CreateMergeToCell(string family, ByteString qualifier, long amount)
    {
        return new Mutation
        {
            MergeToCell = new Mutation.Types.MergeToCell
            {
                FamilyName = family,
                ColumnQualifier = new Value { RawValue = qualifier },
                // Use a fixed timestamp so merges target the same cell
                Timestamp = new Value { RawTimestampMicros = 1000 },
                Input = new Value { IntValue = amount },
            }
        };
    }

    private static long ReadInt64(ByteString value)
    {
        if (value.IsEmpty || value.Length < 8) return 0;
        return System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(value.Span[..8]);
    }

    #endregion
}

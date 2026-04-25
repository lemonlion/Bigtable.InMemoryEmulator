using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for aggregate column families: Sum, Min, Max via AddToCell.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
///   AddToCell: Atomically adds to or subtracts from an aggregate cell value.
/// Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#columnfamily
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.GcpOnly)]
public sealed class AggregateFamilyTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string SUM_CF = "sum_cf";
    private const string MIN_CF = "min_cf";
    private const string MAX_CF = "max_cf";
    private const string REG_CF = "regular";

    public AggregateFamilyTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        // Initialize the fixture first (needed to get AdminClient)
        await _fixture.CreateTableAsync("agg-seed", new[] { "seed" });

        // Create table with aggregate families and a regular one
        var request = new Google.Cloud.Bigtable.Admin.V2.CreateTableRequest
        {
            Parent = _fixture.InstanceName,
            TableId = "agg-test",
            Table = new Google.Cloud.Bigtable.Admin.V2.Table()
        };

        // Sum aggregator
        var sumFamily = new Google.Cloud.Bigtable.Admin.V2.ColumnFamily
        {
            ValueType = new Google.Cloud.Bigtable.Admin.V2.Type
            {
                AggregateType = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Aggregate
                {
                    InputType = new Google.Cloud.Bigtable.Admin.V2.Type
                    {
                        Int64Type = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Int64
                        {
                            Encoding = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Int64.Types.Encoding
                            {
                                BigEndianBytes = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Int64.Types.Encoding.Types.BigEndianBytes()
                            }
                        }
                    },
                    Sum = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Aggregate.Types.Sum()
                }
            }
        };
        request.Table.ColumnFamilies.Add(SUM_CF, sumFamily);

        // Min aggregator
        var minFamily = new Google.Cloud.Bigtable.Admin.V2.ColumnFamily
        {
            ValueType = new Google.Cloud.Bigtable.Admin.V2.Type
            {
                AggregateType = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Aggregate
                {
                    InputType = new Google.Cloud.Bigtable.Admin.V2.Type
                    {
                        Int64Type = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Int64
                        {
                            Encoding = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Int64.Types.Encoding
                            {
                                BigEndianBytes = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Int64.Types.Encoding.Types.BigEndianBytes()
                            }
                        }
                    },
                    Min = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Aggregate.Types.Min()
                }
            }
        };
        request.Table.ColumnFamilies.Add(MIN_CF, minFamily);

        // Max aggregator
        var maxFamily = new Google.Cloud.Bigtable.Admin.V2.ColumnFamily
        {
            ValueType = new Google.Cloud.Bigtable.Admin.V2.Type
            {
                AggregateType = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Aggregate
                {
                    InputType = new Google.Cloud.Bigtable.Admin.V2.Type
                    {
                        Int64Type = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Int64
                        {
                            Encoding = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Int64.Types.Encoding
                            {
                                BigEndianBytes = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Int64.Types.Encoding.Types.BigEndianBytes()
                            }
                        }
                    },
                    Max = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Aggregate.Types.Max()
                }
            }
        };
        request.Table.ColumnFamilies.Add(MAX_CF, maxFamily);

        // Regular family too
        request.Table.ColumnFamilies.Add(REG_CF, new Google.Cloud.Bigtable.Admin.V2.ColumnFamily());

        await _fixture.AdminClient.CreateTableAsync(request);
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("agg-test");

    private static ByteString Int64Bytes(long value)
    {
        var bytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return ByteString.CopyFrom(bytes);
    }

    private static long ReadInt64(ByteString bs) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(bs.Span);

    private async Task<long> ReadAggValue(string rowKey, string family, string col)
    {
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys(rowKey)))
            foreach (var fam in row.Families)
                if (fam.Name == family)
                    foreach (var column in fam.Columns)
                        if (column.Qualifier.ToStringUtf8() == col)
                            return ReadInt64(column.Cells[0].Value);
        throw new InvalidOperationException($"Cell {rowKey}/{family}/{col} not found");
    }

    private Mutation AddToCell(string family, string col, long value)
    {
        return new Mutation
        {
            AddToCell = new Mutation.Types.AddToCell
            {
                FamilyName = family,
                ColumnQualifier = new Value { RawValue = ByteString.CopyFromUtf8(col) },
                Timestamp = new Value { RawTimestampMicros = 0 },
                Input = new Value { IntValue = value }
            }
        };
    }

    #region Sum aggregator

    [Fact]
    public async Task Sum_single_add()
    {
        await Client.MutateRowAsync(TN, "sum-1", AddToCell(SUM_CF, "counter", 10));
        var val = await ReadAggValue("sum-1", SUM_CF, "counter");
        val.Should().Be(10);
    }

    [Fact]
    public async Task Sum_multiple_adds()
    {
        await Client.MutateRowAsync(TN, "sum-multi", AddToCell(SUM_CF, "counter", 5));
        await Client.MutateRowAsync(TN, "sum-multi", AddToCell(SUM_CF, "counter", 3));
        await Client.MutateRowAsync(TN, "sum-multi", AddToCell(SUM_CF, "counter", 7));
        var val = await ReadAggValue("sum-multi", SUM_CF, "counter");
        val.Should().Be(15);
    }

    [Fact]
    public async Task Sum_negative_values()
    {
        await Client.MutateRowAsync(TN, "sum-neg", AddToCell(SUM_CF, "counter", 100));
        await Client.MutateRowAsync(TN, "sum-neg", AddToCell(SUM_CF, "counter", -30));
        var val = await ReadAggValue("sum-neg", SUM_CF, "counter");
        val.Should().Be(70);
    }

    [Fact]
    public async Task Sum_from_zero()
    {
        // First add to a non-existent cell
        await Client.MutateRowAsync(TN, "sum-zero", AddToCell(SUM_CF, "counter", 42));
        var val = await ReadAggValue("sum-zero", SUM_CF, "counter");
        val.Should().Be(42);
    }

    [Fact]
    public async Task Sum_multiple_columns()
    {
        await Client.MutateRowAsync(TN, "sum-cols",
            AddToCell(SUM_CF, "a", 10),
            AddToCell(SUM_CF, "b", 20),
            AddToCell(SUM_CF, "c", 30));
        (await ReadAggValue("sum-cols", SUM_CF, "a")).Should().Be(10);
        (await ReadAggValue("sum-cols", SUM_CF, "b")).Should().Be(20);
        (await ReadAggValue("sum-cols", SUM_CF, "c")).Should().Be(30);
    }

    [Fact]
    public async Task Sum_add_zero()
    {
        await Client.MutateRowAsync(TN, "sum-add0", AddToCell(SUM_CF, "c", 100));
        await Client.MutateRowAsync(TN, "sum-add0", AddToCell(SUM_CF, "c", 0));
        var val = await ReadAggValue("sum-add0", SUM_CF, "c");
        val.Should().Be(100);
    }

    #endregion

    #region Min aggregator

    [Fact]
    public async Task Min_single_value()
    {
        await Client.MutateRowAsync(TN, "min-1", AddToCell(MIN_CF, "c", 50));
        var val = await ReadAggValue("min-1", MIN_CF, "c");
        val.Should().Be(50);
    }

    [Fact]
    public async Task Min_smaller_value_replaces()
    {
        await Client.MutateRowAsync(TN, "min-replace", AddToCell(MIN_CF, "c", 50));
        await Client.MutateRowAsync(TN, "min-replace", AddToCell(MIN_CF, "c", 30));
        var val = await ReadAggValue("min-replace", MIN_CF, "c");
        val.Should().Be(30);
    }

    [Fact]
    public async Task Min_larger_value_does_not_replace()
    {
        await Client.MutateRowAsync(TN, "min-keep", AddToCell(MIN_CF, "c", 30));
        await Client.MutateRowAsync(TN, "min-keep", AddToCell(MIN_CF, "c", 50));
        var val = await ReadAggValue("min-keep", MIN_CF, "c");
        val.Should().Be(30);
    }

    [Fact]
    public async Task Min_negative_values()
    {
        await Client.MutateRowAsync(TN, "min-neg", AddToCell(MIN_CF, "c", -10));
        await Client.MutateRowAsync(TN, "min-neg", AddToCell(MIN_CF, "c", -20));
        var val = await ReadAggValue("min-neg", MIN_CF, "c");
        val.Should().Be(-20);
    }

    [Fact]
    public async Task Min_equal_values()
    {
        await Client.MutateRowAsync(TN, "min-eq", AddToCell(MIN_CF, "c", 42));
        await Client.MutateRowAsync(TN, "min-eq", AddToCell(MIN_CF, "c", 42));
        var val = await ReadAggValue("min-eq", MIN_CF, "c");
        val.Should().Be(42);
    }

    #endregion

    #region Max aggregator

    [Fact]
    public async Task Max_single_value()
    {
        await Client.MutateRowAsync(TN, "max-1", AddToCell(MAX_CF, "c", 50));
        var val = await ReadAggValue("max-1", MAX_CF, "c");
        val.Should().Be(50);
    }

    [Fact]
    public async Task Max_larger_value_replaces()
    {
        await Client.MutateRowAsync(TN, "max-replace", AddToCell(MAX_CF, "c", 30));
        await Client.MutateRowAsync(TN, "max-replace", AddToCell(MAX_CF, "c", 50));
        var val = await ReadAggValue("max-replace", MAX_CF, "c");
        val.Should().Be(50);
    }

    [Fact]
    public async Task Max_smaller_value_does_not_replace()
    {
        await Client.MutateRowAsync(TN, "max-keep", AddToCell(MAX_CF, "c", 50));
        await Client.MutateRowAsync(TN, "max-keep", AddToCell(MAX_CF, "c", 30));
        var val = await ReadAggValue("max-keep", MAX_CF, "c");
        val.Should().Be(50);
    }

    [Fact]
    public async Task Max_negative_values()
    {
        await Client.MutateRowAsync(TN, "max-neg", AddToCell(MAX_CF, "c", -20));
        await Client.MutateRowAsync(TN, "max-neg", AddToCell(MAX_CF, "c", -10));
        var val = await ReadAggValue("max-neg", MAX_CF, "c");
        val.Should().Be(-10);
    }

    #endregion

    #region SetCell on aggregate family should fail

    [Fact]
    public async Task SetCell_on_aggregate_family_throws()
    {
        var act = () => Client.MutateRowAsync(TN, "agg-setcell",
            Mutations.SetCell(SUM_CF, "c", "v", new BigtableVersion(1000)));
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    #endregion

    #region Delete operations on aggregate families

    [Fact]
    public async Task DeleteFromRow_removes_aggregate_cells()
    {
        await Client.MutateRowAsync(TN, "agg-del-row",
            AddToCell(SUM_CF, "c", 100),
            Mutations.SetCell(REG_CF, "d", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "agg-del-row", Mutations.DeleteFromRow());

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("agg-del-row")))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteFromFamily_on_aggregate_family()
    {
        await Client.MutateRowAsync(TN, "agg-del-fam",
            AddToCell(SUM_CF, "c", 50),
            Mutations.SetCell(REG_CF, "d", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "agg-del-fam", Mutations.DeleteFromFamily(SUM_CF));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("agg-del-fam")))
            rows.Add(row);
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle();
        rows[0].Families[0].Name.Should().Be(REG_CF);
    }

    [Fact]
    public async Task Delete_and_re_aggregate()
    {
        await Client.MutateRowAsync(TN, "agg-re", AddToCell(SUM_CF, "c", 100));
        await Client.MutateRowAsync(TN, "agg-re", Mutations.DeleteFromRow());
        await Client.MutateRowAsync(TN, "agg-re", AddToCell(SUM_CF, "c", 50));
        var val = await ReadAggValue("agg-re", SUM_CF, "c");
        val.Should().Be(50);
    }

    #endregion

    #region Mixed aggregate and regular

    [Fact]
    public async Task Mixed_aggregate_and_regular_in_same_row()
    {
        await Client.MutateRowAsync(TN, "mixed-row",
            AddToCell(SUM_CF, "count", 1),
            Mutations.SetCell(REG_CF, "name", "test", new BigtableVersion(1000)));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("mixed-row")))
            rows.Add(row);
        rows.Should().ContainSingle();
        rows[0].Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Read_aggregate_with_filter()
    {
        await Client.MutateRowAsync(TN, "agg-filter", AddToCell(SUM_CF, "c", 42));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN,
            RowSet.FromRowKeys("agg-filter"),
            RowFilters.FamilyNameExact(SUM_CF)))
            rows.Add(row);
        rows.Should().ContainSingle();
    }

    #endregion

    #region Batch aggregate operations

    [Fact]
    public async Task Batch_add_to_cell()
    {
        var entries = Enumerable.Range(0, 10).Select(i =>
            Mutations.CreateEntry($"batch-agg-{i:D2}", AddToCell(SUM_CF, "c", i * 10))
        ).ToArray();

        await Client.MutateRowsAsync(TN, entries);

        var val = await ReadAggValue("batch-agg-05", SUM_CF, "c");
        val.Should().Be(50);
    }

    #endregion
}

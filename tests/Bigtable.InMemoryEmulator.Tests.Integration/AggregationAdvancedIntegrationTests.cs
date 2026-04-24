using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Advanced aggregation integration tests — Min, Max, MergeToCell, multiple increments,
/// negative values, and edge cases.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
///   Mutation.AddToCell — "Incrementally updates a cell in an Aggregate family"
///   Mutation.MergeToCell — "Merges a cell into an Aggregate family"
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.InMemoryOnly)]
public sealed class AggregationAdvancedIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "agg-adv-tests";
    private const string RegularFamily = "cf";
    private const string SumFamily = "sumf";
    private const string MinFamily = "minf";
    private const string MaxFamily = "maxf";

    public AggregationAdvancedIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync("_agg_init", new[] { "cf" });

        var createRequest = new CreateTableRequest
        {
            Parent = _fixture.InstanceName,
            TableId = Table,
            Table = new Google.Cloud.Bigtable.Admin.V2.Table
            {
                ColumnFamilies =
                {
                    [RegularFamily] = new ColumnFamily(),
                    [SumFamily] = new ColumnFamily
                    {
                        ValueType = CreateInt64AggType(
                            new Google.Cloud.Bigtable.Admin.V2.Type.Types.Aggregate.Types.Sum())
                    },
                    [MinFamily] = new ColumnFamily
                    {
                        ValueType = CreateInt64AggType(null, isMin: true)
                    },
                    [MaxFamily] = new ColumnFamily
                    {
                        ValueType = CreateInt64AggType(null, isMax: true)
                    },
                }
            }
        };
        await _fixture.AdminClient.CreateTableAsync(createRequest);
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private TableName TN => _fixture.GetTableName(Table);
    private BigtableServiceApiClient ServiceApi => _fixture.ServiceApiClient;
    private BigtableClient Client => _fixture.Client;

    private static Google.Cloud.Bigtable.Admin.V2.Type CreateInt64AggType(
        Google.Cloud.Bigtable.Admin.V2.Type.Types.Aggregate.Types.Sum? sum = null,
        bool isMin = false, bool isMax = false)
    {
        var agg = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Aggregate
        {
            StateType = new Google.Cloud.Bigtable.Admin.V2.Type
            {
                Int64Type = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Int64
                {
                    Encoding = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Int64.Types.Encoding
                    {
                        BigEndianBytes = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Int64.Types.Encoding.Types.BigEndianBytes()
                    }
                }
            }
        };
        if (sum != null) agg.Sum = sum;
        else if (isMin) agg.Min = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Aggregate.Types.Min();
        else if (isMax) agg.Max = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Aggregate.Types.Max();
        return new Google.Cloud.Bigtable.Admin.V2.Type { AggregateType = agg };
    }

    private static long ReadBigEndianInt64(ByteString bytes)
    {
        var arr = bytes.ToByteArray();
        if (BitConverter.IsLittleEndian) System.Array.Reverse(arr);
        return BitConverter.ToInt64(arr, 0);
    }

    private async Task AddToCell(string family, string rowKey, string qualifier, long value, long timestampMicros = 1000)
    {
        await ServiceApi.MutateRowAsync(new MutateRowRequest
        {
            TableName = TN.ToString(),
            RowKey = ByteString.CopyFromUtf8(rowKey),
            Mutations =
            {
                new Mutation
                {
                    AddToCell = new Mutation.Types.AddToCell
                    {
                        FamilyName = family,
                        ColumnQualifier = new Value { RawValue = ByteString.CopyFromUtf8(qualifier) },
                        Timestamp = new Value { RawTimestampMicros = timestampMicros },
                        Input = new Value { IntValue = value },
                    }
                }
            }
        });
    }

    private async Task MergeToCell(string family, string rowKey, string qualifier, long value, long timestampMicros = 1000)
    {
        await ServiceApi.MutateRowAsync(new MutateRowRequest
        {
            TableName = TN.ToString(),
            RowKey = ByteString.CopyFromUtf8(rowKey),
            Mutations =
            {
                new Mutation
                {
                    MergeToCell = new Mutation.Types.MergeToCell
                    {
                        FamilyName = family,
                        ColumnQualifier = new Value { RawValue = ByteString.CopyFromUtf8(qualifier) },
                        Timestamp = new Value { RawTimestampMicros = timestampMicros },
                        Input = new Value { IntValue = value },
                    }
                }
            }
        });
    }

    #region Sum aggregation

    [Fact]
    public async Task Sum_single_add()
    {
        await AddToCell(SumFamily, "sum-1", "counter", 42);
        var row = await Client.ReadRowAsync(TN, "sum-1");
        ReadBigEndianInt64(row!.Families[0].Columns[0].Cells[0].Value).Should().Be(42);
    }

    [Fact]
    public async Task Sum_multiple_adds()
    {
        await AddToCell(SumFamily, "sum-multi", "counter", 10);
        await AddToCell(SumFamily, "sum-multi", "counter", 20);
        await AddToCell(SumFamily, "sum-multi", "counter", 30);
        var row = await Client.ReadRowAsync(TN, "sum-multi");
        ReadBigEndianInt64(row!.Families[0].Columns[0].Cells[0].Value).Should().Be(60);
    }

    [Fact]
    public async Task Sum_negative_values()
    {
        await AddToCell(SumFamily, "sum-neg", "counter", 100);
        await AddToCell(SumFamily, "sum-neg", "counter", -30);
        var row = await Client.ReadRowAsync(TN, "sum-neg");
        ReadBigEndianInt64(row!.Families[0].Columns[0].Cells[0].Value).Should().Be(70);
    }

    [Fact]
    public async Task Sum_zero_add()
    {
        await AddToCell(SumFamily, "sum-zero", "counter", 50);
        await AddToCell(SumFamily, "sum-zero", "counter", 0);
        var row = await Client.ReadRowAsync(TN, "sum-zero");
        ReadBigEndianInt64(row!.Families[0].Columns[0].Cells[0].Value).Should().Be(50);
    }

    [Fact]
    public async Task Sum_different_qualifiers_independent()
    {
        await AddToCell(SumFamily, "sum-qual", "a", 10);
        await AddToCell(SumFamily, "sum-qual", "b", 20);
        var row = await Client.ReadRowAsync(TN, "sum-qual");
        var cols = row!.Families[0].Columns.OrderBy(c => c.Qualifier.ToStringUtf8()).ToList();
        ReadBigEndianInt64(cols[0].Cells[0].Value).Should().Be(10);
        ReadBigEndianInt64(cols[1].Cells[0].Value).Should().Be(20);
    }

    #endregion

    #region Min aggregation

    [Fact]
    public async Task Min_single_value()
    {
        await AddToCell(MinFamily, "min-1", "val", 42);
        var row = await Client.ReadRowAsync(TN, "min-1");
        ReadBigEndianInt64(row!.Families[0].Columns[0].Cells[0].Value).Should().Be(42);
    }

    [Fact]
    public async Task Min_takes_smallest()
    {
        await AddToCell(MinFamily, "min-multi", "val", 50);
        await AddToCell(MinFamily, "min-multi", "val", 10);
        await AddToCell(MinFamily, "min-multi", "val", 30);
        var row = await Client.ReadRowAsync(TN, "min-multi");
        ReadBigEndianInt64(row!.Families[0].Columns[0].Cells[0].Value).Should().Be(10);
    }

    [Fact]
    public async Task Min_with_negative()
    {
        await AddToCell(MinFamily, "min-neg", "val", 5);
        await AddToCell(MinFamily, "min-neg", "val", -3);
        await AddToCell(MinFamily, "min-neg", "val", 10);
        var row = await Client.ReadRowAsync(TN, "min-neg");
        ReadBigEndianInt64(row!.Families[0].Columns[0].Cells[0].Value).Should().Be(-3);
    }

    #endregion

    #region Max aggregation

    [Fact]
    public async Task Max_single_value()
    {
        await AddToCell(MaxFamily, "max-1", "val", 42);
        var row = await Client.ReadRowAsync(TN, "max-1");
        ReadBigEndianInt64(row!.Families[0].Columns[0].Cells[0].Value).Should().Be(42);
    }

    [Fact]
    public async Task Max_takes_largest()
    {
        await AddToCell(MaxFamily, "max-multi", "val", 10);
        await AddToCell(MaxFamily, "max-multi", "val", 50);
        await AddToCell(MaxFamily, "max-multi", "val", 30);
        var row = await Client.ReadRowAsync(TN, "max-multi");
        ReadBigEndianInt64(row!.Families[0].Columns[0].Cells[0].Value).Should().Be(50);
    }

    [Fact]
    public async Task Max_with_negative()
    {
        await AddToCell(MaxFamily, "max-neg", "val", -50);
        await AddToCell(MaxFamily, "max-neg", "val", -10);
        await AddToCell(MaxFamily, "max-neg", "val", -30);
        var row = await Client.ReadRowAsync(TN, "max-neg");
        ReadBigEndianInt64(row!.Families[0].Columns[0].Cells[0].Value).Should().Be(-10);
    }

    #endregion

    #region MergeToCell

    [Fact]
    public async Task MergeToCell_sum_accumulates()
    {
        await MergeToCell(SumFamily, "merge-sum", "counter", 10);
        await MergeToCell(SumFamily, "merge-sum", "counter", 20);
        var row = await Client.ReadRowAsync(TN, "merge-sum");
        ReadBigEndianInt64(row!.Families[0].Columns[0].Cells[0].Value).Should().Be(30);
    }

    [Fact]
    public async Task MergeToCell_min()
    {
        await MergeToCell(MinFamily, "merge-min", "val", 100);
        await MergeToCell(MinFamily, "merge-min", "val", 5);
        var row = await Client.ReadRowAsync(TN, "merge-min");
        ReadBigEndianInt64(row!.Families[0].Columns[0].Cells[0].Value).Should().Be(5);
    }

    #endregion

    #region Error cases

    [Fact]
    public async Task SetCell_on_aggregate_family_throws()
    {
        var rk = new BigtableByteString("agg-setcell-err");
        var act = () => Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(SumFamily, "c", "v", new BigtableVersion(1000)));
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task AddToCell_on_non_aggregate_family_throws()
    {
        var act = () => ServiceApi.MutateRowAsync(new MutateRowRequest
        {
            TableName = TN.ToString(),
            RowKey = ByteString.CopyFromUtf8("agg-nonaggreg"),
            Mutations =
            {
                new Mutation
                {
                    AddToCell = new Mutation.Types.AddToCell
                    {
                        FamilyName = RegularFamily,
                        ColumnQualifier = new Value { RawValue = ByteString.CopyFromUtf8("c") },
                        Timestamp = new Value { RawTimestampMicros = 1000 },
                        Input = new Value { IntValue = 10 },
                    }
                }
            }
        });
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    #endregion

    #region GetTable shows aggregate types

    [Fact]
    public async Task GetTable_shows_aggregate_column_families()
    {
        var tablePath = _fixture.InstanceName + "/tables/" + Table;
        var table = await _fixture.AdminClient.GetTableAsync(tablePath);

        table.ColumnFamilies[SumFamily].ValueType.AggregateType.Should().NotBeNull();
        table.ColumnFamilies[SumFamily].ValueType.AggregateType.Sum.Should().NotBeNull();
        table.ColumnFamilies[MinFamily].ValueType.AggregateType.Min.Should().NotBeNull();
        table.ColumnFamilies[MaxFamily].ValueType.AggregateType.Max.Should().NotBeNull();
    }

    #endregion
}

using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Integration tests for aggregate column families (AddToCell / MergeToCell mutations)
/// through the full gRPC pipeline.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
///   Mutation.AddToCell — "Incrementally updates a cell in an Aggregate family"
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.InMemoryOnly)]
public sealed class AggregationIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "agg-tests";
    private const string AggFamily = "aggf";

    public AggregationIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        // First call to CreateTableAsync initializes the fixture (lazy init pattern)
        await _fixture.CreateTableAsync("_init", new[] { "cf" });

        // Create table with an aggregate Sum column family via the Admin API
        // Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#columnfamily
        var createRequest = new CreateTableRequest
        {
            Parent = _fixture.InstanceName,
            TableId = Table,
            Table = new Google.Cloud.Bigtable.Admin.V2.Table
            {
                ColumnFamilies =
                {
                    [AggFamily] = new ColumnFamily
                    {
                        ValueType = new Google.Cloud.Bigtable.Admin.V2.Type
                        {
                            AggregateType = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Aggregate
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
                                },
                                Sum = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Aggregate.Types.Sum()
                            }
                        }
                    }
                }
            }
        };
        await _fixture.AdminClient.CreateTableAsync(createRequest);
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private TableName TN => _fixture.GetTableName(Table);
    private BigtableServiceApiClient ServiceApi => _fixture.ServiceApiClient;

    private static ByteString WriteBigEndianInt64(long value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
            System.Array.Reverse(bytes);
        return ByteString.CopyFrom(bytes);
    }

    private static long ReadBigEndianInt64(ByteString bytes)
    {
        var arr = bytes.ToByteArray();
        if (BitConverter.IsLittleEndian)
            System.Array.Reverse(arr);
        return BitConverter.ToInt64(arr, 0);
    }

    [Fact]
    public async Task AddToCell_Sum_accumulates_values()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
        //   AddToCell — "Incrementally updates a cell in an Aggregate family"
        var rowKey = new BigtableByteString("agg-sum-row");
        var qualifier = ByteString.CopyFromUtf8("counter");

        // Add 10
        await ServiceApi.MutateRowAsync(new MutateRowRequest
        {
            TableName = TN.ToString(),
            RowKey = rowKey.Value,
            Mutations =
            {
                new Mutation
                {
                    AddToCell = new Mutation.Types.AddToCell
                    {
                        FamilyName = AggFamily,
                        ColumnQualifier = new Value { RawValue = qualifier },
                        Timestamp = new Value { RawTimestampMicros = 1000 },
                        Input = new Value { IntValue = 10 },
                    }
                }
            }
        });

        // Add 25
        await ServiceApi.MutateRowAsync(new MutateRowRequest
        {
            TableName = TN.ToString(),
            RowKey = rowKey.Value,
            Mutations =
            {
                new Mutation
                {
                    AddToCell = new Mutation.Types.AddToCell
                    {
                        FamilyName = AggFamily,
                        ColumnQualifier = new Value { RawValue = qualifier },
                        Timestamp = new Value { RawTimestampMicros = 1000 },
                        Input = new Value { IntValue = 25 },
                    }
                }
            }
        });

        // Read back via high-level client
        var row = await _fixture.Client.ReadRowAsync(TN, rowKey);
        row.Should().NotBeNull();
        var cell = row!.Families[0].Columns[0].Cells[0];
        ReadBigEndianInt64(cell.Value).Should().Be(35);
    }

    [Fact]
    public async Task AddToCell_on_non_aggregate_family_returns_error()
    {
        // Create a regular (non-aggregate) family table
        await _fixture.CreateTableAsync("agg-err", new[] { "cf" });
        var tn = _fixture.GetTableName("agg-err");
        var rowKey = new BigtableByteString("err-row");

        // AddToCell on a regular family should return INVALID_ARGUMENT
        var ex = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () =>
        {
            await ServiceApi.MutateRowAsync(new MutateRowRequest
            {
                TableName = tn.ToString(),
                RowKey = rowKey.Value,
                Mutations =
                {
                    new Mutation
                    {
                        AddToCell = new Mutation.Types.AddToCell
                        {
                            FamilyName = "cf",
                            ColumnQualifier = new Value { RawValue = ByteString.CopyFromUtf8("col") },
                            Timestamp = new Value { RawTimestampMicros = 1000 },
                            Input = new Value { IntValue = 1 },
                        }
                    }
                }
            });
        });
        ex.StatusCode.Should().Be(Grpc.Core.StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task GetTable_returns_aggregate_family_with_value_type()
    {
        // Verify the admin API returns the aggregate type info
        var tableName = $"{_fixture.InstanceName}/tables/{Table}";
        var table = await _fixture.AdminClient.GetTableAsync(new GetTableRequest { Name = tableName });

        table.ColumnFamilies.Should().ContainKey(AggFamily);
        var cf = table.ColumnFamilies[AggFamily];
        cf.ValueType.Should().NotBeNull();
        cf.ValueType.AggregateType.Should().NotBeNull();
        cf.ValueType.AggregateType.AggregatorCase.Should().Be(
            Google.Cloud.Bigtable.Admin.V2.Type.Types.Aggregate.AggregatorOneofCase.Sum);
    }
}

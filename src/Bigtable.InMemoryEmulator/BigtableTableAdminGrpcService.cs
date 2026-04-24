using Google.Cloud.Bigtable.Admin.V2;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Bigtable.InMemoryEmulator;

/// <summary>
/// In-process gRPC service implementing the Bigtable Table Admin API.
/// Handles table CRUD and column family management.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#google.bigtable.admin.v2.BigtableTableAdmin
/// </summary>
internal sealed class BigtableTableAdminGrpcService : BigtableTableAdmin.BigtableTableAdminBase
{
    private readonly InMemoryBigtableStore _store;
    private readonly string _projectId;
    private readonly string _instanceId;

    public BigtableTableAdminGrpcService(InMemoryBigtableStore store,
        string projectId = "test-project", string instanceId = "test-instance")
    {
        _store = store;
        _projectId = projectId;
        _instanceId = instanceId;
    }

    /// <summary>
    /// Creates a new table.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#createtablerequest
    /// </summary>
    public override Task<Table> CreateTable(
        CreateTableRequest request,
        ServerCallContext context)
    {
        var tableId = request.TableId;
        if (string.IsNullOrEmpty(tableId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "table_id must not be empty."));
        }

        var families = new List<string>();
        var gcRules = new Dictionary<string, GcRule?>();
        var aggregateFamilies = new Dictionary<string, AggregateConfig>();

        if (request.Table?.ColumnFamilies != null)
        {
            foreach (var (name, cf) in request.Table.ColumnFamilies)
            {
                // Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#columnfamily
                //   ColumnFamily.value_type — if set with aggregate_type, the family is an aggregate family.
                if (cf.ValueType?.AggregateType != null)
                {
                    var aggConfig = ParseAggregateConfig(cf.ValueType.AggregateType);
                    aggregateFamilies[name] = aggConfig;
                }
                else
                {
                    families.Add(name);
                    gcRules[name] = cf.GcRule?.RuleCase != GcRule.RuleOneofCase.None ? cf.GcRule : null;
                }
            }
        }

        if (aggregateFamilies.Count > 0)
        {
            _store.CreateTableWithAggregates(tableId, families, aggregateFamilies);
            // Apply GC rules to regular families after creation
            if (gcRules.Count > 0)
            {
                var table = _store.GetTable(tableId);
                foreach (var (family, gcRule) in gcRules)
                {
                    if (gcRule != null)
                        table.Config.ColumnFamilies[family] = gcRule;
                }
            }
        }
        else
        {
            _store.CreateTable(tableId, families, gcRules.Count > 0 ? gcRules : null);
        }

        return Task.FromResult(BuildTableProto(tableId));
    }

    /// <summary>
    /// Gets a table.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#gettablerequest
    /// </summary>
    public override Task<Table> GetTable(
        GetTableRequest request,
        ServerCallContext context)
    {
        var tableId = ExtractTableName(request.Name);
        _store.GetTable(tableId); // Verify it exists (throws NOT_FOUND)

        return Task.FromResult(BuildTableProto(tableId));
    }

    /// <summary>
    /// Deletes a table.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#deletetablerequest
    /// </summary>
    public override Task<Empty> DeleteTable(
        DeleteTableRequest request,
        ServerCallContext context)
    {
        var tableId = ExtractTableName(request.Name);
        _store.DeleteTable(tableId);
        return Task.FromResult(new Empty());
    }

    /// <summary>
    /// Lists all tables in the instance.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#listtablesrequest
    /// </summary>
    public override Task<ListTablesResponse> ListTables(
        ListTablesRequest request,
        ServerCallContext context)
    {
        var tableNames = _store.ListTables();
        var response = new ListTablesResponse();

        foreach (var name in tableNames)
        {
            response.Tables.Add(BuildTableProto(name));
        }

        return Task.FromResult(response);
    }

    /// <summary>
    /// Modifies column families on a table.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#modifycolumnfamiliesrequest
    /// </summary>
    public override Task<Table> ModifyColumnFamilies(
        ModifyColumnFamiliesRequest request,
        ServerCallContext context)
    {
        var tableId = ExtractTableName(request.Name);
        var modifications = new List<(string FamilyId, InMemoryBigtableStore.ModifyAction Action, GcRule? GcRule)>();

        foreach (var mod in request.Modifications)
        {
            var familyId = mod.Id;

            switch (mod.ModCase)
            {
                case ModifyColumnFamiliesRequest.Types.Modification.ModOneofCase.Create:
                    var gcRule = mod.Create.GcRule?.RuleCase != GcRule.RuleOneofCase.None
                        ? mod.Create.GcRule : null;
                    modifications.Add((familyId, InMemoryBigtableStore.ModifyAction.Create, gcRule));
                    break;

                case ModifyColumnFamiliesRequest.Types.Modification.ModOneofCase.Update:
                    var updateGcRule = mod.Update.GcRule?.RuleCase != GcRule.RuleOneofCase.None
                        ? mod.Update.GcRule : null;
                    modifications.Add((familyId, InMemoryBigtableStore.ModifyAction.Update, updateGcRule));
                    break;

                case ModifyColumnFamiliesRequest.Types.Modification.ModOneofCase.Drop:
                    if (mod.Drop)
                    {
                        modifications.Add((familyId, InMemoryBigtableStore.ModifyAction.Drop, null));
                    }
                    break;
            }
        }

        _store.ModifyColumnFamilies(tableId, modifications);

        return Task.FromResult(BuildTableProto(tableId));
    }

    /// <summary>
    /// Builds a Table proto from internal state.
    /// </summary>
    private Table BuildTableProto(string tableId)
    {
        var table = _store.GetTable(tableId);
        var result = new Table
        {
            Name = $"projects/{_projectId}/instances/{_instanceId}/tables/{tableId}",
        };

        foreach (var (familyName, gcRule) in table.Config.ColumnFamilies)
        {
            var cf = new ColumnFamily();
            if (gcRule != null)
            {
                cf.GcRule = gcRule;
            }
            result.ColumnFamilies.Add(familyName, cf);
        }

        foreach (var (familyName, aggConfig) in table.Config.AggregateFamilies)
        {
            result.ColumnFamilies.Add(familyName, new ColumnFamily
            {
                ValueType = BuildAggregateValueType(aggConfig),
            });
        }

        return result;
    }

    /// <summary>
    /// Parses a ColumnFamily.ValueType.AggregateType proto into our internal AggregateConfig.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#type
    /// </summary>
    private static AggregateConfig ParseAggregateConfig(Google.Cloud.Bigtable.Admin.V2.Type.Types.Aggregate aggregate)
    {
        return aggregate.AggregatorCase switch
        {
            Google.Cloud.Bigtable.Admin.V2.Type.Types.Aggregate.AggregatorOneofCase.Sum => AggregateConfig.Sum(),
            Google.Cloud.Bigtable.Admin.V2.Type.Types.Aggregate.AggregatorOneofCase.Min => AggregateConfig.Min(),
            Google.Cloud.Bigtable.Admin.V2.Type.Types.Aggregate.AggregatorOneofCase.Max => AggregateConfig.Max(),
            Google.Cloud.Bigtable.Admin.V2.Type.Types.Aggregate.AggregatorOneofCase.HllppUniqueCount
                => AggregateConfig.HllppUniqueCount(),
            _ => AggregateConfig.Sum(), // Default to Sum if unspecified
        };
    }

    /// <summary>
    /// Converts an AggregateConfig back to a ValueType proto for table metadata responses.
    /// </summary>
    private static Google.Cloud.Bigtable.Admin.V2.Type BuildAggregateValueType(AggregateConfig config)
    {
        var aggregate = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Aggregate
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
        };

        switch (config.Aggregator)
        {
            case AggregatorType.Sum:
                aggregate.Sum = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Aggregate.Types.Sum();
                break;
            case AggregatorType.Min:
                aggregate.Min = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Aggregate.Types.Min();
                break;
            case AggregatorType.Max:
                aggregate.Max = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Aggregate.Types.Max();
                break;
            case AggregatorType.HllppUniqueCount:
                aggregate.HllppUniqueCount = new Google.Cloud.Bigtable.Admin.V2.Type.Types.Aggregate.Types.HyperLogLogPlusPlusUniqueCount();
                break;
        }

        return new Google.Cloud.Bigtable.Admin.V2.Type { AggregateType = aggregate };
    }

    /// <summary>
    /// Extracts the short table name from a fully-qualified resource name.
    /// Delegates to shared Superpower-based ResourceNameParser.
    /// </summary>
    private static string ExtractTableName(string resourceName)
        => ResourceNameParser.ExtractTableName(resourceName);
}

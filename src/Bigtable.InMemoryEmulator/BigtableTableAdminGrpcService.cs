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

        if (request.Table?.ColumnFamilies != null)
        {
            foreach (var (name, cf) in request.Table.ColumnFamilies)
            {
                families.Add(name);
                gcRules[name] = cf.GcRule?.RuleCase != GcRule.RuleOneofCase.None ? cf.GcRule : null;
            }
        }

        _store.CreateTable(tableId, families, gcRules.Count > 0 ? gcRules : null);

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

        foreach (var (familyName, _) in table.Config.AggregateFamilies)
        {
            result.ColumnFamilies.Add(familyName, new ColumnFamily());
        }

        return result;
    }

    /// <summary>
    /// Extracts the short table name from a fully-qualified resource name.
    /// Format: "projects/{project}/instances/{instance}/tables/{table}"
    /// </summary>
    private static string ExtractTableName(string resourceName)
    {
        if (string.IsNullOrEmpty(resourceName))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Table name must not be empty."));
        }

        var parts = resourceName.Split('/');
        if (parts.Length >= 6 && parts[4] == "tables")
        {
            return parts[5];
        }

        return resourceName;
    }
}

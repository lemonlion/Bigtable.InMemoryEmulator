using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;

namespace InMemoryEmulator.Bigtable.Tests.Infrastructure;

/// <summary>
/// ITestTableFixture implementation backed by a real GCP Bigtable instance.
/// Uses Application Default Credentials (ADC) or Workload Identity Federation (WIF) in CI.
///
/// Ref: https://cloud.google.com/bigtable/docs/authentication
///   Uses default credentials from GOOGLE_APPLICATION_CREDENTIALS or WIF.
///
/// Table names are suffixed with a GUID to avoid collisions across parallel test runs.
/// Tables are cleaned up in DisposeAsync.
/// </summary>
public sealed class GcpTestFixture : ITestTableFixture
{
    private readonly string _projectId;
    private readonly string _instanceId;
    private BigtableClient? _client;
    private BigtableTableAdminClient? _adminClient;
    private BigtableServiceApiClient? _serviceApiClient;
    private readonly List<string> _createdTables = [];
    private readonly string _tableNameSuffix = Guid.NewGuid().ToString("N")[..8];

    public GcpTestFixture(string projectId, string instanceId)
    {
        _projectId = projectId;
        _instanceId = instanceId;
    }

    public BigtableClient Client
    {
        get
        {
            _client ??= new BigtableClientBuilder().Build();
            return _client;
        }
    }

    public BigtableTableAdminClient AdminClient
    {
        get
        {
            _adminClient ??= new BigtableTableAdminClientBuilder().Build();
            return _adminClient;
        }
    }

    public BigtableServiceApiClient ServiceApiClient
    {
        get
        {
            _serviceApiClient ??= new BigtableServiceApiClientBuilder().Build();
            return _serviceApiClient;
        }
    }

    public string InstanceName => $"projects/{_projectId}/instances/{_instanceId}";

    public TestTarget Target => TestTarget.Gcp;

    public TableName GetTableName(string tableName)
    {
        // Suffix with GUID to avoid collisions in parallel runs
        return new TableName(_projectId, _instanceId, $"{tableName}-{_tableNameSuffix}");
    }

    public async Task CreateTableAsync(string tableName, IEnumerable<string> columnFamilies)
    {
        var instanceName = new InstanceName(_projectId, _instanceId);
        var qualifiedName = $"{tableName}-{_tableNameSuffix}";
        var table = new Table();
        foreach (var cf in columnFamilies)
        {
            table.ColumnFamilies.Add(cf, new ColumnFamily());
        }

        try
        {
            await AdminClient.CreateTableAsync(instanceName, qualifiedName, table);
            _createdTables.Add(qualifiedName);
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.AlreadyExists)
        {
            // Table already exists — clear rows instead
            await ClearRowsAsync(tableName);
        }
    }

    public async Task ClearRowsAsync(string tableName)
    {
        var tn = GetTableName(tableName);
        var rows = Client.ReadRows(tn);
        await foreach (var row in rows)
        {
            await Client.MutateRowAsync(tn, row.Key, Mutations.DeleteFromRow());
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Clean up tables created during the test
        foreach (var qualifiedName in _createdTables)
        {
            try
            {
                await AdminClient.DeleteTableAsync(
                    new TableName(_projectId, _instanceId, qualifiedName));
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }
}

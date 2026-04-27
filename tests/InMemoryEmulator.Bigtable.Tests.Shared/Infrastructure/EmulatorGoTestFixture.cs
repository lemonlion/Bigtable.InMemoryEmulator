using Google.Api.Gax;
using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;

namespace InMemoryEmulator.Bigtable.Tests.Infrastructure;

/// <summary>
/// ITestTableFixture implementation backed by Google's Go Bigtable emulator.
/// Connects via BIGTABLE_EMULATOR_HOST environment variable.
///
/// Ref: https://cloud.google.com/bigtable/docs/emulator
///   "gcloud beta emulators bigtable start --host-port=0.0.0.0:8086"
///
/// Note: The Go emulator does NOT support ExecuteQuery/GoogleSQL, ReadChangeStream,
/// or the Sink filter. Tests for these features should be tagged InMemoryOnly or GcpOnly.
/// </summary>
public sealed class EmulatorGoTestFixture : ITestTableFixture
{
    private readonly string _emulatorHost;
    private readonly string _projectId;
    private readonly string _instanceId;
    private BigtableClient? _client;
    private BigtableTableAdminClient? _adminClient;
    private BigtableServiceApiClient? _serviceApiClient;
    private readonly List<string> _createdTables = [];

    public EmulatorGoTestFixture(string emulatorHost, string projectId, string instanceId)
    {
        _emulatorHost = emulatorHost;
        _projectId = projectId;
        _instanceId = instanceId;
    }

    public BigtableClient Client
    {
        get
        {
            _client ??= new BigtableClientBuilder
            {
                EmulatorDetection = EmulatorDetection.EmulatorOnly,
            }.Build();
            return _client;
        }
    }

    public BigtableTableAdminClient AdminClient
    {
        get
        {
            _adminClient ??= new BigtableTableAdminClientBuilder
            {
                EmulatorDetection = EmulatorDetection.EmulatorOnly,
            }.Build();
            return _adminClient;
        }
    }

    public BigtableServiceApiClient ServiceApiClient
    {
        get
        {
            _serviceApiClient ??= new BigtableServiceApiClientBuilder
            {
                EmulatorDetection = EmulatorDetection.EmulatorOnly,
            }.Build();
            return _serviceApiClient;
        }
    }

    public string InstanceName => $"projects/{_projectId}/instances/{_instanceId}";

    public TestTarget Target => TestTarget.EmulatorGo;

    public TableName GetTableName(string tableName)
    {
        return new TableName(_projectId, _instanceId, tableName);
    }

    public async Task CreateTableAsync(string tableName, IEnumerable<string> columnFamilies)
    {
        var instanceName = new InstanceName(_projectId, _instanceId);
        var table = new Table();
        foreach (var cf in columnFamilies)
        {
            table.ColumnFamilies.Add(cf, new ColumnFamily());
        }

        try
        {
            await AdminClient.CreateTableAsync(instanceName, tableName, table);
            _createdTables.Add(tableName);
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.AlreadyExists)
        {
            // Table already exists — clear rows instead
            await ClearRowsAsync(tableName);
        }
    }

    public async Task ClearRowsAsync(string tableName)
    {
        // The Go emulator supports DropRowRange for clearing all rows
        var tn = GetTableName(tableName);
        // Read all rows and delete them
        var rows = Client.ReadRows(tn);
        await foreach (var row in rows)
        {
            await Client.MutateRowAsync(tn, row.Key, Mutations.DeleteFromRow());
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Clean up tables created during the test
        foreach (var tableName in _createdTables)
        {
            try
            {
                await AdminClient.DeleteTableAsync(
                    new TableName(_projectId, _instanceId, tableName));
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }
}

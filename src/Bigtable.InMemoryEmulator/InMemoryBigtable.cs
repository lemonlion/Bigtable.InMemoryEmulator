using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;

namespace Bigtable.InMemoryEmulator;

/// <summary>
/// Main entry point for creating in-memory Bigtable instances for testing.
/// Provides static factory methods and a fluent builder.
///
/// Quick usage:
///   var result = InMemoryBigtable.Create("my-table", ["cf1", "cf2"]);
///   BigtableClient client = result.Client;
///
/// Builder usage:
///   var result = InMemoryBigtable.Builder()
///       .AddTable("users", ["profile", "activity"])
///       .AddTable("events", ["data"], gc => gc.MaxVersions("data", 5))
///       .ProjectId("my-project")
///       .InstanceId("my-instance")
///       .Build();
/// </summary>
public static class InMemoryBigtable
{
    /// <summary>
    /// Creates an in-memory Bigtable with a single table and the specified column families.
    /// </summary>
    public static InMemoryBigtableResult Create(string tableName, IEnumerable<string> columnFamilies)
    {
        return Builder()
            .AddTable(tableName, columnFamilies)
            .Build();
    }

    /// <summary>
    /// Creates a fluent builder for configuring multiple tables and advanced options.
    /// </summary>
    public static InMemoryBigtableBuilder Builder() => new();
}

/// <summary>
/// Fluent builder for configuring an in-memory Bigtable instance.
/// </summary>
public sealed class InMemoryBigtableBuilder
{
    private string _projectId = "test-project";
    private string _instanceId = "test-instance";
    private string? _statePersistenceDirectory;
    private readonly List<TableDefinition> _tables = [];

    /// <summary>
    /// Sets the project ID used in TableName resources.
    /// </summary>
    public InMemoryBigtableBuilder ProjectId(string projectId)
    {
        _projectId = projectId;
        return this;
    }

    /// <summary>
    /// Sets the instance ID used in TableName resources.
    /// </summary>
    public InMemoryBigtableBuilder InstanceId(string instanceId)
    {
        _instanceId = instanceId;
        return this;
    }

    /// <summary>
    /// Adds a table with the specified column families.
    /// </summary>
    public InMemoryBigtableBuilder AddTable(string tableName, IEnumerable<string> columnFamilies)
    {
        _tables.Add(new TableDefinition(tableName, columnFamilies.ToList(), null));
        return this;
    }

    /// <summary>
    /// Adds a table with column families and GC rule configuration.
    /// </summary>
    public InMemoryBigtableBuilder AddTable(string tableName, IEnumerable<string> columnFamilies, Action<GcRuleBuilder> gcConfigure)
    {
        var gcBuilder = new GcRuleBuilder();
        gcConfigure(gcBuilder);
        _tables.Add(new TableDefinition(tableName, columnFamilies.ToList(), gcBuilder.Build()));
        return this;
    }

    /// <summary>
    /// Sets the directory for automatic state persistence.
    /// State is loaded on startup (if file exists) and saved on dispose.
    /// </summary>
    public InMemoryBigtableBuilder StatePersistenceDirectory(string directory)
    {
        _statePersistenceDirectory = directory;
        return this;
    }

    /// <summary>
    /// Builds the in-memory Bigtable instance.
    /// </summary>
    public InMemoryBigtableResult Build()
    {
        var store = new InMemoryBigtableStore();

        foreach (var table in _tables)
        {
            store.CreateTable(table.Name, table.Families, table.GcRules);
        }

        var server = InMemoryBigtableServer.Create(store);

        var result = new InMemoryBigtableResult(server, _projectId, _instanceId, _statePersistenceDirectory);

        // Auto-load state from persistence directory if it exists
        if (_statePersistenceDirectory != null)
        {
            var stateFile = Path.Combine(_statePersistenceDirectory, "bigtable-state.json");
            if (File.Exists(stateFile))
            {
                StatePersistence.ImportStateFromFile(store, stateFile);
            }
        }

        return result;
    }

    private sealed record TableDefinition(string Name, List<string> Families, Dictionary<string, GcRule?>? GcRules);
}

/// <summary>
/// Builder for configuring GC rules on column families.
/// </summary>
public sealed class GcRuleBuilder
{
    private readonly Dictionary<string, GcRule?> _rules = new();

    /// <summary>
    /// Sets MaxVersions GC rule for a column family.
    /// </summary>
    public GcRuleBuilder MaxVersions(string family, int maxVersions)
    {
        _rules[family] = new GcRule { MaxNumVersions = maxVersions };
        return this;
    }

    /// <summary>
    /// Sets MaxAge GC rule for a column family.
    /// </summary>
    public GcRuleBuilder MaxAge(string family, TimeSpan maxAge)
    {
        _rules[family] = new GcRule { MaxAge = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(maxAge) };
        return this;
    }

    internal Dictionary<string, GcRule?> Build() => _rules;
}

/// <summary>
/// Result object returned by InMemoryBigtable.Create() and Builder().Build().
/// Provides access to the BigtableClient, store, and test helper methods.
/// Implements IDisposable to clean up the in-process gRPC server.
/// </summary>
public sealed class InMemoryBigtableResult : IDisposable
{
    private readonly InMemoryBigtableServer _server;
    private readonly string? _statePersistenceDirectory;

    internal InMemoryBigtableResult(InMemoryBigtableServer server, string projectId, string instanceId,
        string? statePersistenceDirectory = null)
    {
        _server = server;
        ProjectId = projectId;
        InstanceId = instanceId;
        _statePersistenceDirectory = statePersistenceDirectory;
    }

    /// <summary>
    /// The project ID for this emulator instance.
    /// </summary>
    public string ProjectId { get; }

    /// <summary>
    /// The instance ID for this emulator instance.
    /// </summary>
    public string InstanceId { get; }

    /// <summary>
    /// The gRPC channel for creating additional clients (e.g., BigtableServiceApiClient).
    /// </summary>
    public Grpc.Net.Client.GrpcChannel Channel => _server.Channel;

    /// <summary>
    /// The in-memory BigtableClient. Use this in production code under test.
    /// This is a real BigtableClient backed by an in-process gRPC server, providing
    /// full SDK fidelity (row assembly, retry logic, streaming).
    /// </summary>
    public Google.Cloud.Bigtable.V2.BigtableClient Client => _server.Client;

    /// <summary>
    /// The in-memory BigtableTableAdminClient for table management (create, delete, modify).
    /// </summary>
    public Google.Cloud.Bigtable.Admin.V2.BigtableTableAdminClient AdminClient =>
        new Google.Cloud.Bigtable.Admin.V2.BigtableTableAdminClientBuilder
        {
            CallInvoker = _server.Channel.CreateCallInvoker()
        }.Build();

    /// <summary>
    /// Gets an InstanceName resource for this emulator instance.
    /// </summary>
    public Google.Cloud.Bigtable.Common.V2.InstanceName GetInstanceName()
    {
        return new Google.Cloud.Bigtable.Common.V2.InstanceName(ProjectId, InstanceId);
    }

    /// <summary>
    /// Gets the backing in-memory store for direct inspection in tests.
    /// </summary>
    internal InMemoryBigtableStore Store => _server.Store;

    /// <summary>
    /// Fault injector for simulating gRPC errors (UNAVAILABLE, DEADLINE_EXCEEDED, etc.).
    /// </summary>
    public FaultInjector FaultInjector => _server.FaultInjector;

    /// <summary>
    /// Log of all gRPC requests processed by the server.
    /// </summary>
    public RpcLog RpcLog => _server.RpcLog;

    /// <summary>
    /// Log of all SQL queries executed via ExecuteQuery.
    /// </summary>
    public QueryLog QueryLog => _server.QueryLog;

    /// <summary>
    /// Gets a test setup interface for the specified table.
    /// </summary>
    public ITableTestSetup SetupTable(string tableName)
    {
        var table = _server.Store.GetTable(tableName);
        var stateFile = _statePersistenceDirectory != null
            ? Path.Combine(_statePersistenceDirectory, "bigtable-state.json")
            : null;
        return new InMemoryTableTestSetup(table, stateFile);
    }

    /// <summary>
    /// Gets a TableName resource for the specified table.
    /// </summary>
    public TableName GetTableName(string tableName)
    {
        return new TableName(ProjectId, InstanceId, tableName);
    }

    /// <summary>
    /// Clears all rows from the specified table (or all tables if null).
    /// Useful for test cleanup between tests sharing a fixture.
    /// </summary>
    public void ClearRows(string? tableName = null)
    {
        if (tableName != null)
        {
            _server.Store.GetTable(tableName).ClearRows();
        }
        else
        {
            foreach (var name in _server.Store.ListTables())
            {
                _server.Store.GetTable(name).ClearRows();
            }
        }
    }

    /// <summary>
    /// Gets the number of rows in the specified table.
    /// </summary>
    public int RowCount(string tableName)
    {
        return _server.Store.GetTable(tableName).RowCount;
    }

    /// <summary>
    /// Exports the state of all tables as a JSON string.
    /// </summary>
    public string ExportState()
    {
        return StatePersistence.ExportState(_server.Store);
    }

    /// <summary>
    /// Imports state from a JSON string. Tables must already exist. Full replacement.
    /// </summary>
    public void ImportState(string json)
    {
        StatePersistence.ImportState(_server.Store, json);
    }

    /// <summary>
    /// Exports state to a file.
    /// </summary>
    public void ExportStateToFile(string filePath)
    {
        StatePersistence.ExportStateToFile(_server.Store, filePath);
    }

    /// <summary>
    /// Imports state from a file.
    /// </summary>
    public void ImportStateFromFile(string filePath)
    {
        StatePersistence.ImportStateFromFile(_server.Store, filePath);
    }

    public void Dispose()
    {
        // Auto-persist state if directory is configured
        // Ref: Phase 7 plan — "auto-save on dispose, auto-load on create"
        if (_statePersistenceDirectory != null)
        {
            Directory.CreateDirectory(_statePersistenceDirectory);
            var stateFile = Path.Combine(_statePersistenceDirectory, "bigtable-state.json");
            StatePersistence.ExportStateToFile(_server.Store, stateFile);
        }

        _server.Dispose();
    }
}

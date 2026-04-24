using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;

namespace Bigtable.InMemoryEmulator.Tests.Infrastructure;

/// <summary>
/// ITestTableFixture implementation backed by InMemoryBigtable.
/// Uses the in-process gRPC server for full SDK fidelity.
/// </summary>
public sealed class InMemoryTestFixture : ITestTableFixture
{
    private InMemoryBigtableResult? _result;
    private readonly InMemoryBigtableBuilder _builder = InMemoryBigtable.Builder();
    private BigtableServiceApiClient? _serviceApiClient;

    public BigtableClient Client => _result?.Client ?? throw new InvalidOperationException("Fixture not initialized. Call CreateTableAsync first.");

    public BigtableTableAdminClient AdminClient => _result?.AdminClient ?? throw new InvalidOperationException("Fixture not initialized. Call CreateTableAsync first.");

    public BigtableServiceApiClient ServiceApiClient
    {
        get
        {
            if (_result == null) throw new InvalidOperationException("Fixture not initialized.");
            return _serviceApiClient ??= new BigtableServiceApiClientBuilder
            {
                CallInvoker = _result.Channel.CreateCallInvoker()
            }.Build();
        }
    }

    public string InstanceName
    {
        get
        {
            if (_result == null) throw new InvalidOperationException("Fixture not initialized.");
            return $"projects/{_result.ProjectId}/instances/{_result.InstanceId}";
        }
    }

    public TestTarget Target => TestTarget.InMemory;

    public TableName GetTableName(string tableName)
    {
        return _result?.GetTableName(tableName) ?? throw new InvalidOperationException("Fixture not initialized.");
    }

    public Task CreateTableAsync(string tableName, IEnumerable<string> columnFamilies)
    {
        if (_result == null)
        {
            _builder.AddTable(tableName, columnFamilies);
            _result = _builder.Build();
        }
        else
        {
            // Add table to existing store
            _result.Store.CreateTable(tableName, columnFamilies);
        }
        return Task.CompletedTask;
    }

    public Task ClearRowsAsync(string tableName)
    {
        _result?.ClearRows(tableName);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _result?.Dispose();
        return ValueTask.CompletedTask;
    }
}

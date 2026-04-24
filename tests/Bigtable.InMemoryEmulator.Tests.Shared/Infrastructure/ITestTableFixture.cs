using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Grpc.Core;

namespace Bigtable.InMemoryEmulator.Tests.Infrastructure;

/// <summary>
/// Abstraction for creating and interacting with Bigtable tables in tests.
/// Implemented differently per test target:
///   - InMemoryTestFixture (in-process)
///   - EmulatorTestFixture (Go emulator via BIGTABLE_EMULATOR_HOST)
///   - GcpTestFixture (real GCP Bigtable)
/// </summary>
public interface ITestTableFixture : IAsyncDisposable
{
    /// <summary>
    /// The BigtableClient for this test session.
    /// </summary>
    BigtableClient Client { get; }

    /// <summary>
    /// The BigtableTableAdminClient for table management operations.
    /// </summary>
    BigtableTableAdminClient AdminClient { get; }

    /// <summary>
    /// The low-level BigtableServiceApiClient for operations not exposed by BigtableClient.
    /// </summary>
    BigtableServiceApiClient ServiceApiClient { get; }

    /// <summary>
    /// Gets a TableName for the specified table.
    /// </summary>
    TableName GetTableName(string tableName);

    /// <summary>
    /// Gets the instance resource name (projects/{p}/instances/{i}).
    /// </summary>
    string InstanceName { get; }

    /// <summary>
    /// Creates a table with the specified column families.
    /// </summary>
    Task CreateTableAsync(string tableName, IEnumerable<string> columnFamilies);

    /// <summary>
    /// Clears all rows from the specified table.
    /// </summary>
    Task ClearRowsAsync(string tableName);

    /// <summary>
    /// Gets the current test target.
    /// </summary>
    TestTarget Target { get; }
}

/// <summary>
/// Available test targets.
/// </summary>
public enum TestTarget
{
    InMemory,
    EmulatorGo,
    Gcp,
}

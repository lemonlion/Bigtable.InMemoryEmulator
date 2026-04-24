using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.V2;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bigtable.InMemoryEmulator;

/// <summary>
/// DI extension methods for replacing BigtableClient with an in-memory implementation.
///
/// Usage in test setup (e.g., WebApplicationFactory.ConfigureTestServices):
///   services.UseInMemoryBigtable(options => {
///       options.AddTable("my-table", ["cf1", "cf2"]);
///   });
///
/// Ref: Phase 5 plan — "DI Extension Methods (ServiceCollectionExtensions)"
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Replaces any existing BigtableClient registration with an in-memory implementation.
    /// The in-memory client uses a real SDK pipeline backed by an in-process gRPC server.
    /// </summary>
    public static IServiceCollection UseInMemoryBigtable(
        this IServiceCollection services,
        Action<InMemoryBigtableOptions> configure)
    {
        var options = new InMemoryBigtableOptions();
        configure(options);

        // Build the in-memory server
        var store = new InMemoryBigtableStore();
        foreach (var table in options.Tables)
        {
            store.CreateTable(table.Name, table.Families, table.GcRules);
        }

        var server = InMemoryBigtableServer.Create(store);

        // Invoke callbacks
        options.OnClientCreatedCallback?.Invoke(server.Client);
        options.OnStoreCreatedCallback?.Invoke(store);

        // Replace existing BigtableClient registration
        services.RemoveAll<BigtableClient>();
        services.AddSingleton(server.Client);

        // Store the result for test access
        var result = new InMemoryBigtableResult(server, options.ProjectId, options.InstanceId);
        services.AddSingleton(result);

        return services;
    }

    /// <summary>
    /// Replaces any existing BigtableTableAdminClient registration with an in-memory implementation.
    /// Uses the same backing store as UseInMemoryBigtable if called together, or creates its own.
    ///
    /// Ref: Phase 5 plan — "UseInMemoryBigtableAdmin() — optional, for table management"
    /// </summary>
    public static IServiceCollection UseInMemoryBigtableAdmin(
        this IServiceCollection services,
        Action<InMemoryBigtableOptions>? configure = null)
    {
        var options = new InMemoryBigtableOptions();
        configure?.Invoke(options);

        // Build the in-memory server (reuse existing if available)
        var store = new InMemoryBigtableStore();
        foreach (var table in options.Tables)
        {
            store.CreateTable(table.Name, table.Families, table.GcRules);
        }

        var server = InMemoryBigtableServer.Create(store, options.ProjectId, options.InstanceId);

        // Create admin client from the same channel
        var adminClient = new BigtableTableAdminClientBuilder
        {
            CallInvoker = server.Channel.CreateCallInvoker(),
        }.Build();

        services.RemoveAll<BigtableTableAdminClient>();
        services.AddSingleton(adminClient);

        return services;
    }
}

/// <summary>
/// Options for configuring the in-memory Bigtable DI replacement.
/// </summary>
public sealed class InMemoryBigtableOptions
{
    internal List<TableDefinitionEntry> Tables { get; } = [];
    internal Action<BigtableClient>? OnClientCreatedCallback { get; private set; }
    internal Action<InMemoryBigtableStore>? OnStoreCreatedCallback { get; private set; }

    /// <summary>
    /// Project ID for TableName construction. Default: "test-project".
    /// </summary>
    public string ProjectId { get; set; } = "test-project";

    /// <summary>
    /// Instance ID for TableName construction. Default: "test-instance".
    /// </summary>
    public string InstanceId { get; set; } = "test-instance";

    /// <summary>
    /// Directory path for automatic state persistence.
    /// When set, state is automatically loaded on startup (if file exists)
    /// and saved on dispose.
    ///
    /// Ref: Phase 7 plan — "Auto-persist on Dispose, auto-load on create"
    /// </summary>
    public string? StatePersistenceDirectory { get; set; }

    /// <summary>
    /// Adds a table with the specified column families.
    /// </summary>
    public InMemoryBigtableOptions AddTable(string tableName, IEnumerable<string> columnFamilies)
    {
        Tables.Add(new TableDefinitionEntry(tableName, columnFamilies.ToList(), null));
        return this;
    }

    /// <summary>
    /// Callback invoked after the BigtableClient is created.
    /// Use for seeding data.
    /// </summary>
    public InMemoryBigtableOptions OnClientCreated(Action<BigtableClient> callback)
    {
        OnClientCreatedCallback = callback;
        return this;
    }

    /// <summary>
    /// Callback invoked after the store is created.
    /// Use for direct store access during setup (test-only, internal API).
    /// </summary>
    internal InMemoryBigtableOptions OnStoreCreated(Action<InMemoryBigtableStore> callback)
    {
        OnStoreCreatedCallback = callback;
        return this;
    }

    internal sealed record TableDefinitionEntry(string Name, List<string> Families, Dictionary<string, Google.Cloud.Bigtable.Admin.V2.GcRule?>? GcRules);
}

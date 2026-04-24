using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Microsoft.Extensions.DependencyInjection;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Integration tests for DI extension methods (UseInMemoryBigtable, UseInMemoryBigtableAdmin).
/// These test the ServiceCollection DI replacement pattern that production code uses
/// via WebApplicationFactory.ConfigureTestServices().
///
/// Ref: Phase 5 plan — "DI Extension Methods (ServiceCollectionExtensions)"
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.InMemoryOnly)]
public sealed class DiExtensionIntegrationTests
{
    [Fact]
    public async Task UseInMemoryBigtable_registers_BigtableClient_and_can_mutate_and_read()
    {
        // Arrange
        var services = new ServiceCollection();
        services.UseInMemoryBigtable(options =>
        {
            options.AddTable("di-test-table", ["cf1"]);
        });

        var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<BigtableClient>();
        var result = provider.GetRequiredService<InMemoryBigtableResult>();
        var tableName = new TableName(result.ProjectId, result.InstanceId, "di-test-table");

        // Act — write and read back
        await client.MutateRowAsync(tableName, new BigtableByteString("row1"),
            Mutations.SetCell("cf1", "col1", "hello", new BigtableVersion(1000)));

        var rows = client.ReadRows(tableName);
        var rowList = new List<Row>();
        await foreach (var row in rows) rowList.Add(row);

        // Assert
        rowList.Should().HaveCount(1);
        rowList[0].Key.ToStringUtf8().Should().Be("row1");
    }

    [Fact]
    public void UseInMemoryBigtable_replaces_existing_BigtableClient()
    {
        // Arrange — simulate production registering a client first
        var services = new ServiceCollection();
        services.AddSingleton<BigtableClient>(sp =>
            throw new InvalidOperationException("Production client should not be called"));

        // Act — in-memory replacement
        services.UseInMemoryBigtable(options =>
        {
            options.AddTable("replace-test", ["cf1"]);
        });

        var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<BigtableClient>();

        // Assert — should not throw (i.e., we got the in-memory client, not the production one)
        var act = () => client.ReadRows(
            new TableName("test-project", "test-instance", "replace-test"));
        act.Should().NotThrow();
    }

    [Fact]
    public void UseInMemoryBigtable_registers_InMemoryBigtableResult()
    {
        var services = new ServiceCollection();
        services.UseInMemoryBigtable(options =>
        {
            options.ProjectId = "my-proj";
            options.InstanceId = "my-inst";
            options.AddTable("result-test", ["cf1"]);
        });

        var provider = services.BuildServiceProvider();
        var result = provider.GetRequiredService<InMemoryBigtableResult>();

        result.Should().NotBeNull();
        result.ProjectId.Should().Be("my-proj");
        result.InstanceId.Should().Be("my-inst");
        result.Client.Should().NotBeNull();
        result.FaultInjector.Should().NotBeNull();
        result.RpcLog.Should().NotBeNull();
        result.QueryLog.Should().NotBeNull();
    }

    [Fact]
    public void UseInMemoryBigtable_OnClientCreated_callback_invoked()
    {
        var callbackInvoked = false;
        var services = new ServiceCollection();
        services.UseInMemoryBigtable(options =>
        {
            options.AddTable("callback-test", ["cf1"]);
            options.OnClientCreated(_ => callbackInvoked = true);
        });

        services.BuildServiceProvider();

        callbackInvoked.Should().BeTrue();
    }

    [Fact]
    public void UseInMemoryBigtableAdmin_registers_admin_client()
    {
        // Arrange
        var services = new ServiceCollection();
        services.UseInMemoryBigtableAdmin(options =>
        {
            options.AddTable("admin-di-test", ["cf1"]);
        });

        var provider = services.BuildServiceProvider();
        var adminClient = provider.GetRequiredService<BigtableTableAdminClient>();

        // Act — list tables
        var tables = adminClient.ListTables(new InstanceName("test-project", "test-instance")).ToList();

        // Assert
        tables.Should().NotBeEmpty();
        tables.Any(t => t.Name.Contains("admin-di-test")).Should().BeTrue();
    }

    [Fact]
    public async Task UseInMemoryBigtableAdmin_can_create_and_get_table()
    {
        // Arrange
        var services = new ServiceCollection();
        services.UseInMemoryBigtableAdmin();

        var provider = services.BuildServiceProvider();
        var adminClient = provider.GetRequiredService<BigtableTableAdminClient>();
        var instanceName = new InstanceName("test-project", "test-instance");

        // Act
        var table = new Table();
        table.ColumnFamilies.Add("cf1", new ColumnFamily());
        await adminClient.CreateTableAsync(instanceName, "dynamic-table", table);
        var fetched = await adminClient.GetTableAsync(
            new TableName("test-project", "test-instance", "dynamic-table"));

        // Assert
        fetched.Should().NotBeNull();
        fetched.ColumnFamilies.Should().ContainKey("cf1");
    }
}

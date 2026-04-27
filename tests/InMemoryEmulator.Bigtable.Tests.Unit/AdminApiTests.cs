using InMemoryEmulator.Bigtable;
using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for the Bigtable Table Admin API stubs:
/// CreateTable, GetTable, DeleteTable, ListTables, ModifyColumnFamilies.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#google.bigtable.admin.v2.BigtableTableAdmin
/// </summary>
public class AdminApiTests : IDisposable
{
    private readonly InMemoryBigtableServer _server;
    private readonly BigtableTableAdminClient _adminClient;
    private readonly BigtableClient _dataClient;
    private readonly string _instanceName;

    public AdminApiTests()
    {
        var store = new InMemoryBigtableStore();
        _server = InMemoryBigtableServer.Create(store, "test-project", "test-instance");
        _dataClient = _server.Client;
        _instanceName = "projects/test-project/instances/test-instance";

        _adminClient = new BigtableTableAdminClientBuilder
        {
            CallInvoker = _server.Channel.CreateCallInvoker(),
        }.Build();
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    [Fact]
    public async Task CreateTable_creates_table()
    {
        var request = new CreateTableRequest
        {
            Parent = _instanceName,
            TableId = "test-table",
            Table = new Table(),
        };
        request.Table.ColumnFamilies.Add("cf1", new ColumnFamily());

        var result = await _adminClient.CreateTableAsync(request);

        result.Name.Should().Contain("test-table");
        result.ColumnFamilies.Should().ContainKey("cf1");
    }

    [Fact]
    public async Task CreateTable_with_gc_rule()
    {
        var request = new CreateTableRequest
        {
            Parent = _instanceName,
            TableId = "gc-table",
            Table = new Table(),
        };
        request.Table.ColumnFamilies.Add("cf1", new ColumnFamily
        {
            GcRule = new GcRule { MaxNumVersions = 3 }
        });

        var result = await _adminClient.CreateTableAsync(request);

        result.ColumnFamilies["cf1"].GcRule.MaxNumVersions.Should().Be(3);
    }

    [Fact]
    public async Task CreateTable_duplicate_throws_already_exists()
    {
        var request = new CreateTableRequest
        {
            Parent = _instanceName,
            TableId = "dup-table",
            Table = new Table(),
        };
        request.Table.ColumnFamilies.Add("cf1", new ColumnFamily());

        await _adminClient.CreateTableAsync(request);

        var act = () => _adminClient.CreateTableAsync(request);
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.AlreadyExists);
    }

    [Fact]
    public async Task GetTable_returns_table()
    {
        var createReq = new CreateTableRequest
        {
            Parent = _instanceName,
            TableId = "get-table",
            Table = new Table(),
        };
        createReq.Table.ColumnFamilies.Add("cf1", new ColumnFamily());
        await _adminClient.CreateTableAsync(createReq);

        var result = await _adminClient.GetTableAsync(
            $"{_instanceName}/tables/get-table");

        result.Should().NotBeNull();
        result.Name.Should().Contain("get-table");
        result.ColumnFamilies.Should().ContainKey("cf1");
    }

    [Fact]
    public async Task GetTable_nonexistent_throws_not_found()
    {
        var act = () => _adminClient.GetTableAsync(
            $"{_instanceName}/tables/nonexistent");

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteTable_removes_table()
    {
        var createReq = new CreateTableRequest
        {
            Parent = _instanceName,
            TableId = "del-table",
            Table = new Table(),
        };
        createReq.Table.ColumnFamilies.Add("cf1", new ColumnFamily());
        await _adminClient.CreateTableAsync(createReq);

        await _adminClient.DeleteTableAsync(
            $"{_instanceName}/tables/del-table");

        var act = () => _adminClient.GetTableAsync(
            $"{_instanceName}/tables/del-table");
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task ListTables_returns_all_tables()
    {
        var req1 = new CreateTableRequest
        {
            Parent = _instanceName,
            TableId = "list-a",
            Table = new Table(),
        };
        req1.Table.ColumnFamilies.Add("cf1", new ColumnFamily());
        await _adminClient.CreateTableAsync(req1);

        var req2 = new CreateTableRequest
        {
            Parent = _instanceName,
            TableId = "list-b",
            Table = new Table(),
        };
        req2.Table.ColumnFamilies.Add("cf2", new ColumnFamily());
        await _adminClient.CreateTableAsync(req2);

        var result = _adminClient.ListTables(_instanceName);
        var tables = result.ToList();

        tables.Select(t => t.Name).Should().Contain(n => n.Contains("list-a"));
        tables.Select(t => t.Name).Should().Contain(n => n.Contains("list-b"));
    }

    [Fact]
    public async Task ModifyColumnFamilies_create_adds_family()
    {
        var createReq = new CreateTableRequest
        {
            Parent = _instanceName,
            TableId = "mod-create",
            Table = new Table(),
        };
        createReq.Table.ColumnFamilies.Add("cf1", new ColumnFamily());
        await _adminClient.CreateTableAsync(createReq);

        var modReq = new ModifyColumnFamiliesRequest
        {
            Name = $"{_instanceName}/tables/mod-create",
        };
        modReq.Modifications.Add(new ModifyColumnFamiliesRequest.Types.Modification
        {
            Id = "cf2",
            Create = new ColumnFamily(),
        });

        var result = await _adminClient.ModifyColumnFamiliesAsync(modReq);

        result.ColumnFamilies.Should().ContainKey("cf1");
        result.ColumnFamilies.Should().ContainKey("cf2");
    }

    [Fact]
    public async Task ModifyColumnFamilies_update_gc_rule()
    {
        var createReq = new CreateTableRequest
        {
            Parent = _instanceName,
            TableId = "mod-update",
            Table = new Table(),
        };
        createReq.Table.ColumnFamilies.Add("cf1", new ColumnFamily());
        await _adminClient.CreateTableAsync(createReq);

        var modReq = new ModifyColumnFamiliesRequest
        {
            Name = $"{_instanceName}/tables/mod-update",
        };
        modReq.Modifications.Add(new ModifyColumnFamiliesRequest.Types.Modification
        {
            Id = "cf1",
            Update = new ColumnFamily
            {
                GcRule = new GcRule { MaxNumVersions = 5 }
            },
        });

        var result = await _adminClient.ModifyColumnFamiliesAsync(modReq);

        result.ColumnFamilies["cf1"].GcRule.MaxNumVersions.Should().Be(5);
    }

    [Fact]
    public async Task ModifyColumnFamilies_drop_removes_family()
    {
        var createReq = new CreateTableRequest
        {
            Parent = _instanceName,
            TableId = "mod-drop",
            Table = new Table(),
        };
        createReq.Table.ColumnFamilies.Add("cf1", new ColumnFamily());
        createReq.Table.ColumnFamilies.Add("cf2", new ColumnFamily());
        await _adminClient.CreateTableAsync(createReq);

        var modReq = new ModifyColumnFamiliesRequest
        {
            Name = $"{_instanceName}/tables/mod-drop",
        };
        modReq.Modifications.Add(new ModifyColumnFamiliesRequest.Types.Modification
        {
            Id = "cf1",
            Drop = true,
        });

        var result = await _adminClient.ModifyColumnFamiliesAsync(modReq);

        result.ColumnFamilies.Should().NotContainKey("cf1");
        result.ColumnFamilies.Should().ContainKey("cf2");
    }

    [Fact]
    public async Task Admin_created_table_is_usable_for_data_operations()
    {
        // Create table via admin API
        var createReq = new CreateTableRequest
        {
            Parent = _instanceName,
            TableId = "data-test",
            Table = new Table(),
        };
        createReq.Table.ColumnFamilies.Add("cf1", new ColumnFamily());
        await _adminClient.CreateTableAsync(createReq);

        // Use data API via the same store
        var tableName = new TableName("test-project", "test-instance", "data-test");
        await _dataClient.MutateRowAsync(tableName,
            new BigtableByteString("row1"),
            Mutations.SetCell("cf1", "col", "value", new BigtableVersion(1000)));

        var row = await _dataClient.ReadRowAsync(tableName,
            new BigtableByteString("row1"));

        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("value");
    }
}

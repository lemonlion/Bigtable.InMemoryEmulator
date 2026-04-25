using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Grpc.Core;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class AdminApiIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string SeedTable = "admin-seed";

    public AdminApiIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(SeedTable, new[] { "cf" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private BigtableTableAdminClient Admin => _fixture.AdminClient;
    private string Instance => _fixture.InstanceName;

    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task CreateTable_and_GetTable_round_trip()
    {
        var request = new CreateTableRequest
        {
            Parent = Instance,
            TableId = "admin-create",
            Table = new Table(),
        };
        request.Table.ColumnFamilies.Add("f1", new ColumnFamily());
        request.Table.ColumnFamilies.Add("f2", new ColumnFamily());
        var table = await Admin.CreateTableAsync(request);
        table.Should().NotBeNull();
        table.ColumnFamilies.Should().ContainKey("f1");
        table.ColumnFamilies.Should().ContainKey("f2");

        var retrieved = await Admin.GetTableAsync(Instance + "/tables/admin-create");
        retrieved.ColumnFamilies.Should().ContainKey("f1");
    }

    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task CreateTable_with_gc_rule()
    {
        var request = new CreateTableRequest
        {
            Parent = Instance,
            TableId = "admin-gc",
            Table = new Table(),
        };
        request.Table.ColumnFamilies.Add("versioned", new ColumnFamily
        {
            GcRule = new GcRule { MaxNumVersions = 3 }
        });
        var table = await Admin.CreateTableAsync(request);
        table.ColumnFamilies["versioned"].GcRule.MaxNumVersions.Should().Be(3);
    }

    [Fact]
    public async Task CreateTable_duplicate_throws_AlreadyExists()
    {
        var act = async () =>
        {
            var request = new CreateTableRequest
            {
                Parent = Instance,
                TableId = SeedTable,
                Table = new Table(),
            };
            request.Table.ColumnFamilies.Add("cf", new ColumnFamily());
            await Admin.CreateTableAsync(request);
        };
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.AlreadyExists);
    }

    [Fact]
    public async Task DeleteTable_removes_table()
    {
        var createReq = new CreateTableRequest
        {
            Parent = Instance,
            TableId = "admin-del",
            Table = new Table(),
        };
        createReq.Table.ColumnFamilies.Add("cf", new ColumnFamily());
        await Admin.CreateTableAsync(createReq);

        await Admin.DeleteTableAsync(Instance + "/tables/admin-del");

        var act = async () => await Admin.GetTableAsync(Instance + "/tables/admin-del");
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public void ListTables_returns_all_tables()
    {
        var tables = Admin.ListTables(Instance);
        var ids = tables.Select(t => t.TableName.TableId).ToList();
        ids.Should().Contain(SeedTable);
    }

    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task ModifyColumnFamilies_adds_family()
    {
        var createReq = new CreateTableRequest
        {
            Parent = Instance,
            TableId = "admin-mod",
            Table = new Table(),
        };
        createReq.Table.ColumnFamilies.Add("cf", new ColumnFamily());
        await Admin.CreateTableAsync(createReq);

        var modReq = new ModifyColumnFamiliesRequest
        {
            Name = Instance + "/tables/admin-mod",
        };
        modReq.Modifications.Add(new ModifyColumnFamiliesRequest.Types.Modification
        {
            Id = "new_family",
            Create = new ColumnFamily(),
        });
        var result = await Admin.ModifyColumnFamiliesAsync(modReq);
        result.ColumnFamilies.Should().ContainKey("cf");
        result.ColumnFamilies.Should().ContainKey("new_family");
    }

    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task Admin_created_table_is_usable_for_data()
    {
        var request = new CreateTableRequest
        {
            Parent = Instance,
            TableId = "admin-data",
            Table = new Table(),
        };
        request.Table.ColumnFamilies.Add("cf", new ColumnFamily());
        await Admin.CreateTableAsync(request);

        var dataClient = _fixture.Client;
        var tn = _fixture.GetTableName("admin-data");
        await dataClient.MutateRowAsync(tn, new BigtableByteString("row1"),
            Mutations.SetCell("cf", "col", "value", new BigtableVersion(1000)));
        var row = await dataClient.ReadRowAsync(tn, new BigtableByteString("row1"));
        row.Should().NotBeNull();
    }
}

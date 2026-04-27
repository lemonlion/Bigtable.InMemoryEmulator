using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for admin column family modification — add, remove, update GC rules.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#modifycolumnfamiliesrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.GcpOnly)]
public sealed class AdminColumnFamilyTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;

    public AdminColumnFamilyTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync("admin-cf-seed", new[] { "seed" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private Google.Cloud.Bigtable.Admin.V2.BigtableTableAdminClient Admin => _fixture.AdminClient;
    private string Instance => _fixture.InstanceName;
    private string AdminTN(string table) => Instance + "/tables/" + table;

    #region Create table with families

    [Fact]
    public async Task Create_table_with_single_family()
    {
        var request = new Google.Cloud.Bigtable.Admin.V2.CreateTableRequest
        {
            Parent = Instance,
            TableId = "admin-cf-1fam",
            Table = new Google.Cloud.Bigtable.Admin.V2.Table()
        };
        request.Table.ColumnFamilies.Add("cf1", new Google.Cloud.Bigtable.Admin.V2.ColumnFamily());
        var table = await Admin.CreateTableAsync(request);
        table.ColumnFamilies.Should().ContainKey("cf1");
    }

    [Fact]
    public async Task Create_table_with_multiple_families()
    {
        var request = new Google.Cloud.Bigtable.Admin.V2.CreateTableRequest
        {
            Parent = Instance,
            TableId = "admin-cf-3fam",
            Table = new Google.Cloud.Bigtable.Admin.V2.Table()
        };
        request.Table.ColumnFamilies.Add("fam_a", new Google.Cloud.Bigtable.Admin.V2.ColumnFamily());
        request.Table.ColumnFamilies.Add("fam_b", new Google.Cloud.Bigtable.Admin.V2.ColumnFamily());
        request.Table.ColumnFamilies.Add("fam_c", new Google.Cloud.Bigtable.Admin.V2.ColumnFamily());
        var table = await Admin.CreateTableAsync(request);
        table.ColumnFamilies.Should().HaveCount(3);
    }

    [Fact]
    public async Task Create_table_with_gc_rule()
    {
        var request = new Google.Cloud.Bigtable.Admin.V2.CreateTableRequest
        {
            Parent = Instance,
            TableId = "admin-cf-gc",
            Table = new Google.Cloud.Bigtable.Admin.V2.Table()
        };
        request.Table.ColumnFamilies.Add("cf", new Google.Cloud.Bigtable.Admin.V2.ColumnFamily
        {
            GcRule = new Google.Cloud.Bigtable.Admin.V2.GcRule { MaxNumVersions = 5 }
        });
        var table = await Admin.CreateTableAsync(request);
        table.ColumnFamilies["cf"].GcRule.MaxNumVersions.Should().Be(5);
    }

    #endregion

    #region Add column family

    [Fact]
    public async Task Add_column_family_to_existing_table()
    {
        var createReq = new Google.Cloud.Bigtable.Admin.V2.CreateTableRequest
        {
            Parent = Instance,
            TableId = "admin-cf-add",
            Table = new Google.Cloud.Bigtable.Admin.V2.Table()
        };
        createReq.Table.ColumnFamilies.Add("cf1", new Google.Cloud.Bigtable.Admin.V2.ColumnFamily());
        await Admin.CreateTableAsync(createReq);

        var modReq = new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest
        {
            Name = AdminTN("admin-cf-add"),
            Modifications =
            {
                new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "cf2",
                    Create = new Google.Cloud.Bigtable.Admin.V2.ColumnFamily()
                }
            }
        };
        var result = await Admin.ModifyColumnFamiliesAsync(modReq);
        result.ColumnFamilies.Should().ContainKey("cf1").And.ContainKey("cf2");
    }

    [Fact]
    public async Task Add_family_with_gc_rule()
    {
        var createReq = new Google.Cloud.Bigtable.Admin.V2.CreateTableRequest
        {
            Parent = Instance,
            TableId = "admin-cf-add-gc",
            Table = new Google.Cloud.Bigtable.Admin.V2.Table()
        };
        createReq.Table.ColumnFamilies.Add("cf1", new Google.Cloud.Bigtable.Admin.V2.ColumnFamily());
        await Admin.CreateTableAsync(createReq);

        var modReq = new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest
        {
            Name = AdminTN("admin-cf-add-gc"),
            Modifications =
            {
                new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "cf2",
                    Create = new Google.Cloud.Bigtable.Admin.V2.ColumnFamily
                    {
                        GcRule = new Google.Cloud.Bigtable.Admin.V2.GcRule { MaxNumVersions = 3 }
                    }
                }
            }
        };
        var result = await Admin.ModifyColumnFamiliesAsync(modReq);
        result.ColumnFamilies["cf2"].GcRule.MaxNumVersions.Should().Be(3);
    }

    #endregion

    #region Drop column family

    [Fact]
    public async Task Drop_column_family()
    {
        var createReq = new Google.Cloud.Bigtable.Admin.V2.CreateTableRequest
        {
            Parent = Instance,
            TableId = "admin-cf-drop",
            Table = new Google.Cloud.Bigtable.Admin.V2.Table()
        };
        createReq.Table.ColumnFamilies.Add("cf1", new Google.Cloud.Bigtable.Admin.V2.ColumnFamily());
        createReq.Table.ColumnFamilies.Add("cf2", new Google.Cloud.Bigtable.Admin.V2.ColumnFamily());
        await Admin.CreateTableAsync(createReq);

        var modReq = new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest
        {
            Name = AdminTN("admin-cf-drop"),
            Modifications =
            {
                new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "cf1",
                    Drop = true
                }
            }
        };
        var result = await Admin.ModifyColumnFamiliesAsync(modReq);
        result.ColumnFamilies.Should().NotContainKey("cf1");
        result.ColumnFamilies.Should().ContainKey("cf2");
    }

    [Fact]
    public async Task Drop_family_removes_data()
    {
        await _fixture.CreateTableAsync("admin-cf-drop-data", new[] { "cf1", "cf2" });
        var tn = _fixture.GetTableName("admin-cf-drop-data");

        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf1", "a", "va", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "b", "vb", new BigtableVersion(1000)));

        var modReq = new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest
        {
            Name = AdminTN("admin-cf-drop-data"),
            Modifications =
            {
                new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "cf1",
                    Drop = true
                }
            }
        };
        await Admin.ModifyColumnFamiliesAsync(modReq);

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(tn, RowSet.FromRowKeys("r1")))
            rows.Add(row);
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle();
        rows[0].Families[0].Name.Should().Be("cf2");
    }

    #endregion

    #region Get table

    [Fact]
    public async Task GetTable_returns_column_families()
    {
        var createReq = new Google.Cloud.Bigtable.Admin.V2.CreateTableRequest
        {
            Parent = Instance,
            TableId = "admin-cf-get",
            Table = new Google.Cloud.Bigtable.Admin.V2.Table()
        };
        createReq.Table.ColumnFamilies.Add("f1", new Google.Cloud.Bigtable.Admin.V2.ColumnFamily());
        createReq.Table.ColumnFamilies.Add("f2", new Google.Cloud.Bigtable.Admin.V2.ColumnFamily
        {
            GcRule = new Google.Cloud.Bigtable.Admin.V2.GcRule { MaxNumVersions = 2 }
        });
        await Admin.CreateTableAsync(createReq);

        var table = await Admin.GetTableAsync(AdminTN("admin-cf-get"));
        table.ColumnFamilies.Should().HaveCount(2);
        table.ColumnFamilies.Should().ContainKey("f1");
        table.ColumnFamilies.Should().ContainKey("f2");
    }

    #endregion

    #region Delete table

    [Fact]
    public async Task DeleteTable_removes_table()
    {
        var createReq = new Google.Cloud.Bigtable.Admin.V2.CreateTableRequest
        {
            Parent = Instance,
            TableId = "admin-cf-del",
            Table = new Google.Cloud.Bigtable.Admin.V2.Table()
        };
        createReq.Table.ColumnFamilies.Add("cf", new Google.Cloud.Bigtable.Admin.V2.ColumnFamily());
        await Admin.CreateTableAsync(createReq);

        await Admin.DeleteTableAsync(AdminTN("admin-cf-del"));

        var act = () => Admin.GetTableAsync(AdminTN("admin-cf-del"));
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    #endregion
}

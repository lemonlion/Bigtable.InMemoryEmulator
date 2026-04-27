using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for admin table-level operations: create, get, list, delete tables,
/// and table metadata validation.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#bigtabletableadmin
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class AdminTableOperationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private readonly List<string> _createdTables = new();

    public AdminTableOperationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync("adm-init", new[] { "cf" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableTableAdminClient Admin => _fixture.AdminClient;
    private BigtableClient Client => _fixture.Client;
    private string Instance => _fixture.InstanceName;

    private string AdminTN(string table) => Instance + "/tables/" + table;

    #region Create table

    [Fact]
    public async Task Create_table_with_single_family()
    {
        var tableName = $"adm-create-{Guid.NewGuid():N}".Substring(0, 30);
        _createdTables.Add(tableName);
        var req = new Google.Cloud.Bigtable.Admin.V2.CreateTableRequest
        {
            ParentAsInstanceName = Google.Cloud.Bigtable.Common.V2.InstanceName.Parse(Instance),
            TableId = tableName,
            Table = new Google.Cloud.Bigtable.Admin.V2.Table
            {
                ColumnFamilies = { { "cf", new Google.Cloud.Bigtable.Admin.V2.ColumnFamily() } }
            }
        };
        var table = await Admin.CreateTableAsync(req);
        table.ColumnFamilies.Should().ContainKey("cf");
    }

    [Fact]
    public async Task Create_table_with_multiple_families()
    {
        var tableName = $"adm-multi-{Guid.NewGuid():N}".Substring(0, 30);
        _createdTables.Add(tableName);
        var req = new Google.Cloud.Bigtable.Admin.V2.CreateTableRequest
        {
            ParentAsInstanceName = Google.Cloud.Bigtable.Common.V2.InstanceName.Parse(Instance),
            TableId = tableName,
            Table = new Google.Cloud.Bigtable.Admin.V2.Table
            {
                ColumnFamilies =
                {
                    { "cf1", new Google.Cloud.Bigtable.Admin.V2.ColumnFamily() },
                    { "cf2", new Google.Cloud.Bigtable.Admin.V2.ColumnFamily() },
                    { "cf3", new Google.Cloud.Bigtable.Admin.V2.ColumnFamily() }
                }
            }
        };
        var table = await Admin.CreateTableAsync(req);
        table.ColumnFamilies.Should().HaveCount(3);
        table.ColumnFamilies.Keys.Should().Contain("cf1").And.Contain("cf2").And.Contain("cf3");
    }

    [Fact]
    public async Task Create_table_with_gc_rule()
    {
        var tableName = $"adm-gc-{Guid.NewGuid():N}".Substring(0, 30);
        _createdTables.Add(tableName);
        var req = new Google.Cloud.Bigtable.Admin.V2.CreateTableRequest
        {
            ParentAsInstanceName = Google.Cloud.Bigtable.Common.V2.InstanceName.Parse(Instance),
            TableId = tableName,
            Table = new Google.Cloud.Bigtable.Admin.V2.Table
            {
                ColumnFamilies =
                {
                    { "cf", new Google.Cloud.Bigtable.Admin.V2.ColumnFamily
                    {
                        GcRule = new Google.Cloud.Bigtable.Admin.V2.GcRule { MaxNumVersions = 3 }
                    }}
                }
            }
        };
        var table = await Admin.CreateTableAsync(req);
        table.ColumnFamilies["cf"].GcRule.MaxNumVersions.Should().Be(3);
    }

    #endregion

    #region Get table

    [Fact]
    public async Task Get_existing_table()
    {
        var tableName = $"adm-get-{Guid.NewGuid():N}".Substring(0, 30);
        _createdTables.Add(tableName);
        await _fixture.CreateTableAsync(tableName, new[] { "cf" });
        var table = await Admin.GetTableAsync(new Google.Cloud.Bigtable.Admin.V2.GetTableRequest
        {
            Name = AdminTN(tableName)
        });
        table.Should().NotBeNull();
        table.ColumnFamilies.Should().ContainKey("cf");
    }

    [Fact]
    public async Task Get_nonexistent_table_throws()
    {
        var act = () => Admin.GetTableAsync(new Google.Cloud.Bigtable.Admin.V2.GetTableRequest
        {
            Name = AdminTN("nonexistent-table-xyz")
        });
        await act.Should().ThrowAsync<Grpc.Core.RpcException>()
            .Where(e => e.StatusCode == Grpc.Core.StatusCode.NotFound);
    }

    #endregion

    #region Delete table

    [Fact]
    public async Task Delete_table_succeeds()
    {
        var tableName = $"adm-del-{Guid.NewGuid():N}".Substring(0, 30);
        await _fixture.CreateTableAsync(tableName, new[] { "cf" });
        await Admin.DeleteTableAsync(new Google.Cloud.Bigtable.Admin.V2.DeleteTableRequest
        {
            Name = AdminTN(tableName)
        });
        // Verify table is gone
        var act = () => Admin.GetTableAsync(new Google.Cloud.Bigtable.Admin.V2.GetTableRequest
        {
            Name = AdminTN(tableName)
        });
        await act.Should().ThrowAsync<Grpc.Core.RpcException>()
            .Where(e => e.StatusCode == Grpc.Core.StatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_nonexistent_table_throws()
    {
        var act = () => Admin.DeleteTableAsync(new Google.Cloud.Bigtable.Admin.V2.DeleteTableRequest
        {
            Name = AdminTN("nonexistent-del-table")
        });
        await act.Should().ThrowAsync<Grpc.Core.RpcException>()
            .Where(e => e.StatusCode == Grpc.Core.StatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_table_removes_data()
    {
        var tableName = $"adm-dd-{Guid.NewGuid():N}".Substring(0, 30);
        await _fixture.CreateTableAsync(tableName, new[] { "cf" });
        var tn = _fixture.GetTableName(tableName);
        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf", "c", "v", new BigtableVersion(1000)));
        // Delete the table
        await Admin.DeleteTableAsync(new Google.Cloud.Bigtable.Admin.V2.DeleteTableRequest
        {
            Name = AdminTN(tableName)
        });
        // Recreate and verify data is gone
        await _fixture.CreateTableAsync(tableName, new[] { "cf" });
        var row = await Client.ReadRowAsync(tn, "r1");
        row.Should().BeNull();
    }

    #endregion

    #region Column family modifications

    [Fact]
    public async Task Add_column_family_to_existing_table()
    {
        var tableName = $"adm-addf-{Guid.NewGuid():N}".Substring(0, 30);
        _createdTables.Add(tableName);
        await _fixture.CreateTableAsync(tableName, new[] { "cf1" });
        var resp = await Admin.ModifyColumnFamiliesAsync(new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest
        {
            Name = AdminTN(tableName),
            Modifications =
            {
                new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "cf2",
                    Create = new Google.Cloud.Bigtable.Admin.V2.ColumnFamily()
                }
            }
        });
        resp.ColumnFamilies.Should().ContainKey("cf1").And.ContainKey("cf2");
    }

    [Fact]
    public async Task Drop_column_family()
    {
        var tableName = $"adm-dropf-{Guid.NewGuid():N}".Substring(0, 30);
        _createdTables.Add(tableName);
        await _fixture.CreateTableAsync(tableName, new[] { "cf1", "cf2" });
        var resp = await Admin.ModifyColumnFamiliesAsync(new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest
        {
            Name = AdminTN(tableName),
            Modifications =
            {
                new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "cf2",
                    Drop = true
                }
            }
        });
        resp.ColumnFamilies.Should().ContainKey("cf1");
        resp.ColumnFamilies.Should().NotContainKey("cf2");
    }

    [Fact]
    public async Task Modify_gc_rule_on_existing_family()
    {
        var tableName = $"adm-modgc-{Guid.NewGuid():N}".Substring(0, 30);
        _createdTables.Add(tableName);
        await _fixture.CreateTableAsync(tableName, new[] { "cf" });
        var resp = await Admin.ModifyColumnFamiliesAsync(new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest
        {
            Name = AdminTN(tableName),
            Modifications =
            {
                new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "cf",
                    Update = new Google.Cloud.Bigtable.Admin.V2.ColumnFamily
                    {
                        GcRule = new Google.Cloud.Bigtable.Admin.V2.GcRule { MaxNumVersions = 5 }
                    }
                }
            }
        });
        resp.ColumnFamilies["cf"].GcRule.MaxNumVersions.Should().Be(5);
    }

    [Fact]
    public async Task Drop_nonexistent_family_throws()
    {
        var tableName = $"adm-dropne-{Guid.NewGuid():N}".Substring(0, 30);
        _createdTables.Add(tableName);
        await _fixture.CreateTableAsync(tableName, new[] { "cf" });
        var act = () => Admin.ModifyColumnFamiliesAsync(new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest
        {
            Name = AdminTN(tableName),
            Modifications =
            {
                new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "nonexistent",
                    Drop = true
                }
            }
        });
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    [Fact]
    public async Task Multiple_modifications_in_single_request()
    {
        var tableName = $"adm-multmod-{Guid.NewGuid():N}".Substring(0, 28);
        _createdTables.Add(tableName);
        await _fixture.CreateTableAsync(tableName, new[] { "cf1", "cf2" });
        var resp = await Admin.ModifyColumnFamiliesAsync(new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest
        {
            Name = AdminTN(tableName),
            Modifications =
            {
                new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "cf2", Drop = true
                },
                new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "cf3", Create = new Google.Cloud.Bigtable.Admin.V2.ColumnFamily()
                }
            }
        });
        resp.ColumnFamilies.Should().ContainKey("cf1").And.ContainKey("cf3");
        resp.ColumnFamilies.Should().NotContainKey("cf2");
    }

    #endregion

    #region Data operations after schema changes

    [Fact]
    public async Task Write_to_newly_added_family()
    {
        var tableName = $"adm-wrnew-{Guid.NewGuid():N}".Substring(0, 30);
        _createdTables.Add(tableName);
        await _fixture.CreateTableAsync(tableName, new[] { "cf1" });
        await Admin.ModifyColumnFamiliesAsync(new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest
        {
            Name = AdminTN(tableName),
            Modifications =
            {
                new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "cf2", Create = new Google.Cloud.Bigtable.Admin.V2.ColumnFamily()
                }
            }
        });
        var tn = _fixture.GetTableName(tableName);
        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf2", "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(tn, "r1");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v");
    }

    [Fact]
    public async Task Write_to_dropped_family_fails()
    {
        var tableName = $"adm-wrdrop-{Guid.NewGuid():N}".Substring(0, 30);
        _createdTables.Add(tableName);
        await _fixture.CreateTableAsync(tableName, new[] { "cf1", "cf2" });
        await Admin.ModifyColumnFamiliesAsync(new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest
        {
            Name = AdminTN(tableName),
            Modifications =
            {
                new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "cf2", Drop = true
                }
            }
        });
        var tn = _fixture.GetTableName(tableName);
        var act = () => Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf2", "c", "v", new BigtableVersion(1000)));
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    [Fact]
    public async Task Drop_family_removes_data_in_that_family()
    {
        var tableName = $"adm-dropdta-{Guid.NewGuid():N}".Substring(0, 28);
        _createdTables.Add(tableName);
        await _fixture.CreateTableAsync(tableName, new[] { "cf1", "cf2" });
        var tn = _fixture.GetTableName(tableName);
        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf1", "c", "keep", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "drop", new BigtableVersion(1000)));
        // Drop cf2
        await Admin.ModifyColumnFamiliesAsync(new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest
        {
            Name = AdminTN(tableName),
            Modifications =
            {
                new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "cf2", Drop = true
                }
            }
        });
        var row = await Client.ReadRowAsync(tn, "r1");
        row!.Families.Should().ContainSingle();
        row.Families[0].Name.Should().Be("cf1");
    }

    #endregion
}

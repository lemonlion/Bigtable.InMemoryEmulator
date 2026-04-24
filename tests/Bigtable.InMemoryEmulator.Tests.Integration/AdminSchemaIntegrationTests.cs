using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Admin API schema lifecycle integration tests — table creation with various schemas,
/// column family modifications, GC rule effects on reads, and table lifecycle operations.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class AdminSchemaIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;

    public AdminSchemaIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync()
    {
        // Create a dummy table to initialize the fixture's gRPC channel and project/instance IDs.
        // This enables GetTableName() to work for table names that don't exist yet (needed by
        // tests like GetTable_nonexistent_throws_NotFound and DeleteTable_nonexistent_throws_NotFound).
        await _fixture.CreateTableAsync("admin-init", new[] { "cf" });
    }
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private BigtableClient Client => _fixture.Client;
    private BigtableTableAdminClient AdminClient => _fixture.AdminClient;

    private TableName GetTableName(string tableName) => _fixture.GetTableName(tableName);

    #region Table creation with families

    [Fact]
    public async Task CreateTable_with_single_family()
    {
        await _fixture.CreateTableAsync("admin-s1", new[] { "cf" });
        var tn = GetTableName("admin-s1");

        // Table exists and is usable
        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf", "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(tn, "r1");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateTable_with_multiple_families()
    {
        await _fixture.CreateTableAsync("admin-s2", new[] { "cf1", "cf2", "cf3" });
        var tn = GetTableName("admin-s2");

        // All families are usable
        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf1", "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "v2", new BigtableVersion(1000)),
            Mutations.SetCell("cf3", "c", "v3", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(tn, "r1");
        row!.Families.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetTable_returns_column_families()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#google.bigtable.admin.v2.Table
        await _fixture.CreateTableAsync("admin-s3", new[] { "fam-a", "fam-b" });
        var tn = GetTableName("admin-s3");

        var table = await AdminClient.GetTableAsync(tn);
        table.ColumnFamilies.Should().ContainKey("fam-a");
        table.ColumnFamilies.Should().ContainKey("fam-b");
    }

    [Fact]
    public async Task GetTable_nonexistent_throws_NotFound()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#google.bigtable.admin.v2.BigtableTableAdmin.GetTable
        var tn = GetTableName("admin-nonexistent-table");
        var act = () => AdminClient.GetTableAsync(tn);
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    #endregion

    #region Delete table lifecycle

    [Fact]
    public async Task DeleteTable_then_read_throws_NotFound()
    {
        await _fixture.CreateTableAsync("admin-del1", new[] { "cf" });
        var tn = GetTableName("admin-del1");

        await AdminClient.DeleteTableAsync(tn);

        var act = () => Client.ReadRowAsync(tn, "r1");
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteTable_then_write_throws_NotFound()
    {
        await _fixture.CreateTableAsync("admin-del2", new[] { "cf" });
        var tn = GetTableName("admin-del2");

        await AdminClient.DeleteTableAsync(tn);

        var act = () => Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf", "c", "v", new BigtableVersion(1000)));
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteTable_then_recreate_with_different_schema()
    {
        await _fixture.CreateTableAsync("admin-del3", new[] { "old_cf" });
        var tn = GetTableName("admin-del3");

        await AdminClient.DeleteTableAsync(tn);
        await _fixture.CreateTableAsync("admin-del3", new[] { "new_cf1", "new_cf2" });

        // Old family should not work
        var act = () => Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("old_cf", "c", "v", new BigtableVersion(1000)));
        await act.Should().ThrowAsync<RpcException>();

        // New families should work
        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("new_cf1", "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(tn, "r1");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteTable_then_recreate_data_is_gone()
    {
        await _fixture.CreateTableAsync("admin-del4", new[] { "cf" });
        var tn = GetTableName("admin-del4");

        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf", "c", "v", new BigtableVersion(1000)));

        await AdminClient.DeleteTableAsync(tn);
        await _fixture.CreateTableAsync("admin-del4", new[] { "cf" });

        var row = await Client.ReadRowAsync(tn, "r1");
        row.Should().BeNull();
    }

    [Fact]
    public async Task DeleteTable_nonexistent_throws_NotFound()
    {
        var tn = GetTableName("admin-del-noexist");
        var act = () => AdminClient.DeleteTableAsync(tn);
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    #endregion

    private string TablePath(string tableName) => _fixture.InstanceName + "/tables/" + tableName;

    #region ModifyColumnFamilies

    [Fact]
    public async Task ModifyColumnFamilies_add_new_family()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#google.bigtable.admin.v2.ModifyColumnFamiliesRequest
        await _fixture.CreateTableAsync("admin-mod1", new[] { "cf" });
        var tn = GetTableName("admin-mod1");

        await AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = TablePath("admin-mod1"),
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "new_cf",
                    Create = new ColumnFamily()
                }
            }
        });

        // New family should be usable
        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("new_cf", "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(tn, "r1");
        row!.Families.Should().Contain(f => f.Name == "new_cf");
    }

    [Fact]
    public async Task ModifyColumnFamilies_drop_family_removes_data()
    {
        await _fixture.CreateTableAsync("admin-mod2", new[] { "cf", "to_drop" });
        var tn = GetTableName("admin-mod2");

        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf", "c", "keep", new BigtableVersion(1000)),
            Mutations.SetCell("to_drop", "c", "gone", new BigtableVersion(1000)));

        await AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = TablePath("admin-mod2"),
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "to_drop",
                    Drop = true
                }
            }
        });

        var row = await Client.ReadRowAsync(tn, "r1");
        row!.Families.Should().ContainSingle().Which.Name.Should().Be("cf");
    }

    [Fact]
    public async Task ModifyColumnFamilies_add_and_drop_in_single_call()
    {
        await _fixture.CreateTableAsync("admin-mod3", new[] { "old_cf" });
        var tn = GetTableName("admin-mod3");

        await AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = TablePath("admin-mod3"),
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "old_cf",
                    Drop = true
                },
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "new_cf",
                    Create = new ColumnFamily()
                }
            }
        });

        var table = await AdminClient.GetTableAsync(tn);
        table.ColumnFamilies.Should().NotContainKey("old_cf");
        table.ColumnFamilies.Should().ContainKey("new_cf");
    }

    [Fact]
    public async Task ModifyColumnFamilies_update_gc_rule()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#google.bigtable.admin.v2.ModifyColumnFamiliesRequest.Modification
        await _fixture.CreateTableAsync("admin-mod4", new[] { "cf" });
        var tn = GetTableName("admin-mod4");

        // Set MaxVersions=2
        await AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = TablePath("admin-mod4"),
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "cf",
                    Update = new ColumnFamily { GcRule = new GcRule { MaxNumVersions = 2 } }
                }
            }
        });

        var table = await AdminClient.GetTableAsync(tn);
        table.ColumnFamilies["cf"].GcRule.MaxNumVersions.Should().Be(2);
    }

    [Fact]
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    public async Task ModifyColumnFamilies_update_gc_rule_affects_reads()
    {
        // Go emulator divergence: ModifyColumnFamilies setting MaxVersions after data is written
        // does not retroactively apply GC to existing data in the Go emulator.
        // Ref: https://cloud.google.com/bigtable/docs/garbage-collection
        //   "Cloud Bigtable periodically removes data that has been marked for deletion by a garbage collection rule."
        await _fixture.CreateTableAsync("admin-mod5", new[] { "cf" });
        var tn = GetTableName("admin-mod5");

        // Write 5 versions
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(tn, "r1",
                Mutations.SetCell("cf", "c", $"v{i}", new BigtableVersion(i * 1000)));

        // Set MaxVersions=2
        await AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = TablePath("admin-mod5"),
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "cf",
                    Update = new ColumnFamily { GcRule = new GcRule { MaxNumVersions = 2 } }
                }
            }
        });

        var row = await Client.ReadRowAsync(tn, "r1");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v5");
        row.Families[0].Columns[0].Cells[1].Value.ToStringUtf8().Should().Be("v4");
    }

    [Fact]
    public async Task ModifyColumnFamilies_remove_gc_rule_retains_all_versions()
    {
        await _fixture.CreateTableAsync("admin-mod6", new[] { "cf" });
        var tn = GetTableName("admin-mod6");

        // Set MaxVersions=1 first
        await AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = TablePath("admin-mod6"),
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "cf",
                    Update = new ColumnFamily { GcRule = new GcRule { MaxNumVersions = 1 } }
                }
            }
        });

        // Write 3 versions (only 1 visible with GC rule)
        for (int i = 1; i <= 3; i++)
            await Client.MutateRowAsync(tn, "r1",
                Mutations.SetCell("cf", "c", $"v{i}", new BigtableVersion(i * 1000)));

        // Remove GC rule
        await AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = TablePath("admin-mod6"),
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "cf",
                    Update = new ColumnFamily()
                }
            }
        });

        // Write one more version
        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf", "c", "v4", new BigtableVersion(4000)));

        // Now all newly written versions should be visible
        var row = await Client.ReadRowAsync(tn, "r1");
        row!.Families[0].Columns[0].Cells.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    #endregion

    #region ListTables

    [Fact]
    public async Task ListTables_returns_created_tables()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#google.bigtable.admin.v2.BigtableTableAdmin.ListTables
        await _fixture.CreateTableAsync("admin-list1", new[] { "cf" });
        await _fixture.CreateTableAsync("admin-list2", new[] { "cf" });

        var request = new ListTablesRequest { ParentAsInstanceName = InstanceName.Parse(_fixture.InstanceName) };
        var tables = AdminClient.ListTables(request);
        var tableNames = new List<string>();
        foreach (var table in tables)
            tableNames.Add(table.TableName.TableId);

        tableNames.Should().Contain("admin-list1");
        tableNames.Should().Contain("admin-list2");
    }

    [Fact]
    public async Task ListTables_after_delete_excludes_deleted()
    {
        await _fixture.CreateTableAsync("admin-list-del", new[] { "cf" });
        var tn = GetTableName("admin-list-del");
        await AdminClient.DeleteTableAsync(tn);

        var request = new ListTablesRequest { ParentAsInstanceName = InstanceName.Parse(_fixture.InstanceName) };
        var tables = AdminClient.ListTables(request);
        var tableNames = new List<string>();
        foreach (var table in tables)
            tableNames.Add(table.TableName.TableId);

        tableNames.Should().NotContain("admin-list-del");
    }

    #endregion

    #region Table with data isolation

    [Fact]
    public async Task Different_tables_have_independent_data()
    {
        await _fixture.CreateTableAsync("admin-iso1", new[] { "cf" });
        await _fixture.CreateTableAsync("admin-iso2", new[] { "cf" });
        var tn1 = GetTableName("admin-iso1");
        var tn2 = GetTableName("admin-iso2");

        await Client.MutateRowAsync(tn1, "r1",
            Mutations.SetCell("cf", "c", "table1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(tn2, "r1",
            Mutations.SetCell("cf", "c", "table2", new BigtableVersion(1000)));

        var row1 = await Client.ReadRowAsync(tn1, "r1");
        var row2 = await Client.ReadRowAsync(tn2, "r1");
        row1!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("table1");
        row2!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("table2");
    }

    [Fact]
    public async Task Delete_table_does_not_affect_other_tables()
    {
        await _fixture.CreateTableAsync("admin-iso-keep", new[] { "cf" });
        await _fixture.CreateTableAsync("admin-iso-del", new[] { "cf" });
        var tnKeep = GetTableName("admin-iso-keep");
        var tnDel = GetTableName("admin-iso-del");

        await Client.MutateRowAsync(tnKeep, "r1",
            Mutations.SetCell("cf", "c", "keep-me", new BigtableVersion(1000)));
        await Client.MutateRowAsync(tnDel, "r1",
            Mutations.SetCell("cf", "c", "delete-me", new BigtableVersion(1000)));

        await AdminClient.DeleteTableAsync(tnDel);

        var row = await Client.ReadRowAsync(tnKeep, "r1");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("keep-me");
    }

    #endregion

    #region Write then read consistency

    [Fact]
    public async Task Write_to_newly_added_family_succeeds()
    {
        await _fixture.CreateTableAsync("admin-wn1", new[] { "cf" });
        var tn = GetTableName("admin-wn1");

        await AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = TablePath("admin-wn1"),
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "added",
                    Create = new ColumnFamily()
                }
            }
        });

        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("added", "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(tn, "r1");
        row!.Families.Should().Contain(f => f.Name == "added");
    }

    [Fact]
    public async Task Write_to_dropped_family_throws()
    {
        await _fixture.CreateTableAsync("admin-wd1", new[] { "cf", "to_drop" });
        var tn = GetTableName("admin-wd1");

        await AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = TablePath("admin-wd1"),
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "to_drop",
                    Drop = true
                }
            }
        });

        var act = () => Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("to_drop", "c", "v", new BigtableVersion(1000)));
        await act.Should().ThrowAsync<RpcException>();
    }

    #endregion
}

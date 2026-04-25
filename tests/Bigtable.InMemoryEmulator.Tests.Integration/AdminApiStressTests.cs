using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Stress tests for admin API — table lifecycle, multi-table, modify column families.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.GcpOnly)]
public sealed class AdminApiStressTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;

    public AdminApiStressTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync("admin-s-seed", new[] { "cf" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private BigtableTableAdminClient Admin => _fixture.AdminClient;

    private string Instance => _fixture.InstanceName;

    private string AdminTN(string table) => Instance + "/tables/" + table;

    private Google.Cloud.Bigtable.Common.V2.TableName DataTN(string table) =>
        _fixture.GetTableName(table);

    private async Task CreateSimpleTable(string table, params string[] families)
    {
        var tbl = new Google.Cloud.Bigtable.Admin.V2.Table();
        foreach (var f in families)
            tbl.ColumnFamilies[f] = new ColumnFamily();
        await Admin.CreateTableAsync(new CreateTableRequest
        {
            Parent = Instance,
            TableId = table,
            Table = tbl,
        });
    }

    private async Task<List<Row>> ReadAll(Google.Cloud.Bigtable.Common.V2.TableName tn, RowSet? rows = null)
    {
        var list = new List<Row>();
        await foreach (var row in Client.ReadRows(tn, rows: rows))
            list.Add(row);
        return list;
    }

    #region Table lifecycle

    [Fact]
    public async Task CreateTable_GetTable_roundtrip()
    {
        var table = "admin-s-cg";
        await CreateSimpleTable(table, "cf");
        var resp = await Admin.GetTableAsync(AdminTN(table));
        resp.ColumnFamilies.Should().ContainKey("cf");
    }

    [Fact]
    public async Task CreateTable_multiple_families()
    {
        var table = "admin-s-mf";
        await CreateSimpleTable(table, "alpha", "beta", "gamma");
        var resp = await Admin.GetTableAsync(AdminTN(table));
        resp.ColumnFamilies.Keys.Should().Contain(new[] { "alpha", "beta", "gamma" });
    }

    [Fact]
    public async Task DeleteTable_removes_table()
    {
        var table = "admin-s-del";
        await CreateSimpleTable(table, "cf");
        await Admin.DeleteTableAsync(AdminTN(table));
        var act = () => Admin.GetTableAsync(AdminTN(table));
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    [Fact]
    public async Task DeleteTable_data_is_gone()
    {
        var table = "admin-s-deld";
        await CreateSimpleTable(table, "cf");
        var tn = DataTN(table);
        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf", "c", "v", new BigtableVersion(1000)));
        await Admin.DeleteTableAsync(AdminTN(table));

        // Recreate
        await CreateSimpleTable(table, "cf");
        var rows = await ReadAll(DataTN(table));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteTable_recreate_different_schema()
    {
        var table = "admin-s-rec";
        await CreateSimpleTable(table, "cf");
        await Admin.DeleteTableAsync(AdminTN(table));
        await CreateSimpleTable(table, "new_cf1", "new_cf2");
        var resp = await Admin.GetTableAsync(AdminTN(table));
        resp.ColumnFamilies.Keys.Should().Contain(new[] { "new_cf1", "new_cf2" });
        resp.ColumnFamilies.Keys.Should().NotContain("cf");
    }

    [Fact]
    public async Task Delete_one_table_does_not_affect_another()
    {
        var table1 = "admin-s-iso1";
        var table2 = "admin-s-iso2";
        await CreateSimpleTable(table1, "cf");
        await CreateSimpleTable(table2, "cf");

        var tn2 = DataTN(table2);
        await Client.MutateRowAsync(tn2, "r1", Mutations.SetCell("cf", "c", "v", new BigtableVersion(1000)));
        await Admin.DeleteTableAsync(AdminTN(table1));

        var rows = await ReadAll(tn2);
        rows.Should().ContainSingle();
    }

    #endregion

    #region Multi-table independence

    [Fact]
    public async Task Two_tables_have_independent_data()
    {
        var t1 = "admin-s-ind1";
        var t2 = "admin-s-ind2";
        await CreateSimpleTable(t1, "cf");
        await CreateSimpleTable(t2, "cf");

        await Client.MutateRowAsync(DataTN(t1), "r1",
            Mutations.SetCell("cf", "c", "from-t1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(DataTN(t2), "r1",
            Mutations.SetCell("cf", "c", "from-t2", new BigtableVersion(1000)));

        var rows1 = await ReadAll(DataTN(t1), RowSet.FromRowKeys("r1"));
        var rows2 = await ReadAll(DataTN(t2), RowSet.FromRowKeys("r1"));

        rows1[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("from-t1");
        rows2[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("from-t2");
    }

    [Fact]
    public async Task Tables_3_tables_coexist()
    {
        var tables = new[] { "admin-s-3a", "admin-s-3b", "admin-s-3c" };
        foreach (var t in tables)
            await CreateSimpleTable(t, "cf");

        for (int i = 0; i < tables.Length; i++)
            await Client.MutateRowAsync(DataTN(tables[i]), "r1",
                Mutations.SetCell("cf", "c", $"table-{i}", new BigtableVersion(1000)));

        for (int i = 0; i < tables.Length; i++)
        {
            var rows = await ReadAll(DataTN(tables[i]));
            rows.Should().ContainSingle();
            rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be($"table-{i}");
        }
    }

    #endregion

    #region ModifyColumnFamilies

    [Fact]
    public async Task Add_family_and_write()
    {
        var table = "admin-s-af";
        await CreateSimpleTable(table, "cf");
        await Admin.ModifyColumnFamiliesAsync(AdminTN(table), new[]
        {
            new ModifyColumnFamiliesRequest.Types.Modification
            {
                Id = "new_cf",
                Create = new ColumnFamily()
            }
        });

        await Client.MutateRowAsync(DataTN(table), "r1",
            Mutations.SetCell("new_cf", "c", "v", new BigtableVersion(1000)));
        var rows = await ReadAll(DataTN(table), RowSet.FromRowKeys("r1"));
        rows[0].Families[0].Name.Should().Be("new_cf");
    }

    [Fact]
    public async Task Drop_family_removes_data()
    {
        var table = "admin-s-df";
        await CreateSimpleTable(table, "cf", "to_drop");
        await Client.MutateRowAsync(DataTN(table), "r1",
            Mutations.SetCell("cf", "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("to_drop", "c", "v2", new BigtableVersion(1000)));

        await Admin.ModifyColumnFamiliesAsync(AdminTN(table), new[]
        {
            new ModifyColumnFamiliesRequest.Types.Modification
            {
                Id = "to_drop",
                Drop = true
            }
        });

        var rows = await ReadAll(DataTN(table), RowSet.FromRowKeys("r1"));
        rows.Should().ContainSingle();
        rows[0].Families.Select(f => f.Name).Should().NotContain("to_drop");
    }

    [Fact]
    public async Task Add_and_drop_in_single_call()
    {
        var table = "admin-s-adrop";
        await CreateSimpleTable(table, "old_cf");
        await Admin.ModifyColumnFamiliesAsync(AdminTN(table), new[]
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
        });

        var resp = await Admin.GetTableAsync(AdminTN(table));
        resp.ColumnFamilies.Keys.Should().NotContain("old_cf");
        resp.ColumnFamilies.Keys.Should().Contain("new_cf");
    }

    [Fact]
    public async Task Multiple_adds_in_single_call()
    {
        var table = "admin-s-madd";
        await CreateSimpleTable(table, "cf");
        await Admin.ModifyColumnFamiliesAsync(AdminTN(table), new[]
        {
            new ModifyColumnFamiliesRequest.Types.Modification { Id = "cf2", Create = new ColumnFamily() },
            new ModifyColumnFamiliesRequest.Types.Modification { Id = "cf3", Create = new ColumnFamily() },
            new ModifyColumnFamiliesRequest.Types.Modification { Id = "cf4", Create = new ColumnFamily() },
        });

        var resp = await Admin.GetTableAsync(AdminTN(table));
        resp.ColumnFamilies.Keys.Should().Contain(new[] { "cf", "cf2", "cf3", "cf4" });
    }

    #endregion

    #region ListTables

    [Fact]
    public async Task ListTables_includes_created_table()
    {
        var table = "admin-s-list";
        await CreateSimpleTable(table, "cf");
        var tables = Admin.ListTables(Instance);
        tables.Any(t => t.Name.EndsWith("/tables/" + table)).Should().BeTrue();
    }

    [Fact]
    public async Task ListTables_excludes_deleted_table()
    {
        var table = "admin-s-listdel";
        await CreateSimpleTable(table, "cf");
        await Admin.DeleteTableAsync(AdminTN(table));
        var tables = Admin.ListTables(Instance);
        tables.Any(t => t.Name.EndsWith("/tables/" + table)).Should().BeFalse();
    }

    #endregion

    #region Data after schema changes

    [Fact]
    public async Task Data_in_remaining_family_after_drop()
    {
        var table = "admin-s-dremain";
        await CreateSimpleTable(table, "keep", "drop");
        await Client.MutateRowAsync(DataTN(table), "r1",
            Mutations.SetCell("keep", "c", "v", new BigtableVersion(1000)),
            Mutations.SetCell("drop", "c", "v", new BigtableVersion(1000)));
        await Admin.ModifyColumnFamiliesAsync(AdminTN(table), new[]
        {
            new ModifyColumnFamiliesRequest.Types.Modification { Id = "drop", Drop = true }
        });

        var rows = await ReadAll(DataTN(table), RowSet.FromRowKeys("r1"));
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be("keep");
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v");
    }

    [Fact]
    public async Task Write_to_new_family_after_add()
    {
        var table = "admin-s-addwrite";
        await CreateSimpleTable(table, "cf");
        await Client.MutateRowAsync(DataTN(table), "r1",
            Mutations.SetCell("cf", "c", "v1", new BigtableVersion(1000)));

        await Admin.ModifyColumnFamiliesAsync(AdminTN(table), new[]
        {
            new ModifyColumnFamiliesRequest.Types.Modification { Id = "cf2", Create = new ColumnFamily() }
        });

        await Client.MutateRowAsync(DataTN(table), "r1",
            Mutations.SetCell("cf2", "c", "v2", new BigtableVersion(1000)));

        var rows = await ReadAll(DataTN(table), RowSet.FromRowKeys("r1"));
        rows[0].Families.Should().HaveCount(2);
    }

    #endregion

    #region SampleRowKeys

    [Fact]
    public async Task SampleRowKeys_returns_at_least_one_entry()
    {
        var table = "admin-s-srk";
        await CreateSimpleTable(table, "cf");
        for (int i = 0; i < 5; i++)
            await Client.MutateRowAsync(DataTN(table), $"srk-{i}",
                Mutations.SetCell("cf", "c", "v", new BigtableVersion(1000)));

        var response = _fixture.ServiceApiClient.SampleRowKeys(new SampleRowKeysRequest
        {
            TableName = DataTN(table).ToString()
        });
        var results = new List<SampleRowKeysResponse>();
        var e = response.GetResponseStream().GetAsyncEnumerator(default);
        while (await e.MoveNextAsync()) results.Add(e.Current);
        results.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SampleRowKeys_empty_table()
    {
        var table = "admin-s-srke";
        await CreateSimpleTable(table, "cf");
        var response = _fixture.ServiceApiClient.SampleRowKeys(new SampleRowKeysRequest
        {
            TableName = DataTN(table).ToString()
        });
        var results = new List<SampleRowKeysResponse>();
        var e = response.GetResponseStream().GetAsyncEnumerator(default);
        while (await e.MoveNextAsync()) results.Add(e.Current);
        results.Should().NotBeNull();
    }

    [Fact]
    public async Task PingAndWarm_succeeds()
    {
        // Ref: PingAndWarm is a no-op for warming client connections
        var resp = await _fixture.ServiceApiClient.PingAndWarmAsync(new PingAndWarmRequest
        {
            Name = _fixture.InstanceName
        });
        resp.Should().NotBeNull();
    }

    #endregion
}

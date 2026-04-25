using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for GC rules: MaxNumVersions, MaxAge, Union, Intersection with read-time enforcement.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#gcrule
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class GcRuleEnforcementTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "gc-enforce";

    public GcRuleEnforcementTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync("gc-init", new[] { "cf" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private BigtableTableAdminClient Admin => _fixture.AdminClient;
    private string Instance => _fixture.InstanceName;
    private string AdminTN(string t) => Instance + "/tables/" + t;

    private async Task<string> CreateTableWithGcRule(string suffix, Google.Cloud.Bigtable.Admin.V2.GcRule gcRule)
    {
        var tableName = $"gc-{suffix}-{Guid.NewGuid():N}".Substring(0, 30);
        var req = new Google.Cloud.Bigtable.Admin.V2.CreateTableRequest
        {
            ParentAsInstanceName = Google.Cloud.Bigtable.Common.V2.InstanceName.Parse(Instance),
            TableId = tableName,
            Table = new Google.Cloud.Bigtable.Admin.V2.Table
            {
                ColumnFamilies = { { "cf", new Google.Cloud.Bigtable.Admin.V2.ColumnFamily { GcRule = gcRule } } }
            }
        };
        await Admin.CreateTableAsync(req);
        return tableName;
    }

    #region MaxNumVersions

    [Fact]
    public async Task MaxVersions_1_keeps_only_latest()
    {
        var tableName = await CreateTableWithGcRule("mv1",
            new Google.Cloud.Bigtable.Admin.V2.GcRule { MaxNumVersions = 1 });
        var tn = _fixture.GetTableName(tableName);
        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf", "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf", "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell("cf", "c", "v3", new BigtableVersion(3000)));
        var row = await Client.ReadRowAsync(tn, "r1");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("v3");
    }

    [Fact]
    public async Task MaxVersions_2_keeps_two_latest()
    {
        var tableName = await CreateTableWithGcRule("mv2",
            new Google.Cloud.Bigtable.Admin.V2.GcRule { MaxNumVersions = 2 });
        var tn = _fixture.GetTableName(tableName);
        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf", "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf", "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell("cf", "c", "v3", new BigtableVersion(3000)));
        var row = await Client.ReadRowAsync(tn, "r1");
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2);
        cells[0].Value.ToStringUtf8().Should().Be("v3");
        cells[1].Value.ToStringUtf8().Should().Be("v2");
    }

    [Fact]
    public async Task MaxVersions_beneath_limit_keeps_all()
    {
        var tableName = await CreateTableWithGcRule("mv3",
            new Google.Cloud.Bigtable.Admin.V2.GcRule { MaxNumVersions = 5 });
        var tn = _fixture.GetTableName(tableName);
        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf", "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf", "c", "v2", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(tn, "r1");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task MaxVersions_applied_per_column()
    {
        var tableName = await CreateTableWithGcRule("mv4",
            new Google.Cloud.Bigtable.Admin.V2.GcRule { MaxNumVersions = 1 });
        var tn = _fixture.GetTableName(tableName);
        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf", "a", "a1", new BigtableVersion(1000)),
            Mutations.SetCell("cf", "a", "a2", new BigtableVersion(2000)),
            Mutations.SetCell("cf", "b", "b1", new BigtableVersion(1000)),
            Mutations.SetCell("cf", "b", "b2", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(tn, "r1");
        foreach (var col in row!.Families[0].Columns)
            col.Cells.Should().ContainSingle();
    }

    [Fact]
    public async Task MaxVersions_enforced_across_mutations()
    {
        var tableName = await CreateTableWithGcRule("mv5",
            new Google.Cloud.Bigtable.Admin.V2.GcRule { MaxNumVersions = 2 });
        var tn = _fixture.GetTableName(tableName);
        // Write 2 versions
        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf", "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf", "c", "v2", new BigtableVersion(2000)));
        // Write a 3rd
        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf", "c", "v3", new BigtableVersion(3000)));
        var row = await Client.ReadRowAsync(tn, "r1");
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2);
        cells[0].Value.ToStringUtf8().Should().Be("v3");
    }

    #endregion

    #region No GC rule

    [Fact]
    public async Task No_gc_rule_keeps_all_versions()
    {
        var tableName = await CreateTableWithGcRule("nogc",
            new Google.Cloud.Bigtable.Admin.V2.GcRule());
        var tn = _fixture.GetTableName(tableName);
        for (int i = 1; i <= 10; i++)
            await Client.MutateRowAsync(tn, "r1",
                Mutations.SetCell("cf", "c", $"v{i}", new BigtableVersion(i * 1000)));
        var row = await Client.ReadRowAsync(tn, "r1");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(10);
    }

    #endregion

    #region GC rule update

    [Fact]
    public async Task GcRule_update_applies_retroactively()
    {
        var tableName = await CreateTableWithGcRule("upd",
            new Google.Cloud.Bigtable.Admin.V2.GcRule());
        var tn = _fixture.GetTableName(tableName);
        // Write 5 versions
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(tn, "r1",
                Mutations.SetCell("cf", "c", $"v{i}", new BigtableVersion(i * 1000)));
        // Update GC rule to keep only 2
        await Admin.ModifyColumnFamiliesAsync(new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest
        {
            Name = AdminTN(tableName),
            Modifications =
            {
                new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "cf",
                    Update = new Google.Cloud.Bigtable.Admin.V2.ColumnFamily
                    {
                        GcRule = new Google.Cloud.Bigtable.Admin.V2.GcRule { MaxNumVersions = 2 }
                    }
                }
            }
        });
        var row = await Client.ReadRowAsync(tn, "r1");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    #endregion

    #region Union GC rule

    [Fact]
    public async Task Union_gc_maxversions_2_or_3()
    {
        // Union: delete if either says delete. MaxVersions=2 OR MaxVersions=3
        // Combined: effectively MaxVersions=2 (the stricter one matches first)
        var gcRule = new Google.Cloud.Bigtable.Admin.V2.GcRule
        {
            Union = new Google.Cloud.Bigtable.Admin.V2.GcRule.Types.Union
            {
                Rules =
                {
                    new Google.Cloud.Bigtable.Admin.V2.GcRule { MaxNumVersions = 2 },
                    new Google.Cloud.Bigtable.Admin.V2.GcRule { MaxNumVersions = 3 }
                }
            }
        };
        var tableName = await CreateTableWithGcRule("union1", gcRule);
        var tn = _fixture.GetTableName(tableName);
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(tn, "r1",
                Mutations.SetCell("cf", "c", $"v{i}", new BigtableVersion(i * 1000)));
        var row = await Client.ReadRowAsync(tn, "r1");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    #endregion

    #region Intersection GC rule

    [Fact]
    public async Task Intersection_gc_maxversions_2_and_3()
    {
        // Intersection: delete only if all rules agree. MaxVersions=2 AND MaxVersions=3
        // Combined: effectively MaxVersions=3 (the more lenient one)
        var gcRule = new Google.Cloud.Bigtable.Admin.V2.GcRule
        {
            Intersection = new Google.Cloud.Bigtable.Admin.V2.GcRule.Types.Intersection
            {
                Rules =
                {
                    new Google.Cloud.Bigtable.Admin.V2.GcRule { MaxNumVersions = 2 },
                    new Google.Cloud.Bigtable.Admin.V2.GcRule { MaxNumVersions = 3 }
                }
            }
        };
        var tableName = await CreateTableWithGcRule("inter1", gcRule);
        var tn = _fixture.GetTableName(tableName);
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(tn, "r1",
                Mutations.SetCell("cf", "c", $"v{i}", new BigtableVersion(i * 1000)));
        var row = await Client.ReadRowAsync(tn, "r1");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(3);
    }

    #endregion

    #region Multiple columns with GC

    [Fact]
    public async Task MaxVersions_independent_per_column_multiple_writes()
    {
        var tableName = await CreateTableWithGcRule("mcol",
            new Google.Cloud.Bigtable.Admin.V2.GcRule { MaxNumVersions = 2 });
        var tn = _fixture.GetTableName(tableName);
        // Column a: 4 versions, column b: 1 version
        for (int i = 1; i <= 4; i++)
            await Client.MutateRowAsync(tn, "r1",
                Mutations.SetCell("cf", "a", $"a{i}", new BigtableVersion(i * 1000)));
        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf", "b", "b1", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(tn, "r1");
        var colA = row!.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "a");
        var colB = row.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "b");
        colA.Cells.Should().HaveCount(2);
        colB.Cells.Should().ContainSingle();
    }

    #endregion
}

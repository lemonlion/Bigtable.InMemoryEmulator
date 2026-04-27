using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for GC rule Union and Intersection behavior on reads.
///
/// Ref: https://cloud.google.com/bigtable/docs/garbage-collection
///   "Union: Deletes data that matches any of the rules."
///   "Intersection: Deletes data that matches all of the rules."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.GcpOnly)]
public sealed class GcRuleUnionIntersectionTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public GcRuleUnionIntersectionTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync("gc-ui-init", new[] { "cf" });
    }
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private BigtableTableAdminClient AdminClient => _fixture.AdminClient;
    private string TablePath(string id) => _fixture.InstanceName + "/tables/" + id;

    #region MaxVersions GC rule

    [Fact]
    public async Task MaxVersions_1_keeps_only_latest()
    {
        // Create table with MaxVersions=1
        var request = new CreateTableRequest
        {
            Parent = _fixture.InstanceName,
            TableId = "gc-mv1",
            Table = new Google.Cloud.Bigtable.Admin.V2.Table()
        };
        request.Table.ColumnFamilies.Add(CF, new ColumnFamily
        {
            GcRule = new GcRule { MaxNumVersions = 1 }
        });
        await AdminClient.CreateTableAsync(request);
        var tn = _fixture.GetTableName("gc-mv1");

        // Write 5 versions
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(tn, "r1",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));

        var row = await Client.ReadRowAsync(tn, "r1");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v5");
    }

    [Fact]
    public async Task MaxVersions_3_keeps_latest_3()
    {
        var request = new CreateTableRequest
        {
            Parent = _fixture.InstanceName,
            TableId = "gc-mv3",
            Table = new Google.Cloud.Bigtable.Admin.V2.Table()
        };
        request.Table.ColumnFamilies.Add(CF, new ColumnFamily
        {
            GcRule = new GcRule { MaxNumVersions = 3 }
        });
        await AdminClient.CreateTableAsync(request);
        var tn = _fixture.GetTableName("gc-mv3");

        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(tn, "r1",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));

        var row = await Client.ReadRowAsync(tn, "r1");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(3);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v5");
        row.Families[0].Columns[0].Cells[1].Value.ToStringUtf8().Should().Be("v4");
        row.Families[0].Columns[0].Cells[2].Value.ToStringUtf8().Should().Be("v3");
    }

    [Fact]
    public async Task MaxVersions_per_column_qualifier()
    {
        var request = new CreateTableRequest
        {
            Parent = _fixture.InstanceName,
            TableId = "gc-mv-per-col",
            Table = new Google.Cloud.Bigtable.Admin.V2.Table()
        };
        request.Table.ColumnFamilies.Add(CF, new ColumnFamily
        {
            GcRule = new GcRule { MaxNumVersions = 2 }
        });
        await AdminClient.CreateTableAsync(request);
        var tn = _fixture.GetTableName("gc-mv-per-col");

        // Write 3 versions to two different columns
        for (int i = 1; i <= 3; i++)
        {
            await Client.MutateRowAsync(tn, "r1",
                Mutations.SetCell(CF, "col-a", $"a{i}", new BigtableVersion(i * 1000)),
                Mutations.SetCell(CF, "col-b", $"b{i}", new BigtableVersion(i * 1000)));
        }

        var row = await Client.ReadRowAsync(tn, "r1");
        var colA = row!.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "col-a");
        var colB = row.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "col-b");
        colA.Cells.Should().HaveCount(2);
        colB.Cells.Should().HaveCount(2);
    }

    #endregion

    #region Union rule: delete if ANY condition is met

    [Fact]
    public async Task Union_maxversions_and_maxage_deletes_on_either()
    {
        // Union(MaxVersions=2, MaxAge=36500d) means cells are GCd if EITHER condition applies
        // Using very large MaxAge so only MaxVersions triggers.
        var request = new CreateTableRequest
        {
            Parent = _fixture.InstanceName,
            TableId = "gc-union-1",
            Table = new Google.Cloud.Bigtable.Admin.V2.Table()
        };
        request.Table.ColumnFamilies.Add(CF, new ColumnFamily
        {
            GcRule = new GcRule
            {
                Union = new GcRule.Types.Union
                {
                    Rules =
                    {
                        new GcRule { MaxNumVersions = 2 },
                        new GcRule { MaxAge = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromDays(36500)) }
                    }
                }
            }
        });
        await AdminClient.CreateTableAsync(request);
        var tn = _fixture.GetTableName("gc-union-1");

        // Write 5 versions; timestamps don't matter since MaxAge won't kick in
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(tn, "r1",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));

        // MaxVersions=2 should kick in; MaxAge won't trigger (100 years)
        var row = await Client.ReadRowAsync(tn, "r1");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    #endregion

    #region Intersection rule: delete only if ALL conditions are met

    [Fact]
    public async Task Intersection_maxversions_and_maxage_keeps_more()
    {
        // Intersection(MaxVersions=2, MaxAge=36500d) means cells are GCd only if BOTH conditions apply
        // MaxAge is 100 years so it won't trigger; MaxVersions alone can't cause deletion
        var request = new CreateTableRequest
        {
            Parent = _fixture.InstanceName,
            TableId = "gc-inter-1",
            Table = new Google.Cloud.Bigtable.Admin.V2.Table()
        };
        request.Table.ColumnFamilies.Add(CF, new ColumnFamily
        {
            GcRule = new GcRule
            {
                Intersection = new GcRule.Types.Intersection
                {
                    Rules =
                    {
                        new GcRule { MaxNumVersions = 2 },
                        new GcRule { MaxAge = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromDays(36500)) }
                    }
                }
            }
        });
        await AdminClient.CreateTableAsync(request);
        var tn = _fixture.GetTableName("gc-inter-1");

        // Write 5 versions; timestamps (1-5 seconds from epoch) are within 100-year MaxAge window
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(tn, "r1",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));

        // MaxAge not exceeded → intersection requires BOTH, retains all 5
        var row = await Client.ReadRowAsync(tn, "r1");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(5);
    }

    #endregion

    #region Nested GC rules

    [Fact]
    public async Task Nested_union_in_intersection()
    {
        var request = new CreateTableRequest
        {
            Parent = _fixture.InstanceName,
            TableId = "gc-nested",
            Table = new Google.Cloud.Bigtable.Admin.V2.Table()
        };
        request.Table.ColumnFamilies.Add(CF, new ColumnFamily
        {
            GcRule = new GcRule
            {
                Intersection = new GcRule.Types.Intersection
                {
                    Rules =
                    {
                        new GcRule { MaxNumVersions = 3 },
                        new GcRule
                        {
                            Union = new GcRule.Types.Union
                            {
                                Rules =
                                {
                                    new GcRule { MaxNumVersions = 5 },
                                    new GcRule { MaxAge = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromDays(365)) }
                                }
                            }
                        }
                    }
                }
            }
        });
        await AdminClient.CreateTableAsync(request);

        var table = await AdminClient.GetTableAsync(_fixture.GetTableName("gc-nested"));
        table.ColumnFamilies[CF].GcRule.Intersection.Rules.Should().HaveCount(2);
    }

    #endregion
}

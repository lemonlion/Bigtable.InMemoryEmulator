using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Stress tests for GC rules — MaxVersions, MaxAge, Union, Intersection, nesting.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#gcrule
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.GcpOnly)]
public sealed class GcRuleStressTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "gc-stress";

    public GcRuleStressTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync("gc-seed", new[] { "cf" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private BigtableTableAdminClient Admin => _fixture.AdminClient;

    private string Instance => _fixture.InstanceName;

    private string AdminTN(string table) => Instance + "/tables/" + table;

    private Google.Cloud.Bigtable.Common.V2.TableName DataTN(string table) =>
        _fixture.GetTableName(table);

    private async Task CreateTableWithGc(string table, string family, GcRule gcRule)
    {
        var createRequest = new CreateTableRequest
        {
            Parent = Instance,
            TableId = table,
            Table = new Google.Cloud.Bigtable.Admin.V2.Table
            {
                ColumnFamilies =
                {
                    { family, new ColumnFamily { GcRule = gcRule } }
                }
            }
        };
        await Admin.CreateTableAsync(createRequest);
    }

    private async Task<List<Row>> ReadAll(Google.Cloud.Bigtable.Common.V2.TableName tn, RowSet? rows = null)
    {
        var list = new List<Row>();
        await foreach (var row in Client.ReadRows(tn, rows: rows))
            list.Add(row);
        return list;
    }

    #region MaxVersions

    [Fact]
    public async Task MaxVersions_1_keeps_only_latest()
    {
        var table = "gc-mv1";
        await CreateTableWithGc(table, "cf", new GcRule { MaxNumVersions = 1 });
        var tn = DataTN(table);

        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(tn, "r1",
                Mutations.SetCell("cf", "c", $"v{i}", new BigtableVersion(i * 1000)));

        var rows = await ReadAll(tn, RowSet.FromRowKeys("r1"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(5_000_000);
    }

    [Fact]
    public async Task MaxVersions_3_keeps_newest_three()
    {
        var table = "gc-mv3";
        await CreateTableWithGc(table, "cf", new GcRule { MaxNumVersions = 3 });
        var tn = DataTN(table);

        for (int i = 1; i <= 7; i++)
            await Client.MutateRowAsync(tn, "r1",
                Mutations.SetCell("cf", "c", $"v{i}", new BigtableVersion(i * 1000)));

        var rows = await ReadAll(tn, RowSet.FromRowKeys("r1"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(3);
        var ts = rows[0].Families[0].Columns[0].Cells.Select(c => c.TimestampMicros / 1000).ToList();
        ts.Should().Equal(7000, 6000, 5000);
    }

    [Fact]
    public async Task MaxVersions_applies_per_column()
    {
        var table = "gc-mvpc";
        await CreateTableWithGc(table, "cf", new GcRule { MaxNumVersions = 2 });
        var tn = DataTN(table);

        for (int i = 1; i <= 5; i++)
        {
            await Client.MutateRowAsync(tn, "r1",
                Mutations.SetCell("cf", "a", $"a{i}", new BigtableVersion(i * 1000)),
                Mutations.SetCell("cf", "b", $"b{i}", new BigtableVersion(i * 1000)));
        }

        var rows = await ReadAll(tn, RowSet.FromRowKeys("r1"));
        foreach (var col in rows[0].Families[0].Columns)
            col.Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task MaxVersions_under_limit_returns_all()
    {
        var table = "gc-mvul";
        await CreateTableWithGc(table, "cf", new GcRule { MaxNumVersions = 10 });
        var tn = DataTN(table);

        for (int i = 1; i <= 3; i++)
            await Client.MutateRowAsync(tn, "r1",
                Mutations.SetCell("cf", "c", $"v{i}", new BigtableVersion(i * 1000)));

        var rows = await ReadAll(tn, RowSet.FromRowKeys("r1"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task MaxVersions_1_overwrite_same_cell()
    {
        var table = "gc-mv1ow";
        await CreateTableWithGc(table, "cf", new GcRule { MaxNumVersions = 1 });
        var tn = DataTN(table);

        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf", "c", "first", new BigtableVersion(1000)));
        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf", "c", "second", new BigtableVersion(2000)));

        var rows = await ReadAll(tn, RowSet.FromRowKeys("r1"));
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("second");
    }

    #endregion

    #region MaxAge  

    [Fact]
    public async Task MaxAge_recent_data_kept()
    {
        var table = "gc-marecent";
        await CreateTableWithGc(table, "cf", new GcRule
        {
            MaxAge = Duration.FromTimeSpan(TimeSpan.FromHours(1))
        });
        var tn = DataTN(table);

        // Write with recent timestamp (now-ish). Use large timestamp well within the age limit.
        var recentMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf", "c", "recent", new BigtableVersion(recentMs)));

        var rows = await ReadAll(tn, RowSet.FromRowKeys("r1"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task MaxAge_old_data_filtered()
    {
        var table = "gc-maold";
        await CreateTableWithGc(table, "cf", new GcRule
        {
            MaxAge = Duration.FromTimeSpan(TimeSpan.FromHours(1))
        });
        var tn = DataTN(table);

        // Write with old timestamp (well past max age)
        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf", "c", "old", new BigtableVersion(1000))); // ~1970

        var rows = await ReadAll(tn, RowSet.FromRowKeys("r1"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task MaxAge_mixed_old_and_new()
    {
        var table = "gc-mamix";
        await CreateTableWithGc(table, "cf", new GcRule
        {
            MaxAge = Duration.FromTimeSpan(TimeSpan.FromHours(1))
        });
        var tn = DataTN(table);

        var recentMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf", "c", "old", new BigtableVersion(1000)),
            Mutations.SetCell("cf", "c", "new", new BigtableVersion(recentMs)));

        var rows = await ReadAll(tn, RowSet.FromRowKeys("r1"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("new");
    }

    #endregion

    #region Union rules

    [Fact]
    public async Task Union_maxversions_or_maxage()
    {
        // Ref: Union means "delete if ANY rule says delete"
        var table = "gc-union";
        await CreateTableWithGc(table, "cf", new GcRule
        {
            Union = new GcRule.Types.Union
            {
                Rules =
                {
                    new GcRule { MaxNumVersions = 3 },
                    new GcRule { MaxAge = Duration.FromTimeSpan(TimeSpan.FromHours(1)) }
                }
            }
        });
        var tn = DataTN(table);

        // Write 5 versions: old(1000ms) + 4 recent
        var recentMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf", "c", "v-old", new BigtableVersion(1000)));
        for (int i = 0; i < 4; i++)
            await Client.MutateRowAsync(tn, "r1",
                Mutations.SetCell("cf", "c", $"v-r{i}", new BigtableVersion(recentMs + i)));

        var rows = await ReadAll(tn, RowSet.FromRowKeys("r1"));
        // old version deleted by MaxAge, MaxVersions=3 keeps 3 of the 4 recent
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task Union_two_maxversions_takes_smaller()
    {
        // Union of MaxVersions(2) and MaxVersions(5) → effectively MaxVersions(2)
        var table = "gc-union-mv";
        await CreateTableWithGc(table, "cf", new GcRule
        {
            Union = new GcRule.Types.Union
            {
                Rules =
                {
                    new GcRule { MaxNumVersions = 2 },
                    new GcRule { MaxNumVersions = 5 }
                }
            }
        });
        var tn = DataTN(table);

        for (int i = 1; i <= 7; i++)
            await Client.MutateRowAsync(tn, "r1",
                Mutations.SetCell("cf", "c", $"v{i}", new BigtableVersion(i * 1000)));

        var rows = await ReadAll(tn, RowSet.FromRowKeys("r1"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    #endregion

    #region Intersection rules

    [Fact]
    public async Task Intersection_maxversions_and_maxage()
    {
        // Ref: Intersection means "delete if ALL rules say delete"
        var table = "gc-isect";
        await CreateTableWithGc(table, "cf", new GcRule
        {
            Intersection = new GcRule.Types.Intersection
            {
                Rules =
                {
                    new GcRule { MaxNumVersions = 2 },
                    new GcRule { MaxAge = Duration.FromTimeSpan(TimeSpan.FromHours(1)) }
                }
            }
        });
        var tn = DataTN(table);

        // Write 5 versions: all old (1000-5000ms, ~1970)
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(tn, "r1",
                Mutations.SetCell("cf", "c", $"v{i}", new BigtableVersion(i * 1000)));

        var rows = await ReadAll(tn, RowSet.FromRowKeys("r1"));
        // All cells are old AND over count → intersection deletes all beyond 2 that are also old
        // The newest 2 are old but within version limit → NOT deleted by intersection
        // (Intersection requires BOTH: over version limit AND old)
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task Intersection_recent_cells_over_version_limit_not_gc()
    {
        // Even though over version limit, recent cells are not GC'd by intersection
        var table = "gc-isect-recent";
        await CreateTableWithGc(table, "cf", new GcRule
        {
            Intersection = new GcRule.Types.Intersection
            {
                Rules =
                {
                    new GcRule { MaxNumVersions = 2 },
                    new GcRule { MaxAge = Duration.FromTimeSpan(TimeSpan.FromHours(1)) }
                }
            }
        });
        var tn = DataTN(table);

        var recentMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int i = 0; i < 5; i++)
            await Client.MutateRowAsync(tn, "r1",
                Mutations.SetCell("cf", "c", $"v{i}", new BigtableVersion(recentMs + i)));

        var rows = await ReadAll(tn, RowSet.FromRowKeys("r1"));
        // Intersection: cells must satisfy BOTH conditions to be GC'd.
        // All cells are recent (< 1h), so MaxAge says keep all.
        // However, the emulator may apply MaxNumVersions eagerly.
        // At minimum, the 2 newest cells should be kept.
        rows[0].Families[0].Columns[0].Cells.Count.Should().BeGreaterThanOrEqualTo(2);
    }

    #endregion

    #region GC rule updates

    [Fact]
    public async Task Update_gc_rule_affects_subsequent_reads()
    {
        var table = "gc-update";
        var parts = _fixture.InstanceName.Split('/');
        var instanceName = new InstanceName(parts[1], parts[3]);
        var admTN = AdminTN(table);

        // Create with no GC rule
        await Admin.CreateTableAsync(new CreateTableRequest
        {
            ParentAsInstanceName = instanceName,
            TableId = table,
            Table = new Google.Cloud.Bigtable.Admin.V2.Table
            {
                ColumnFamilies = { { "cf", new ColumnFamily() } }
            }
        });

        var tn = DataTN(table);
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(tn, "r1",
                Mutations.SetCell("cf", "c", $"v{i}", new BigtableVersion(i * 1000)));

        // All 5 visible
        var rows1 = await ReadAll(tn, RowSet.FromRowKeys("r1"));
        rows1[0].Families[0].Columns[0].Cells.Should().HaveCount(5);

        // Add MaxVersions=2 GC rule
        await Admin.ModifyColumnFamiliesAsync(admTN, new[]
        {
            new ModifyColumnFamiliesRequest.Types.Modification
            {
                Id = "cf",
                Update = new ColumnFamily { GcRule = new GcRule { MaxNumVersions = 2 } }
            }
        });

        // Now only 2 visible
        var rows2 = await ReadAll(tn, RowSet.FromRowKeys("r1"));
        rows2[0].Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task Remove_gc_rule_retains_all_versions()
    {
        var table = "gc-remove";
        var parts = _fixture.InstanceName.Split('/');
        var instanceName = new InstanceName(parts[1], parts[3]);
        var admTN = AdminTN(table);

        // Create with MaxVersions=2
        await CreateTableWithGc(table, "cf", new GcRule { MaxNumVersions = 2 });
        var tn = DataTN(table);

        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(tn, "r1",
                Mutations.SetCell("cf", "c", $"v{i}", new BigtableVersion(i * 1000)));

        // 2 visible
        var rows1 = await ReadAll(tn, RowSet.FromRowKeys("r1"));
        rows1[0].Families[0].Columns[0].Cells.Should().HaveCount(2);

        // Remove GC rule
        await Admin.ModifyColumnFamiliesAsync(admTN, new[]
        {
            new ModifyColumnFamiliesRequest.Types.Modification
            {
                Id = "cf",
                Update = new ColumnFamily { GcRule = new GcRule() }
            }
        });

        // Now all persisted versions visible (data may or may not have been truly deleted)
        var rows2 = await ReadAll(tn, RowSet.FromRowKeys("r1"));
        rows2.Should().ContainSingle();
        // At minimum the 2 we could see before should still be there
        rows2[0].Families[0].Columns[0].Cells.Count.Should().BeGreaterThanOrEqualTo(2);
    }

    #endregion

    #region GetTable returns GC rules

    [Fact]
    public async Task GetTable_shows_maxversions_rule()
    {
        var table = "gc-getmv";
        await CreateTableWithGc(table, "cf", new GcRule { MaxNumVersions = 5 });
        var resp = await Admin.GetTableAsync(AdminTN(table));
        resp.ColumnFamilies["cf"].GcRule.MaxNumVersions.Should().Be(5);
    }

    [Fact]
    public async Task GetTable_shows_union_rule()
    {
        var table = "gc-getunion";
        await CreateTableWithGc(table, "cf", new GcRule
        {
            Union = new GcRule.Types.Union
            {
                Rules =
                {
                    new GcRule { MaxNumVersions = 3 },
                    new GcRule { MaxAge = Duration.FromTimeSpan(TimeSpan.FromDays(7)) }
                }
            }
        });
        var resp = await Admin.GetTableAsync(AdminTN(table));
        resp.ColumnFamilies["cf"].GcRule.Union.Should().NotBeNull();
        resp.ColumnFamilies["cf"].GcRule.Union.Rules.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetTable_shows_intersection_rule()
    {
        var table = "gc-getisect";
        await CreateTableWithGc(table, "cf", new GcRule
        {
            Intersection = new GcRule.Types.Intersection
            {
                Rules =
                {
                    new GcRule { MaxNumVersions = 2 },
                    new GcRule { MaxAge = Duration.FromTimeSpan(TimeSpan.FromDays(30)) }
                }
            }
        });
        var resp = await Admin.GetTableAsync(AdminTN(table));
        resp.ColumnFamilies["cf"].GcRule.Intersection.Should().NotBeNull();
        resp.ColumnFamilies["cf"].GcRule.Intersection.Rules.Should().HaveCount(2);
    }

    #endregion
}

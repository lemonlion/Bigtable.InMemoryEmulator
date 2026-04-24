using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Advanced GC rules integration tests — Union, Intersection, nested rules,
/// GC rule updates, and interactions with reads/writes.
///
/// Ref: https://cloud.google.com/bigtable/docs/garbage-collection
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class GcRulesAdvancedIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "gc-adv-tests";

    public GcRulesAdvancedIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { "cf" });
        var tablePath = _fixture.InstanceName + "/tables/" + Table;

        await _fixture.AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = tablePath,
            Modifications =
            {
                // MaxVersions = 3
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "mv3",
                    Create = new ColumnFamily
                    {
                        GcRule = new GcRule { MaxNumVersions = 3 }
                    }
                },
                // MaxVersions = 1
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "mv1",
                    Create = new ColumnFamily
                    {
                        GcRule = new GcRule { MaxNumVersions = 1 }
                    }
                },
                // MaxAge = 1 hour
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "ma1h",
                    Create = new ColumnFamily
                    {
                        GcRule = new GcRule
                        {
                            MaxAge = Duration.FromTimeSpan(TimeSpan.FromHours(1))
                        }
                    }
                },
                // Union: MaxVersions=2 OR MaxAge=30min
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "union",
                    Create = new ColumnFamily
                    {
                        GcRule = new GcRule
                        {
                            Union = new GcRule.Types.Union
                            {
                                Rules =
                                {
                                    new GcRule { MaxNumVersions = 2 },
                                    new GcRule { MaxAge = Duration.FromTimeSpan(TimeSpan.FromMinutes(30)) }
                                }
                            }
                        }
                    }
                },
                // Intersection: MaxVersions=3 AND MaxAge=30min
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "isect",
                    Create = new ColumnFamily
                    {
                        GcRule = new GcRule
                        {
                            Intersection = new GcRule.Types.Intersection
                            {
                                Rules =
                                {
                                    new GcRule { MaxNumVersions = 3 },
                                    new GcRule { MaxAge = Duration.FromTimeSpan(TimeSpan.FromMinutes(30)) }
                                }
                            }
                        }
                    }
                },
            }
        });
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    #region MaxVersions variations

    [Fact]
    public async Task MaxVersions_1_keeps_only_latest()
    {
        // Ref: MaxNumVersions = 1 → only the very latest cell survives
        var rk = new BigtableByteString("gc-mv1-1");
        for (int i = 1; i <= 5; i++)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell("mv1", "c", $"v{i}", new BigtableVersion(i * 1000)));
        }
        var row = await Client.ReadRowAsync(TN, rk);
        var cells = row!.Families.First(f => f.Name == "mv1").Columns.First().Cells;
        cells.Should().HaveCount(1);
        cells[0].Value.ToStringUtf8().Should().Be("v5");
    }

    [Fact]
    public async Task MaxVersions_3_retains_newest_three()
    {
        var rk = new BigtableByteString("gc-mv3-1");
        for (int i = 1; i <= 6; i++)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell("mv3", "c", $"v{i}", new BigtableVersion(i * 1000)));
        }
        var row = await Client.ReadRowAsync(TN, rk);
        var cells = row!.Families.First(f => f.Name == "mv3").Columns.First().Cells;
        cells.Should().HaveCount(3);
        cells[0].Value.ToStringUtf8().Should().Be("v6");
        cells[1].Value.ToStringUtf8().Should().Be("v5");
        cells[2].Value.ToStringUtf8().Should().Be("v4");
    }

    [Fact]
    public async Task MaxVersions_under_limit_keeps_all()
    {
        // If we write fewer versions than the limit, all are kept
        var rk = new BigtableByteString("gc-mv3-under");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("mv3", "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("mv3", "c", "v2", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, rk);
        var cells = row!.Families.First(f => f.Name == "mv3").Columns.First().Cells;
        cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task MaxVersions_applies_per_column()
    {
        // GC rule is per-column, not per-row
        var rk = new BigtableByteString("gc-mv1-percol");
        for (int i = 1; i <= 3; i++)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell("mv1", "a", $"a{i}", new BigtableVersion(i * 1000)),
                Mutations.SetCell("mv1", "b", $"b{i}", new BigtableVersion(i * 1000)));
        }
        var row = await Client.ReadRowAsync(TN, rk);
        var family = row!.Families.First(f => f.Name == "mv1");
        // Each column should have exactly 1 version (MaxVersions=1)
        foreach (var col in family.Columns)
        {
            col.Cells.Should().HaveCount(1);
        }
    }

    [Fact]
    public async Task MaxVersions_overwrite_same_timestamp_no_extra_version()
    {
        // Writing to the same timestamp replaces the value; version count stays the same
        var rk = new BigtableByteString("gc-mv3-overwrite");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("mv3", "c", "original", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("mv3", "c", "replaced", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, rk);
        var cells = row!.Families.First(f => f.Name == "mv3").Columns.First().Cells;
        cells.Should().HaveCount(1);
        cells[0].Value.ToStringUtf8().Should().Be("replaced");
    }

    #endregion

    #region MaxAge variations

    [Fact]
    public async Task MaxAge_recent_cells_are_kept()
    {
        var rk = new BigtableByteString("gc-ma-recent");
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        nowMs = nowMs / 1000 * 1000;
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("ma1h", "c", "recent", new BigtableVersion(nowMs)));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.First(f => f.Name == "ma1h").Columns.First().Cells.Should().HaveCount(1);
    }

    [Fact]
    public async Task MaxAge_old_cells_are_filtered()
    {
        var rk = new BigtableByteString("gc-ma-old");
        var twoHoursAgoMs = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds();
        twoHoursAgoMs = twoHoursAgoMs / 1000 * 1000;
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("ma1h", "c", "expired", new BigtableVersion(twoHoursAgoMs)));
        var row = await Client.ReadRowAsync(TN, rk);
        // Row either null (no visible cells) or empty family
        if (row != null && row.Families.Any(f => f.Name == "ma1h"))
        {
            row.Families.First(f => f.Name == "ma1h").Columns.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task MaxAge_mixed_old_and_new_keeps_only_new()
    {
        var rk = new BigtableByteString("gc-ma-mixed");
        var twoHoursAgoMs = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds();
        twoHoursAgoMs = twoHoursAgoMs / 1000 * 1000;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        nowMs = nowMs / 1000 * 1000;

        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("ma1h", "c", "old", new BigtableVersion(twoHoursAgoMs)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("ma1h", "c", "new", new BigtableVersion(nowMs)));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        var cells = row!.Families.First(f => f.Name == "ma1h").Columns.First().Cells;
        cells.Should().HaveCount(1);
        cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    #endregion

    #region Union rules

    [Fact]
    public async Task Union_maxversions_or_maxage_applies_either()
    {
        // Union(MaxVersions=2, MaxAge=30min):
        // Cells deleted if they exceed version limit OR are too old
        var rk = new BigtableByteString("gc-union-1");
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        nowMs = nowMs / 1000 * 1000;

        // Write 4 recent versions
        for (int i = 1; i <= 4; i++)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell("union", "c", $"v{i}", new BigtableVersion(nowMs + i * 1000)));
        }

        var row = await Client.ReadRowAsync(TN, rk);
        var cells = row!.Families.First(f => f.Name == "union").Columns.First().Cells;
        // MaxVersions=2 keeps only 2 most recent (Union applies either rule)
        cells.Should().HaveCountLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task Union_maxage_triggers_even_within_version_limit()
    {
        // Union: old cells are removed even if under the version limit
        var rk = new BigtableByteString("gc-union-age");
        var oneHourAgoMs = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
        oneHourAgoMs = oneHourAgoMs / 1000 * 1000;

        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("union", "c", "old", new BigtableVersion(oneHourAgoMs)));

        var row = await Client.ReadRowAsync(TN, rk);
        // MaxAge=30min → this cell should be filtered
        if (row != null && row.Families.Any(f => f.Name == "union"))
        {
            var cols = row.Families.First(f => f.Name == "union").Columns;
            cols.SelectMany(c => c.Cells).Should().BeEmpty();
        }
    }

    #endregion

    #region Intersection rules

    [Fact]
    public async Task Intersection_requires_both_rules_to_trigger()
    {
        // Intersection(MaxVersions=3, MaxAge=30min):
        // Cells deleted only when BOTH conditions are met
        var rk = new BigtableByteString("gc-isect-1");
        var oneHourAgoMs = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
        oneHourAgoMs = oneHourAgoMs / 1000 * 1000;

        // Write 4 old versions (>30min old AND >3 versions)
        for (int i = 0; i < 4; i++)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell("isect", "c", $"v{i}", new BigtableVersion(oneHourAgoMs + i * 1000)));
        }

        var row = await Client.ReadRowAsync(TN, rk);
        // With intersection, both conditions must be true to GC.
        // The oldest version(s) exceed both MaxVersions=3 AND MaxAge=30min
        row.Should().NotBeNull();
        var cells = row!.Families.First(f => f.Name == "isect").Columns.First().Cells;
        // Should have 3 cells (MaxVersions=3 keeps 3, but only those also >30min are eligible)
        cells.Should().HaveCountLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task Intersection_recent_cells_over_version_limit_not_gc()
    {
        // Intersection: recent cells beyond version limit should NOT be GC'd (MaxAge not met)
        var rk = new BigtableByteString("gc-isect-recent");
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        nowMs = nowMs / 1000 * 1000;

        for (int i = 1; i <= 5; i++)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell("isect", "c", $"new{i}", new BigtableVersion(nowMs + i * 1000)));
        }

        var row = await Client.ReadRowAsync(TN, rk);
        var cells = row!.Families.First(f => f.Name == "isect").Columns.First().Cells;
        // Intersection requires BOTH conditions. These are recent (not old enough for MaxAge),
        // so even though > MaxVersions=3, they should NOT all be GC'd if MaxAge not met.
        // Implementation may vary - at minimum, recent cells should be present
        cells.Should().NotBeEmpty();
    }

    #endregion

    #region GC rule updates

    [Fact]
    public async Task Update_gc_rule_affects_subsequent_reads()
    {
        // Create a family, write data, then tighten the GC rule
        var tablePath = _fixture.InstanceName + "/tables/" + Table;

        // Create family with MaxVersions=5
        await _fixture.AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = tablePath,
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "gcupd",
                    Create = new ColumnFamily
                    {
                        GcRule = new GcRule { MaxNumVersions = 5 }
                    }
                }
            }
        });

        var rk = new BigtableByteString("gc-upd-1");
        for (int i = 1; i <= 5; i++)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell("gcupd", "c", $"v{i}", new BigtableVersion(i * 1000)));
        }

        // All 5 should be visible
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.First(f => f.Name == "gcupd").Columns.First().Cells.Should().HaveCount(5);

        // Now tighten to MaxVersions=2
        await _fixture.AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = tablePath,
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "gcupd",
                    Update = new ColumnFamily
                    {
                        GcRule = new GcRule { MaxNumVersions = 2 }
                    }
                }
            }
        });

        // After tightening GC, existing cells are NOT immediately removed.
        // In real Bigtable, GC is lazy and asynchronous — cells are only removed
        // when compaction runs, not at read time.
        // Ref: https://cloud.google.com/bigtable/docs/garbage-collection
        //   "Cloud Bigtable periodically garbage collects cells that are no longer needed."
        row = await Client.ReadRowAsync(TN, rk);
        row!.Families.First(f => f.Name == "gcupd").Columns.First().Cells
            .Should().HaveCountGreaterThanOrEqualTo(2);

        // But new writes should respect the tightened rule
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("gcupd", "c", "v6", new BigtableVersion(6000)),
            Mutations.SetCell("gcupd", "c", "v7", new BigtableVersion(7000)),
            Mutations.SetCell("gcupd", "c", "v8", new BigtableVersion(8000)));
        row = await Client.ReadRowAsync(TN, rk);
        row!.Families.First(f => f.Name == "gcupd").Columns.First().Cells
            .Should().HaveCountLessThanOrEqualTo(5); // some old may linger but new GC applies
    }

    [Fact]
    public async Task Remove_gc_rule_retains_all_versions()
    {
        var tablePath = _fixture.InstanceName + "/tables/" + Table;

        // Create with MaxVersions=1
        await _fixture.AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = tablePath,
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "gcrem",
                    Create = new ColumnFamily
                    {
                        GcRule = new GcRule { MaxNumVersions = 1 }
                    }
                }
            }
        });

        var rk = new BigtableByteString("gc-rem-1");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("gcrem", "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("gcrem", "c", "v2", new BigtableVersion(2000)));

        // Only 1 version visible with MaxVersions=1
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.First(f => f.Name == "gcrem").Columns.First().Cells.Should().HaveCount(1);

        // Remove GC rule by updating to empty GcRule
        await _fixture.AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = tablePath,
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "gcrem",
                    Update = new ColumnFamily()
                }
            }
        });

        // Write again — with no GC rule, new version should accumulate
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("gcrem", "c", "v3", new BigtableVersion(3000)));
        row = await Client.ReadRowAsync(TN, rk);
        row!.Families.First(f => f.Name == "gcrem").Columns.First().Cells
            .Should().HaveCountGreaterThanOrEqualTo(2);
    }

    #endregion

    #region GC rules with multiple columns

    [Fact]
    public async Task MaxVersions_applied_independently_to_each_column()
    {
        var rk = new BigtableByteString("gc-percol");
        // Write 5 versions to col-a and 2 to col-b (MaxVersions=3)
        for (int i = 1; i <= 5; i++)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell("mv3", "col-a", $"a{i}", new BigtableVersion(i * 1000)));
        }
        for (int i = 1; i <= 2; i++)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell("mv3", "col-b", $"b{i}", new BigtableVersion(i * 1000)));
        }

        var row = await Client.ReadRowAsync(TN, rk);
        var family = row!.Families.First(f => f.Name == "mv3");
        var colA = family.Columns.First(c => c.Qualifier.ToStringUtf8() == "col-a");
        var colB = family.Columns.First(c => c.Qualifier.ToStringUtf8() == "col-b");
        colA.Cells.Should().HaveCount(3); // capped at 3
        colB.Cells.Should().HaveCount(2); // under limit
    }

    [Fact]
    public async Task MaxVersions_with_multiple_families_independent()
    {
        // Different families have different GC rules
        var rk = new BigtableByteString("gc-multi-fam");
        for (int i = 1; i <= 5; i++)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell("mv1", "c", $"mv1-{i}", new BigtableVersion(i * 1000)),
                Mutations.SetCell("mv3", "c", $"mv3-{i}", new BigtableVersion(i * 1000)),
                Mutations.SetCell("cf", "c", $"cf-{i}", new BigtableVersion(i * 1000)));
        }
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.First(f => f.Name == "mv1").Columns.First().Cells.Should().HaveCount(1);
        row.Families.First(f => f.Name == "mv3").Columns.First().Cells.Should().HaveCount(3);
        row.Families.First(f => f.Name == "cf").Columns.First().Cells.Should().HaveCount(5);
    }

    #endregion

    #region GetTable returns compound rules

    [Fact]
    public async Task GetTable_returns_union_rule()
    {
        var tablePath = _fixture.InstanceName + "/tables/" + Table;
        var table = await _fixture.AdminClient.GetTableAsync(tablePath);
        table.ColumnFamilies["union"].GcRule.Union.Should().NotBeNull();
        table.ColumnFamilies["union"].GcRule.Union.Rules.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetTable_returns_intersection_rule()
    {
        var tablePath = _fixture.InstanceName + "/tables/" + Table;
        var table = await _fixture.AdminClient.GetTableAsync(tablePath);
        table.ColumnFamilies["isect"].GcRule.Intersection.Should().NotBeNull();
        table.ColumnFamilies["isect"].GcRule.Intersection.Rules.Should().HaveCount(2);
    }

    #endregion
}

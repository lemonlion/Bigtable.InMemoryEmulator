using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for admin-level validation: family name patterns, duplicate tables, nonexistent operations.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#columnfamily
///   "Family names: must match [_a-zA-Z0-9][-_.a-zA-Z0-9]*"
/// Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#google.bigtable.admin.v2.BigtableTableAdmin
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.GcpOnly)]
public sealed class AdminValidationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;

    public AdminValidationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync("admin-val-init", new[] { "cf" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableTableAdminClient AdminClient => _fixture.AdminClient;
    private BigtableClient Client => _fixture.Client;
    private string TablePath(string id) => _fixture.InstanceName + "/tables/" + id;

    #region Family name validation

    [Theory]
    [InlineData("cf")]
    [InlineData("_private")]
    [InlineData("Abc123")]
    [InlineData("a-b-c")]
    [InlineData("a.b.c")]
    [InlineData("a_b_c")]
    [InlineData("CF1")]
    public async Task Valid_family_names_accepted(string familyName)
    {
        var tableId = $"admin-vfn-{familyName.Replace(".", "d").Replace("_", "u")}";
        var request = new CreateTableRequest
        {
            Parent = _fixture.InstanceName,
            TableId = tableId,
            Table = new Google.Cloud.Bigtable.Admin.V2.Table()
        };
        request.Table.ColumnFamilies.Add(familyName, new ColumnFamily());
        await AdminClient.CreateTableAsync(request);

        var table = await AdminClient.GetTableAsync(_fixture.GetTableName(tableId));
        table.ColumnFamilies.Should().ContainKey(familyName);
    }

    [Theory]
    [InlineData("")] // Empty
    [InlineData("-starts-with-hyphen")] // Must start with [_a-zA-Z0-9]
    [InlineData(".starts-with-dot")]
    public async Task Invalid_family_names_rejected(string familyName)
    {
        var tableId = $"admin-ifn-{Guid.NewGuid():N}";
        var request = new CreateTableRequest
        {
            Parent = _fixture.InstanceName,
            TableId = tableId,
            Table = new Google.Cloud.Bigtable.Admin.V2.Table()
        };
        request.Table.ColumnFamilies.Add(familyName, new ColumnFamily());
        var act = () => AdminClient.CreateTableAsync(request);
        await act.Should().ThrowAsync<RpcException>();
    }

    #endregion

    #region Duplicate table

    [Fact]
    public async Task Create_duplicate_table_throws_AlreadyExists()
    {
        await _fixture.CreateTableAsync("admin-dup", new[] { "cf" });
        var act = () => _fixture.CreateTableAsync("admin-dup", new[] { "cf" });
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.AlreadyExists);
    }

    #endregion

    #region Drop nonexistent family

    [Fact]
    public async Task Drop_nonexistent_family_throws()
    {
        await _fixture.CreateTableAsync("admin-dropnf", new[] { "cf" });
        var act = () => AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = TablePath("admin-dropnf"),
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "nonexistent",
                    Drop = true
                }
            }
        });
        await act.Should().ThrowAsync<RpcException>();
    }

    #endregion

    #region Create family that already exists

    [Fact]
    public async Task Create_duplicate_family_throws()
    {
        await _fixture.CreateTableAsync("admin-dupfam", new[] { "cf" });
        var act = () => AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = TablePath("admin-dupfam"),
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "cf",
                    Create = new ColumnFamily()
                }
            }
        });
        await act.Should().ThrowAsync<RpcException>();
    }

    #endregion

    #region Write to nonexistent family

    [Fact]
    public async Task Write_to_nonexistent_family_throws()
    {
        await _fixture.CreateTableAsync("admin-wnf", new[] { "cf" });
        var tn = _fixture.GetTableName("admin-wnf");
        var act = () => Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("nosuchfamily", "c", "v", new BigtableVersion(1000)));
        await act.Should().ThrowAsync<RpcException>();
    }

    #endregion

    #region Table with many families

    [Fact]
    public async Task Create_table_with_many_families()
    {
        var request = new CreateTableRequest
        {
            Parent = _fixture.InstanceName,
            TableId = "admin-many-fam",
            Table = new Google.Cloud.Bigtable.Admin.V2.Table()
        };
        for (int i = 0; i < 50; i++)
            request.Table.ColumnFamilies.Add($"cf{i}", new ColumnFamily());
        await AdminClient.CreateTableAsync(request);

        var table = await AdminClient.GetTableAsync(_fixture.GetTableName("admin-many-fam"));
        table.ColumnFamilies.Should().HaveCount(50);
    }

    #endregion

    #region Modify on nonexistent table

    [Fact]
    public async Task Modify_families_on_nonexistent_table_throws()
    {
        var act = () => AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = TablePath("admin-no-such-table"),
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "cf",
                    Create = new ColumnFamily()
                }
            }
        });
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    #endregion

    #region GC rule modifications

    [Fact]
    public async Task Set_max_versions_gc_rule()
    {
        await _fixture.CreateTableAsync("admin-gc-ver", new[] { "cf" });
        await AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = TablePath("admin-gc-ver"),
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "cf",
                    Update = new ColumnFamily
                    {
                        GcRule = new GcRule { MaxNumVersions = 3 }
                    }
                }
            }
        });

        var table = await AdminClient.GetTableAsync(_fixture.GetTableName("admin-gc-ver"));
        table.ColumnFamilies["cf"].GcRule.MaxNumVersions.Should().Be(3);
    }

    [Fact]
    public async Task Set_max_age_gc_rule()
    {
        await _fixture.CreateTableAsync("admin-gc-age", new[] { "cf" });
        await AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = TablePath("admin-gc-age"),
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "cf",
                    Update = new ColumnFamily
                    {
                        GcRule = new GcRule
                        {
                            MaxAge = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromDays(7))
                        }
                    }
                }
            }
        });

        var table = await AdminClient.GetTableAsync(_fixture.GetTableName("admin-gc-age"));
        table.ColumnFamilies["cf"].GcRule.MaxAge.Should().NotBeNull();
    }

    [Fact]
    public async Task Set_union_gc_rule()
    {
        await _fixture.CreateTableAsync("admin-gc-union", new[] { "cf" });
        await AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = TablePath("admin-gc-union"),
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "cf",
                    Update = new ColumnFamily
                    {
                        GcRule = new GcRule
                        {
                            Union = new GcRule.Types.Union
                            {
                                Rules =
                                {
                                    new GcRule { MaxNumVersions = 2 },
                                    new GcRule { MaxAge = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromDays(1)) }
                                }
                            }
                        }
                    }
                }
            }
        });

        var table = await AdminClient.GetTableAsync(_fixture.GetTableName("admin-gc-union"));
        table.ColumnFamilies["cf"].GcRule.Union.Rules.Should().HaveCount(2);
    }

    [Fact]
    public async Task Set_intersection_gc_rule()
    {
        await _fixture.CreateTableAsync("admin-gc-inter", new[] { "cf" });
        await AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = TablePath("admin-gc-inter"),
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "cf",
                    Update = new ColumnFamily
                    {
                        GcRule = new GcRule
                        {
                            Intersection = new GcRule.Types.Intersection
                            {
                                Rules =
                                {
                                    new GcRule { MaxNumVersions = 5 },
                                    new GcRule { MaxAge = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromDays(30)) }
                                }
                            }
                        }
                    }
                }
            }
        });

        var table = await AdminClient.GetTableAsync(_fixture.GetTableName("admin-gc-inter"));
        table.ColumnFamilies["cf"].GcRule.Intersection.Rules.Should().HaveCount(2);
    }

    [Fact]
    public async Task Replace_gc_rule()
    {
        await _fixture.CreateTableAsync("admin-gc-replace", new[] { "cf" });
        // Set MaxVersions=2 first
        await AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = TablePath("admin-gc-replace"),
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "cf",
                    Update = new ColumnFamily { GcRule = new GcRule { MaxNumVersions = 2 } }
                }
            }
        });
        // Replace with MaxVersions=5
        await AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = TablePath("admin-gc-replace"),
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "cf",
                    Update = new ColumnFamily { GcRule = new GcRule { MaxNumVersions = 5 } }
                }
            }
        });

        var table = await AdminClient.GetTableAsync(_fixture.GetTableName("admin-gc-replace"));
        table.ColumnFamilies["cf"].GcRule.MaxNumVersions.Should().Be(5);
    }

    #endregion

    #region Update nonexistent family

    [Fact]
    public async Task Update_nonexistent_family_throws()
    {
        await _fixture.CreateTableAsync("admin-upd-ne", new[] { "cf" });
        var act = () => AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = TablePath("admin-upd-ne"),
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "nosuchfamily",
                    Update = new ColumnFamily { GcRule = new GcRule { MaxNumVersions = 1 } }
                }
            }
        });
        await act.Should().ThrowAsync<RpcException>();
    }

    #endregion
}

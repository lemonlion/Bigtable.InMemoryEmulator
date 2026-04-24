using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Advanced Admin API integration tests — column family lifecycle, GC rule modifications,
/// table renaming, and error conditions.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class AdminApiAdvancedIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "admin-adv-tests";

    public AdminApiAdvancedIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { "cf1", "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableTableAdminClient Admin => _fixture.AdminClient;
    private string TablePath => _fixture.InstanceName + "/tables/" + Table;

    [Fact]
    public async Task ModifyColumnFamilies_add_with_gc_rule()
    {
        // Ref: ModifyColumnFamilies — create with GC rule
        await Admin.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = TablePath,
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "gcf1",
                    Create = new Google.Cloud.Bigtable.Admin.V2.ColumnFamily
                    {
                        GcRule = new Google.Cloud.Bigtable.Admin.V2.GcRule { MaxNumVersions = 3 }
                    }
                }
            }
        });
        var table = await Admin.GetTableAsync(new GetTableRequest { Name = TablePath });
        table.ColumnFamilies.Should().ContainKey("gcf1");
        table.ColumnFamilies["gcf1"].GcRule.MaxNumVersions.Should().Be(3);
    }

    [Fact]
    public async Task ModifyColumnFamilies_update_gc_rule()
    {
        // Add a family, then update its GC rule
        await Admin.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = TablePath,
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "gcupd",
                    Create = new Google.Cloud.Bigtable.Admin.V2.ColumnFamily
                    {
                        GcRule = new Google.Cloud.Bigtable.Admin.V2.GcRule { MaxNumVersions = 5 }
                    }
                }
            }
        });
        // Update
        await Admin.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = TablePath,
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "gcupd",
                    Update = new Google.Cloud.Bigtable.Admin.V2.ColumnFamily
                    {
                        GcRule = new Google.Cloud.Bigtable.Admin.V2.GcRule { MaxNumVersions = 2 }
                    }
                }
            }
        });
        var table = await Admin.GetTableAsync(new GetTableRequest { Name = TablePath });
        table.ColumnFamilies["gcupd"].GcRule.MaxNumVersions.Should().Be(2);
    }

    [Fact]
    public async Task ModifyColumnFamilies_drop_family()
    {
        await Admin.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = TablePath,
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "todrop",
                    Create = new Google.Cloud.Bigtable.Admin.V2.ColumnFamily()
                }
            }
        });
        await Admin.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = TablePath,
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "todrop",
                    Drop = true
                }
            }
        });
        var table = await Admin.GetTableAsync(new GetTableRequest { Name = TablePath });
        table.ColumnFamilies.Should().NotContainKey("todrop");
    }

    [Fact]
    public async Task ModifyColumnFamilies_multiple_modifications_in_single_call()
    {
        await Admin.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = TablePath,
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "new1",
                    Create = new Google.Cloud.Bigtable.Admin.V2.ColumnFamily()
                },
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "new2",
                    Create = new Google.Cloud.Bigtable.Admin.V2.ColumnFamily
                    {
                        GcRule = new Google.Cloud.Bigtable.Admin.V2.GcRule { MaxNumVersions = 10 }
                    }
                }
            }
        });
        var table = await Admin.GetTableAsync(new GetTableRequest { Name = TablePath });
        table.ColumnFamilies.Should().ContainKey("new1");
        table.ColumnFamilies.Should().ContainKey("new2");
    }

    [Fact]
    public async Task GetTable_returns_all_families()
    {
        var table = await Admin.GetTableAsync(new GetTableRequest { Name = TablePath });
        table.ColumnFamilies.Should().ContainKey("cf1");
        table.ColumnFamilies.Should().ContainKey("cf2");
    }

    [Fact]
    public async Task GetTable_returns_fully_qualified_name()
    {
        var table = await Admin.GetTableAsync(new GetTableRequest { Name = TablePath });
        table.Name.Should().Contain("/tables/");
    }

    [Fact]
    public async Task CreateTable_with_multiple_families()
    {
        var tableId = "multi-cf-create";
        await Admin.CreateTableAsync(new CreateTableRequest
        {
            Parent = _fixture.InstanceName,
            TableId = tableId,
            Table = new Google.Cloud.Bigtable.Admin.V2.Table
            {
                ColumnFamilies =
                {
                    { "f1", new Google.Cloud.Bigtable.Admin.V2.ColumnFamily() },
                    { "f2", new Google.Cloud.Bigtable.Admin.V2.ColumnFamily() },
                    { "f3", new Google.Cloud.Bigtable.Admin.V2.ColumnFamily() },
                }
            }
        });
        var table = await Admin.GetTableAsync(new GetTableRequest
        {
            Name = _fixture.InstanceName + "/tables/" + tableId
        });
        table.ColumnFamilies.Should().HaveCount(3);
    }

    [Fact]
    public async Task CreateTable_with_gc_rules()
    {
        var tableId = "gc-create";
        await Admin.CreateTableAsync(new CreateTableRequest
        {
            Parent = _fixture.InstanceName,
            TableId = tableId,
            Table = new Google.Cloud.Bigtable.Admin.V2.Table
            {
                ColumnFamilies =
                {
                    { "versioned", new Google.Cloud.Bigtable.Admin.V2.ColumnFamily
                    {
                        GcRule = new Google.Cloud.Bigtable.Admin.V2.GcRule { MaxNumVersions = 3 }
                    }},
                    { "timed", new Google.Cloud.Bigtable.Admin.V2.ColumnFamily
                    {
                        GcRule = new Google.Cloud.Bigtable.Admin.V2.GcRule
                        {
                            MaxAge = Duration.FromTimeSpan(TimeSpan.FromDays(7))
                        }
                    }},
                }
            }
        });
        var table = await Admin.GetTableAsync(new GetTableRequest
        {
            Name = _fixture.InstanceName + "/tables/" + tableId
        });
        table.ColumnFamilies["versioned"].GcRule.MaxNumVersions.Should().Be(3);
        table.ColumnFamilies["timed"].GcRule.MaxAge.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteTable_makes_it_inaccessible()
    {
        var tableId = "to-delete";
        await Admin.CreateTableAsync(new CreateTableRequest
        {
            Parent = _fixture.InstanceName,
            TableId = tableId,
            Table = new Google.Cloud.Bigtable.Admin.V2.Table
            {
                ColumnFamilies = { { "cf", new Google.Cloud.Bigtable.Admin.V2.ColumnFamily() } }
            }
        });
        await Admin.DeleteTableAsync(new DeleteTableRequest
        {
            Name = _fixture.InstanceName + "/tables/" + tableId
        });
        var act = () => Admin.GetTableAsync(new GetTableRequest
        {
            Name = _fixture.InstanceName + "/tables/" + tableId
        });
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task ModifyColumnFamilies_update_nonexistent_family_throws()
    {
        var act = () => Admin.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = TablePath,
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "nonexistent",
                    Update = new Google.Cloud.Bigtable.Admin.V2.ColumnFamily
                    {
                        GcRule = new Google.Cloud.Bigtable.Admin.V2.GcRule { MaxNumVersions = 1 }
                    }
                }
            }
        });
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }
}

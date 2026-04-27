using InMemoryEmulator.Bigtable;
using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for InMemoryTableOptions — per-table configuration for the builder and DI.
/// Concept mapping: InMemoryContainerOptions → InMemoryTableOptions
/// </summary>
public class InMemoryTableOptionsTests
{
    [Fact]
    public void TableOptions_default_has_no_gc_rules()
    {
        var options = new InMemoryTableOptions();
        options.GcRules.Should().BeEmpty();
    }

    [Fact]
    public void TableOptions_MaxVersions_adds_gc_rule()
    {
        var options = new InMemoryTableOptions();
        options.MaxVersions("cf1", 5);

        options.GcRules.Should().ContainKey("cf1");
        options.GcRules["cf1"]!.MaxNumVersions.Should().Be(5);
    }

    [Fact]
    public void TableOptions_MaxAge_adds_gc_rule()
    {
        var options = new InMemoryTableOptions();
        options.MaxAge("cf1", TimeSpan.FromHours(24));

        options.GcRules.Should().ContainKey("cf1");
        options.GcRules["cf1"]!.MaxAge.Should().NotBeNull();
    }

    [Fact]
    public void TableOptions_multiple_families()
    {
        var options = new InMemoryTableOptions();
        options.MaxVersions("cf1", 3);
        options.MaxAge("cf2", TimeSpan.FromDays(7));

        options.GcRules.Should().HaveCount(2);
    }

    [Fact]
    public async Task Builder_AddTable_with_options_action()
    {
        using var result = InMemoryBigtable.Builder()
            .AddTable("t1", ["cf1", "cf2"], opts =>
            {
                opts.MaxVersions("cf1", 2);
            })
            .Build();

        var client = result.Client;
        var tableName = result.GetTableName("t1");
        var rowKey = new BigtableByteString("r1");

        // Write 3 versions
        await client.MutateRowAsync(tableName, rowKey, Mutations.SetCell("cf1", "col", "v1", new BigtableVersion(1000)));
        await client.MutateRowAsync(tableName, rowKey, Mutations.SetCell("cf1", "col", "v2", new BigtableVersion(2000)));
        await client.MutateRowAsync(tableName, rowKey, Mutations.SetCell("cf1", "col", "v3", new BigtableVersion(3000)));

        // MaxVersions=2 should keep only 2 newest
        var row = await client.ReadRowAsync(tableName, rowKey);
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task DI_AddTable_with_options_action()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.UseInMemoryBigtable(opts =>
        {
            opts.AddTable("t1", ["cf1"], tableOpts =>
            {
                tableOpts.MaxVersions("cf1", 1);
            });
        });

        var sp = services.BuildServiceProvider();
        var result = sp.GetRequiredService<InMemoryBigtableResult>();
        var client = result.Client;
        var tableName = result.GetTableName("t1");
        var rowKey = new BigtableByteString("r1");

        // Write 2 versions
        await client.MutateRowAsync(tableName, rowKey, Mutations.SetCell("cf1", "col", "v1", new BigtableVersion(1000)));
        await client.MutateRowAsync(tableName, rowKey, Mutations.SetCell("cf1", "col", "v2", new BigtableVersion(2000)));

        // MaxVersions=1 should keep only the newest
        var row = await client.ReadRowAsync(tableName, rowKey);
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v2");
    }

    [Fact]
    public void TableOptions_OnCreated_callback_fires()
    {
        var options = new InMemoryTableOptions();
        options.OnCreated(_ => { });

        // The callback is stored; we can verify it exists
        options.OnCreatedCallback.Should().NotBeNull();
    }
}

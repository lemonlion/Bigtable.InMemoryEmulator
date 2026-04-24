using Bigtable.InMemoryEmulator;
using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for the public user-facing API: InMemoryBigtable.Create(), Builder(), and InMemoryBigtableResult.
/// </summary>
public class InMemoryBigtableApiTests
{
    #region InMemoryBigtable.Create()

    [Fact]
    public async Task Create_returns_functional_client()
    {
        using var result = InMemoryBigtable.Create("my-table", ["cf1", "cf2"]);
        var client = result.Client;
        var tableName = result.GetTableName("my-table");

        await client.MutateRowAsync(tableName, new BigtableByteString("row1"),
            Mutations.SetCell("cf1", "col", "value", new BigtableVersion(1000)));

        var row = await client.ReadRowAsync(tableName, new BigtableByteString("row1"));
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("value");
    }

    [Fact]
    public void Create_returns_correct_table_name()
    {
        using var result = InMemoryBigtable.Create("users", ["cf1"]);
        var tableName = result.GetTableName("users");

        tableName.ProjectId.Should().Be("test-project");
        tableName.InstanceId.Should().Be("test-instance");
        tableName.TableId.Should().Be("users");
    }

    #endregion

    #region InMemoryBigtable.Builder()

    [Fact]
    public async Task Builder_creates_multiple_tables()
    {
        using var result = InMemoryBigtable.Builder()
            .AddTable("users", ["profile", "activity"])
            .AddTable("events", ["data"])
            .Build();

        var client = result.Client;

        // Write to both tables
        await client.MutateRowAsync(result.GetTableName("users"), new BigtableByteString("u1"),
            Mutations.SetCell("profile", "name", "Alice", new BigtableVersion(1000)));

        await client.MutateRowAsync(result.GetTableName("events"), new BigtableByteString("e1"),
            Mutations.SetCell("data", "type", "click", new BigtableVersion(1000)));

        // Read from both
        var user = await client.ReadRowAsync(result.GetTableName("users"), new BigtableByteString("u1"));
        var evt = await client.ReadRowAsync(result.GetTableName("events"), new BigtableByteString("e1"));

        user.Should().NotBeNull();
        evt.Should().NotBeNull();
        user!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("Alice");
        evt!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("click");
    }

    [Fact]
    public async Task Builder_with_custom_project_and_instance()
    {
        using var result = InMemoryBigtable.Builder()
            .ProjectId("my-proj")
            .InstanceId("my-inst")
            .AddTable("t1", ["cf"])
            .Build();

        var tableName = result.GetTableName("t1");
        tableName.ProjectId.Should().Be("my-proj");
        tableName.InstanceId.Should().Be("my-inst");

        // Verify client works with this table name
        await result.Client.MutateRowAsync(tableName, new BigtableByteString("r1"),
            Mutations.SetCell("cf", "c", "v", new BigtableVersion(1000)));

        var row = await result.Client.ReadRowAsync(tableName, new BigtableByteString("r1"));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Builder_with_gc_rules()
    {
        using var result = InMemoryBigtable.Builder()
            .AddTable("t1", ["cf"], opts => opts.MaxVersions("cf", 2))
            .Build();

        var client = result.Client;
        var tableName = result.GetTableName("t1");
        var rowKey = new BigtableByteString("row1");

        // Write 3 versions
        await client.MutateRowAsync(tableName, rowKey, Mutations.SetCell("cf", "col", "v1", new BigtableVersion(1000)));
        await client.MutateRowAsync(tableName, rowKey, Mutations.SetCell("cf", "col", "v2", new BigtableVersion(2000)));
        await client.MutateRowAsync(tableName, rowKey, Mutations.SetCell("cf", "col", "v3", new BigtableVersion(3000)));

        // MaxVersions=2 should keep only 2 newest
        var row = await client.ReadRowAsync(tableName, rowKey);
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v3");
        row.Families[0].Columns[0].Cells[1].Value.ToStringUtf8().Should().Be("v2");
    }

    #endregion

    #region InMemoryBigtableResult helpers

    [Fact]
    public async Task ClearRows_removes_all_data()
    {
        using var result = InMemoryBigtable.Create("t1", ["cf"]);
        var tableName = result.GetTableName("t1");

        await result.Client.MutateRowAsync(tableName, new BigtableByteString("r1"),
            Mutations.SetCell("cf", "c", "v", new BigtableVersion(1000)));
        await result.Client.MutateRowAsync(tableName, new BigtableByteString("r2"),
            Mutations.SetCell("cf", "c", "v", new BigtableVersion(1000)));

        result.RowCount("t1").Should().Be(2);

        result.ClearRows("t1");

        result.RowCount("t1").Should().Be(0);
    }

    [Fact]
    public async Task RowCount_returns_correct_count()
    {
        using var result = InMemoryBigtable.Create("t1", ["cf"]);
        var tableName = result.GetTableName("t1");

        result.RowCount("t1").Should().Be(0);

        await result.Client.MutateRowAsync(tableName, new BigtableByteString("r1"),
            Mutations.SetCell("cf", "c", "v", new BigtableVersion(1000)));

        result.RowCount("t1").Should().Be(1);
    }

    [Fact]
    public void Dispose_cleans_up_resources()
    {
        var result = InMemoryBigtable.Create("t1", ["cf"]);
        result.Dispose();

        // Should not throw — just verifying cleanup doesn't crash
    }

    #endregion

    #region SetupTable — GcRules property

    [Fact]
    public void SetupTable_GcRules_returns_configured_gc_rules()
    {
        using var result = InMemoryBigtable.Builder()
            .AddTable("t1", ["cf1"], opts => opts.MaxVersions("cf1", 3))
            .Build();

        var setup = result.SetupTable("t1");

        setup.GcRules.Should().ContainKey("cf1");
        setup.GcRules["cf1"]!.MaxNumVersions.Should().Be(3);
    }

    [Fact]
    public void SetupTable_GcRules_returns_null_for_family_without_gc_rule()
    {
        using var result = InMemoryBigtable.Create("t1", ["cf1"]);

        var setup = result.SetupTable("t1");

        setup.GcRules.Should().ContainKey("cf1");
        setup.GcRules["cf1"].Should().BeNull();
    }

    [Fact]
    public void SetupTable_GcRules_includes_multiple_families()
    {
        using var result = InMemoryBigtable.Builder()
            .AddTable("t1", ["cf1", "cf2"], opts =>
            {
                opts.MaxVersions("cf1", 5);
                opts.MaxAge("cf2", TimeSpan.FromHours(2));
            })
            .Build();

        var setup = result.SetupTable("t1");

        setup.GcRules.Should().HaveCount(2);
        setup.GcRules["cf1"]!.MaxNumVersions.Should().Be(5);
        setup.GcRules["cf2"]!.MaxAge.Should().NotBeNull();
    }

    #endregion

    #region SetupTable — StateFilePath property

    [Fact]
    public void SetupTable_StateFilePath_returns_null_when_not_configured()
    {
        using var result = InMemoryBigtable.Create("t1", ["cf1"]);
        var setup = result.SetupTable("t1");

        setup.StateFilePath.Should().BeNull();
    }

    [Fact]
    public void SetupTable_StateFilePath_returns_path_when_configured()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bt-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var result = InMemoryBigtable.Builder()
                .AddTable("t1", ["cf1"])
                .StatePersistenceDirectory(dir)
                .Build();

            var setup = result.SetupTable("t1");

            setup.StateFilePath.Should().NotBeNull();
            setup.StateFilePath.Should().Be(Path.Combine(dir, "bigtable-state.json"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    #endregion

    #region ReadRows through public API

    [Fact]
    public async Task ReadRows_via_public_api_returns_ordered_rows()
    {
        using var result = InMemoryBigtable.Create("t1", ["cf"]);
        var tableName = result.GetTableName("t1");
        var client = result.Client;

        // Insert rows out of order
        await client.MutateRowAsync(tableName, new BigtableByteString("c"), Mutations.SetCell("cf", "x", "3", new BigtableVersion(1000)));
        await client.MutateRowAsync(tableName, new BigtableByteString("a"), Mutations.SetCell("cf", "x", "1", new BigtableVersion(1000)));
        await client.MutateRowAsync(tableName, new BigtableByteString("b"), Mutations.SetCell("cf", "x", "2", new BigtableVersion(1000)));

        var stream = client.ReadRows(tableName);
        var rows = new List<Row>();
        var enumerator = stream.GetAsyncEnumerator(default);
        while (await enumerator.MoveNextAsync())
        {
            rows.Add(enumerator.Current);
        }

        rows.Should().HaveCount(3);
        rows[0].Key.ToStringUtf8().Should().Be("a");
        rows[1].Key.ToStringUtf8().Should().Be("b");
        rows[2].Key.ToStringUtf8().Should().Be("c");
    }

    #endregion
}

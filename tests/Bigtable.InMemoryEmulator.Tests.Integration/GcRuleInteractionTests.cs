using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for GC rule interactions — MaxVersions, MaxAge, Union, Intersection.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#gcRule
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.GcpOnly)]
public sealed class GcRuleInteractionTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF_V1 = "v1";
    private const string CF_V3 = "v3";
    private const string CF_V5 = "v5";

    public GcRuleInteractionTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync()
    {
        // Must call CreateTableAsync first to initialize the fixture (AdminClient, InstanceName, etc.)
        await _fixture.CreateTableAsync("gc-seed", new[] { "seed" });

        // Create table with different GC rules per family
        var request = new Google.Cloud.Bigtable.Admin.V2.CreateTableRequest
        {
            Parent = _fixture.InstanceName,
            TableId = "gc-interact",
            Table = new Google.Cloud.Bigtable.Admin.V2.Table()
        };
        request.Table.ColumnFamilies.Add(CF_V1, new Google.Cloud.Bigtable.Admin.V2.ColumnFamily
        {
            GcRule = new Google.Cloud.Bigtable.Admin.V2.GcRule { MaxNumVersions = 1 }
        });
        request.Table.ColumnFamilies.Add(CF_V3, new Google.Cloud.Bigtable.Admin.V2.ColumnFamily
        {
            GcRule = new Google.Cloud.Bigtable.Admin.V2.GcRule { MaxNumVersions = 3 }
        });
        request.Table.ColumnFamilies.Add(CF_V5, new Google.Cloud.Bigtable.Admin.V2.ColumnFamily
        {
            GcRule = new Google.Cloud.Bigtable.Admin.V2.GcRule { MaxNumVersions = 5 }
        });
        await _fixture.AdminClient.CreateTableAsync(request);
    }
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("gc-interact");

    private async Task<List<Cell>> ReadCells(string rowKey, string family, string col)
    {
        var cells = new List<Cell>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys(rowKey)))
            foreach (var fam in row.Families)
                if (fam.Name == family)
                    foreach (var column in fam.Columns)
                        if (column.Qualifier.ToStringUtf8() == col)
                            cells.AddRange(column.Cells);
        return cells;
    }

    #region MaxVersions=1

    [Fact]
    public async Task V1_keeps_only_latest_version()
    {
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "gc-v1-a",
                Mutations.SetCell(CF_V1, "c", $"v{i}", new BigtableVersion(i * 1000)));
        var cells = await ReadCells("gc-v1-a", CF_V1, "c");
        cells.Should().ContainSingle();
        cells[0].Value.ToStringUtf8().Should().Be("v5");
    }

    [Fact]
    public async Task V1_overwrite_replaces_value()
    {
        await Client.MutateRowAsync(TN, "gc-v1-b",
            Mutations.SetCell(CF_V1, "c", "first", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "gc-v1-b",
            Mutations.SetCell(CF_V1, "c", "second", new BigtableVersion(2000)));
        var cells = await ReadCells("gc-v1-b", CF_V1, "c");
        cells.Should().ContainSingle();
        cells[0].Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task V1_multiple_columns_each_keep_one()
    {
        for (int col = 0; col < 3; col++)
            for (int ver = 1; ver <= 3; ver++)
                await Client.MutateRowAsync(TN, "gc-v1-mc",
                    Mutations.SetCell(CF_V1, $"c{col}", $"v{ver}", new BigtableVersion(ver * 1000)));

        for (int col = 0; col < 3; col++)
        {
            var cells = await ReadCells("gc-v1-mc", CF_V1, $"c{col}");
            cells.Should().ContainSingle();
            cells[0].Value.ToStringUtf8().Should().Be("v3");
        }
    }

    #endregion

    #region MaxVersions=3

    [Fact]
    public async Task V3_keeps_latest_3_of_5()
    {
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "gc-v3-a",
                Mutations.SetCell(CF_V3, "c", $"v{i}", new BigtableVersion(i * 1000)));
        var cells = await ReadCells("gc-v3-a", CF_V3, "c");
        cells.Should().HaveCount(3);
        cells.Select(c => c.Value.ToStringUtf8()).Should().BeEquivalentTo(new[] { "v5", "v4", "v3" });
    }

    [Fact]
    public async Task V3_keeps_all_when_under_limit()
    {
        for (int i = 1; i <= 2; i++)
            await Client.MutateRowAsync(TN, "gc-v3-b",
                Mutations.SetCell(CF_V3, "c", $"v{i}", new BigtableVersion(i * 1000)));
        var cells = await ReadCells("gc-v3-b", CF_V3, "c");
        cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task V3_exactly_at_limit()
    {
        for (int i = 1; i <= 3; i++)
            await Client.MutateRowAsync(TN, "gc-v3-c",
                Mutations.SetCell(CF_V3, "c", $"v{i}", new BigtableVersion(i * 1000)));
        var cells = await ReadCells("gc-v3-c", CF_V3, "c");
        cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task V3_ten_writes_keeps_3()
    {
        for (int i = 1; i <= 10; i++)
            await Client.MutateRowAsync(TN, "gc-v3-d",
                Mutations.SetCell(CF_V3, "c", $"v{i}", new BigtableVersion(i * 1000)));
        var cells = await ReadCells("gc-v3-d", CF_V3, "c");
        cells.Should().HaveCount(3);
        cells[0].Value.ToStringUtf8().Should().Be("v10");
    }

    #endregion

    #region MaxVersions=5

    [Fact]
    public async Task V5_keeps_latest_5_of_8()
    {
        for (int i = 1; i <= 8; i++)
            await Client.MutateRowAsync(TN, "gc-v5-a",
                Mutations.SetCell(CF_V5, "c", $"v{i}", new BigtableVersion(i * 1000)));
        var cells = await ReadCells("gc-v5-a", CF_V5, "c");
        cells.Should().HaveCount(5);
    }

    [Fact]
    public async Task V5_under_limit_keeps_all()
    {
        for (int i = 1; i <= 3; i++)
            await Client.MutateRowAsync(TN, "gc-v5-b",
                Mutations.SetCell(CF_V5, "c", $"v{i}", new BigtableVersion(i * 1000)));
        var cells = await ReadCells("gc-v5-b", CF_V5, "c");
        cells.Should().HaveCount(3);
    }

    #endregion

    #region Cross-family with different GC

    [Fact]
    public async Task Same_data_different_families_different_retention()
    {
        // Write 5 versions to each family for the same row
        for (int i = 1; i <= 5; i++)
        {
            await Client.MutateRowAsync(TN, "gc-cross",
                Mutations.SetCell(CF_V1, "c", $"v{i}", new BigtableVersion(i * 1000)),
                Mutations.SetCell(CF_V3, "c", $"v{i}", new BigtableVersion(i * 1000)),
                Mutations.SetCell(CF_V5, "c", $"v{i}", new BigtableVersion(i * 1000)));
        }

        var v1Cells = await ReadCells("gc-cross", CF_V1, "c");
        var v3Cells = await ReadCells("gc-cross", CF_V3, "c");
        var v5Cells = await ReadCells("gc-cross", CF_V5, "c");

        v1Cells.Should().ContainSingle();
        v3Cells.Should().HaveCount(3);
        v5Cells.Should().HaveCount(5);
    }

    [Fact]
    public async Task Delete_from_one_family_preserves_others()
    {
        await Client.MutateRowAsync(TN, "gc-del-fam",
            Mutations.SetCell(CF_V1, "c", "a", new BigtableVersion(1000)),
            Mutations.SetCell(CF_V3, "c", "b", new BigtableVersion(1000)),
            Mutations.SetCell(CF_V5, "c", "c", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "gc-del-fam",
            Mutations.DeleteFromFamily(CF_V3));

        var v1 = await ReadCells("gc-del-fam", CF_V1, "c");
        var v3 = await ReadCells("gc-del-fam", CF_V3, "c");
        var v5 = await ReadCells("gc-del-fam", CF_V5, "c");

        v1.Should().ContainSingle();
        v3.Should().BeEmpty();
        v5.Should().ContainSingle();
    }

    #endregion

    #region GC after delete and rewrite

    [Fact]
    public async Task V3_delete_then_rewrite_keeps_new_versions()
    {
        // Write 5 versions
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "gc-rewrite",
                Mutations.SetCell(CF_V3, "c", $"old-{i}", new BigtableVersion(i * 1000)));

        // Delete all from column
        await Client.MutateRowAsync(TN, "gc-rewrite",
            Mutations.DeleteFromColumn(CF_V3, "c"));

        // Write 2 new versions
        for (int i = 1; i <= 2; i++)
            await Client.MutateRowAsync(TN, "gc-rewrite",
                Mutations.SetCell(CF_V3, "c", $"new-{i}", new BigtableVersion((10 + i) * 1000)));

        var cells = await ReadCells("gc-rewrite", CF_V3, "c");
        cells.Should().HaveCount(2);
        cells.All(c => c.Value.ToStringUtf8().StartsWith("new-")).Should().BeTrue();
    }

    [Fact]
    public async Task V1_delete_row_then_rewrite()
    {
        await Client.MutateRowAsync(TN, "gc-v1-rewrite",
            Mutations.SetCell(CF_V1, "c", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "gc-v1-rewrite", Mutations.DeleteFromRow());
        await Client.MutateRowAsync(TN, "gc-v1-rewrite",
            Mutations.SetCell(CF_V1, "c", "new", new BigtableVersion(2000)));

        var cells = await ReadCells("gc-v1-rewrite", CF_V1, "c");
        cells.Should().ContainSingle();
        cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    #endregion
}

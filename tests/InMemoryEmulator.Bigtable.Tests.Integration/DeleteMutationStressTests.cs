using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Exhaustive edge-case tests for delete mutations.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class DeleteMutationStressTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "delete-stress";
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public DeleteMutationStressTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task SeedRow(string key, string family = CF, int versions = 3)
    {
        var mutations = Enumerable.Range(1, versions).Select(v =>
            Mutations.SetCell(family, "c", $"v{v}", new BigtableVersion(v * 1000))
        ).ToArray();
        await Client.MutateRowAsync(TN, key, mutations);
    }

    private async Task SeedMultiColumn(string key, string[] cols, string family = CF)
    {
        var mutations = cols.Select(c =>
            Mutations.SetCell(family, c, $"val-{c}", new BigtableVersion(1000))
        ).ToArray();
        await Client.MutateRowAsync(TN, key, mutations);
    }

    private async Task<List<Row>> ReadAll(RowSet? rows = null)
    {
        var list = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: rows))
            list.Add(row);
        return list;
    }

    #region DeleteFromRow

    [Fact]
    public async Task DeleteFromRow_on_row_with_single_cell()
    {
        await Client.MutateRowAsync(TN, "d-dfr1", Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "d-dfr1", Mutations.DeleteFromRow());
        var rows = await ReadAll(RowSet.FromRowKeys("d-dfr1"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteFromRow_on_row_with_multiple_families()
    {
        await Client.MutateRowAsync(TN, "d-dfrmf",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "d-dfrmf", Mutations.DeleteFromRow());
        var rows = await ReadAll(RowSet.FromRowKeys("d-dfrmf"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteFromRow_on_row_with_multiple_versions()
    {
        await SeedRow("d-dfrmv");
        await Client.MutateRowAsync(TN, "d-dfrmv", Mutations.DeleteFromRow());
        var rows = await ReadAll(RowSet.FromRowKeys("d-dfrmv"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteFromRow_on_nonexistent_row_is_noop()
    {
        // Should not throw
        await Client.MutateRowAsync(TN, "d-dfrnone", Mutations.DeleteFromRow());
        var rows = await ReadAll(RowSet.FromRowKeys("d-dfrnone"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteFromRow_twice_is_idempotent()
    {
        await SeedRow("d-dfridm");
        await Client.MutateRowAsync(TN, "d-dfridm", Mutations.DeleteFromRow());
        await Client.MutateRowAsync(TN, "d-dfridm", Mutations.DeleteFromRow());
        var rows = await ReadAll(RowSet.FromRowKeys("d-dfridm"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteFromRow_does_not_affect_other_rows()
    {
        await SeedRow("d-dfro1");
        await SeedRow("d-dfro2");
        await Client.MutateRowAsync(TN, "d-dfro1", Mutations.DeleteFromRow());
        var rows = await ReadAll(RowSet.FromRowKeys("d-dfro2"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteFromRow_then_rewrite_creates_new_data()
    {
        await SeedRow("d-dfrrw");
        await Client.MutateRowAsync(TN, "d-dfrrw", Mutations.DeleteFromRow());
        await Client.MutateRowAsync(TN, "d-dfrrw", Mutations.SetCell(CF, "new", "rewritten", new BigtableVersion(5000)));
        var rows = await ReadAll(RowSet.FromRowKeys("d-dfrrw"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("new");
    }

    #endregion

    #region DeleteFromFamily

    [Fact]
    public async Task DeleteFromFamily_single_family()
    {
        await Client.MutateRowAsync(TN, "d-dff1",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "d-dff1", Mutations.DeleteFromFamily(CF));
        var rows = await ReadAll(RowSet.FromRowKeys("d-dff1"));
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF2);
    }

    [Fact]
    public async Task DeleteFromFamily_all_families_makes_row_invisible()
    {
        await Client.MutateRowAsync(TN, "d-dffai",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "d-dffai",
            Mutations.DeleteFromFamily(CF),
            Mutations.DeleteFromFamily(CF2));
        var rows = await ReadAll(RowSet.FromRowKeys("d-dffai"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteFromFamily_removes_all_columns()
    {
        await SeedMultiColumn("d-dffmc", new[] { "a", "b", "c" });
        await Client.MutateRowAsync(TN, "d-dffmc", Mutations.DeleteFromFamily(CF));
        var rows = await ReadAll(RowSet.FromRowKeys("d-dffmc"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteFromFamily_removes_all_versions()
    {
        await SeedRow("d-dffmv", versions: 5);
        await Client.MutateRowAsync(TN, "d-dffmv", Mutations.DeleteFromFamily(CF));
        var rows = await ReadAll(RowSet.FromRowKeys("d-dffmv"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteFromFamily_preserves_other_family()
    {
        await Client.MutateRowAsync(TN, "d-dffpof",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "v2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "d-dffpof", Mutations.DeleteFromFamily(CF));
        var rows = await ReadAll(RowSet.FromRowKeys("d-dffpof"));
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF2);
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v2");
    }

    [Fact]
    public async Task DeleteFromFamily_then_rewrite_same_family()
    {
        await SeedRow("d-dffrw");
        await Client.MutateRowAsync(TN, "d-dffrw", Mutations.DeleteFromFamily(CF));
        await Client.MutateRowAsync(TN, "d-dffrw", Mutations.SetCell(CF, "new", "val", new BigtableVersion(5000)));
        var rows = await ReadAll(RowSet.FromRowKeys("d-dffrw"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("new");
    }

    #endregion

    #region DeleteFromColumn

    [Fact]
    public async Task DeleteFromColumn_all_versions()
    {
        await SeedRow("d-dfcav", versions: 5);
        await Client.MutateRowAsync(TN, "d-dfcav",
            Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(new BigtableVersion(0), new BigtableVersion(10000))));
        var rows = await ReadAll(RowSet.FromRowKeys("d-dfcav"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteFromColumn_specific_time_range()
    {
        await SeedRow("d-dfctr", versions: 5);
        // Delete versions at 2000ms and 3000ms (range [2ms, 4ms) in microseconds)
        await Client.MutateRowAsync(TN, "d-dfctr",
            Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(4000))));
        var rows = await ReadAll(RowSet.FromRowKeys("d-dfctr"));
        rows.Should().ContainSingle();
        var ts = rows[0].Families[0].Columns[0].Cells.Select(c => c.TimestampMicros).ToList();
        ts.Should().NotContain(2_000_000);
        ts.Should().NotContain(3_000_000);
        ts.Should().Contain(1_000_000);
        ts.Should().Contain(4_000_000);
        ts.Should().Contain(5_000_000);
    }

    [Fact]
    public async Task DeleteFromColumn_single_version()
    {
        await SeedRow("d-dfcsv", versions: 3);
        await Client.MutateRowAsync(TN, "d-dfcsv",
            Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(3000))));
        var rows = await ReadAll(RowSet.FromRowKeys("d-dfcsv"));
        rows.Should().ContainSingle();
        var ts = rows[0].Families[0].Columns[0].Cells.Select(c => c.TimestampMicros).ToList();
        ts.Should().HaveCount(2);
        ts.Should().Contain(1_000_000);
        ts.Should().Contain(3_000_000);
    }

    [Fact]
    public async Task DeleteFromColumn_preserves_other_columns()
    {
        await SeedMultiColumn("d-dfcpoc", new[] { "a", "b", "c" });
        await Client.MutateRowAsync(TN, "d-dfcpoc",
            Mutations.DeleteFromColumn(CF, "b", new BigtableVersionRange(new BigtableVersion(0), new BigtableVersion(2000))));
        var rows = await ReadAll(RowSet.FromRowKeys("d-dfcpoc"));
        rows.Should().ContainSingle();
        var cols = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("a");
        cols.Should().Contain("c");
        cols.Should().NotContain("b");
    }

    [Fact]
    public async Task DeleteFromColumn_nonexistent_column_is_noop()
    {
        await SeedRow("d-dfcne");
        await Client.MutateRowAsync(TN, "d-dfcne",
            Mutations.DeleteFromColumn(CF, "nonexistent", new BigtableVersionRange(new BigtableVersion(0), new BigtableVersion(10000))));
        var rows = await ReadAll(RowSet.FromRowKeys("d-dfcne"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(3);
    }

    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task DeleteFromColumn_empty_time_range_is_noop()
    {
        await SeedRow("d-dfcetr");
        // Range where start >= end — no cells match
        await Client.MutateRowAsync(TN, "d-dfcetr",
            Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(new BigtableVersion(5000), new BigtableVersion(5000))));
        var rows = await ReadAll(RowSet.FromRowKeys("d-dfcetr"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task DeleteFromColumn_unbounded_start()
    {
        await SeedRow("d-dfcus", versions: 5);
        // Ref: timestamp_range with start=0, end=3ms → deletes versions at 1ms, 2ms
        await Client.MutateRowAsync(TN, "d-dfcus",
            Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(new BigtableVersion(0), new BigtableVersion(3000))));
        var rows = await ReadAll(RowSet.FromRowKeys("d-dfcus"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(3); // 3ms, 4ms, 5ms
    }

    [Fact]
    public async Task DeleteFromColumn_then_rewrite_same_version()
    {
        await Client.MutateRowAsync(TN, "d-dfcrw",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "d-dfcrw",
            Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(new BigtableVersion(0), new BigtableVersion(2000))));
        await Client.MutateRowAsync(TN, "d-dfcrw",
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("d-dfcrw"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    #endregion

    #region Combined delete patterns

    [Fact]
    public async Task Delete_column_and_set_cell_same_column_same_mutation()
    {
        await Client.MutateRowAsync(TN, "d-dcsc",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "d-dcsc",
            Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(new BigtableVersion(0), new BigtableVersion(2000))),
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(3000)));
        var rows = await ReadAll(RowSet.FromRowKeys("d-dcsc"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task DeleteFromRow_and_SetCell_same_mutation()
    {
        await SeedRow("d-drsc");
        await Client.MutateRowAsync(TN, "d-drsc",
            Mutations.DeleteFromRow(),
            Mutations.SetCell(CF, "c", "after", new BigtableVersion(5000)));
        var rows = await ReadAll(RowSet.FromRowKeys("d-drsc"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("after");
    }

    [Fact]
    public async Task DeleteFromFamily_and_SetCell_other_family_same_mutation()
    {
        await Client.MutateRowAsync(TN, "d-dfscof",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "v2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "d-dfscof",
            Mutations.DeleteFromFamily(CF),
            Mutations.SetCell(CF2, "new", "v3", new BigtableVersion(2000)));
        var rows = await ReadAll(RowSet.FromRowKeys("d-dfscof"));
        rows.Should().ContainSingle();
        rows[0].Families.All(f => f.Name == CF2).Should().BeTrue();
    }

    [Fact]
    public async Task Multiple_delete_from_column_in_same_mutation()
    {
        await SeedMultiColumn("d-mdfc", new[] { "a", "b", "c", "d" });
        await Client.MutateRowAsync(TN, "d-mdfc",
            Mutations.DeleteFromColumn(CF, "a", new BigtableVersionRange(new BigtableVersion(0), new BigtableVersion(2000))),
            Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(new BigtableVersion(0), new BigtableVersion(2000))));
        var rows = await ReadAll(RowSet.FromRowKeys("d-mdfc"));
        rows.Should().ContainSingle();
        var cols = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Equal("b", "d");
    }

    [Fact]
    public async Task DeleteFromRow_in_MutateRows_batch()
    {
        await SeedRow("d-dfrb1");
        await SeedRow("d-dfrb2");
        var entries = new[]
        {
            Mutations.CreateEntry("d-dfrb1", Mutations.DeleteFromRow()),
            Mutations.CreateEntry("d-dfrb2", Mutations.SetCell(CF, "x", "y", new BigtableVersion(5000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var r1 = await ReadAll(RowSet.FromRowKeys("d-dfrb1"));
        r1.Should().BeEmpty();
        var r2 = await ReadAll(RowSet.FromRowKeys("d-dfrb2"));
        r2.Should().ContainSingle();
    }

    #endregion

    #region Delete with version patterns

    [Fact]
    public async Task Delete_oldest_version_keep_rest()
    {
        await SeedRow("d-dov", versions: 3);
        await Client.MutateRowAsync(TN, "d-dov",
            Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(new BigtableVersion(0), new BigtableVersion(2000))));
        var rows = await ReadAll(RowSet.FromRowKeys("d-dov"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(2);
        var ts = rows[0].Families[0].Columns[0].Cells.Select(c => c.TimestampMicros / 1000).ToList();
        ts.Should().NotContain(1);
    }

    [Fact]
    public async Task Delete_newest_version_keep_rest()
    {
        await SeedRow("d-dnv", versions: 3);
        await Client.MutateRowAsync(TN, "d-dnv",
            Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(new BigtableVersion(3000), new BigtableVersion(4000))));
        var rows = await ReadAll(RowSet.FromRowKeys("d-dnv"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(2);
        var ts = rows[0].Families[0].Columns[0].Cells.Select(c => c.TimestampMicros / 1000).ToList();
        ts.Should().NotContain(3);
    }

    [Fact]
    public async Task Delete_middle_version()
    {
        await SeedRow("d-dmv", versions: 5);
        await Client.MutateRowAsync(TN, "d-dmv",
            Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(new BigtableVersion(3000), new BigtableVersion(4000))));
        var rows = await ReadAll(RowSet.FromRowKeys("d-dmv"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(4);
    }

    [Fact]
    public async Task Delete_all_versions_makes_cell_invisible()
    {
        await SeedRow("d-davi", versions: 3);
        await Client.MutateRowAsync(TN, "d-davi",
            Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(new BigtableVersion(0), new BigtableVersion(10000))));
        var rows = await ReadAll(RowSet.FromRowKeys("d-davi"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Sequential_deletes_narrow_version_set()
    {
        await SeedRow("d-sdnv", versions: 5);
        // Delete 1ms
        await Client.MutateRowAsync(TN, "d-sdnv",
            Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(new BigtableVersion(1000), new BigtableVersion(2000))));
        // Delete 5ms
        await Client.MutateRowAsync(TN, "d-sdnv",
            Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(new BigtableVersion(5000), new BigtableVersion(6000))));
        // Delete 3ms
        await Client.MutateRowAsync(TN, "d-sdnv",
            Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(new BigtableVersion(3000), new BigtableVersion(4000))));
        var rows = await ReadAll(RowSet.FromRowKeys("d-sdnv"));
        rows.Should().ContainSingle();
        var ts = rows[0].Families[0].Columns[0].Cells.Select(c => c.TimestampMicros / 1000).ToList();
        ts.Should().HaveCount(2);
        ts.Should().Contain(new[] { 2000L, 4000L });
    }

    #endregion
}

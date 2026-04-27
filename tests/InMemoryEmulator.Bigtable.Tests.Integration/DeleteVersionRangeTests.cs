using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for delete mutation semantics with version ranges, families, and columns.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class DeleteVersionRangeTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "del-ver";
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public DeleteVersionRangeTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null)
    {
        var list = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: rows, filter: filter))
            list.Add(row);
        return list;
    }

    private async Task SeedRow(string key, string family, string col, int versions)
    {
        var mutations = new List<Mutation>();
        for (int v = 1; v <= versions; v++)
            mutations.Add(Mutations.SetCell(family, col, $"v{v}", new BigtableVersion(v * 1000)));
        await Client.MutateRowAsync(TN, key, mutations.ToArray());
    }

    #region DeleteFromColumn with time range

    [Fact]
    public async Task Delete_column_all_versions()
    {
        await SeedRow("dvr-all", CF, "c", 5);
        await Client.MutateRowAsync(TN, "dvr-all", Mutations.DeleteFromColumn(CF, "c"));
        var rows = await ReadAll(RowSet.FromRowKeys("dvr-all"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_column_with_version_range_removes_middle()
    {
        // Versions 1000,2000,3000,4000,5000
        await SeedRow("dvr-mid", CF, "c", 5);
        // Delete versions in range [2000, 4000) -- i.e., 2000 and 3000
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
        //   DeleteFromColumn time_range is [start_timestamp_micros, end_timestamp_micros)
        await Client.MutateRowAsync(TN, "dvr-mid",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(4000))));
        var rows = await ReadAll(RowSet.FromRowKeys("dvr-mid"));
        var cells = rows[0].Families[0].Columns[0].Cells;
        cells.Should().HaveCount(3);
        var ts = cells.Select(c => c.TimestampMicros / 1000).OrderByDescending(t => t).ToList();
        ts.Should().BeEquivalentTo(new[] { 5000L, 4000L, 1000L });
    }

    [Fact]
    public async Task Delete_column_range_start_only()
    {
        await SeedRow("dvr-start", CF, "c", 5);
        // Delete versions >= 3000 (start 3000, no end)
        await Client.MutateRowAsync(TN, "dvr-start",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(3000), null)));
        var rows = await ReadAll(RowSet.FromRowKeys("dvr-start"));
        var cells = rows[0].Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2);
        var ts = cells.Select(c => c.TimestampMicros / 1000).OrderByDescending(t => t).ToList();
        ts.Should().BeEquivalentTo(new[] { 2000L, 1000L });
    }

    [Fact]
    public async Task Delete_column_range_end_only()
    {
        await SeedRow("dvr-end", CF, "c", 5);
        // Delete versions < 3000 (no start, end 3000) -- i.e., 1000 and 2000
        await Client.MutateRowAsync(TN, "dvr-end",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(null, new BigtableVersion(3000))));
        var rows = await ReadAll(RowSet.FromRowKeys("dvr-end"));
        var cells = rows[0].Families[0].Columns[0].Cells;
        cells.Should().HaveCount(3);
        var ts = cells.Select(c => c.TimestampMicros / 1000).OrderByDescending(t => t).ToList();
        ts.Should().BeEquivalentTo(new[] { 5000L, 4000L, 3000L });
    }

    [Fact]
    public async Task Delete_column_range_deletes_nothing_when_no_match()
    {
        await SeedRow("dvr-none", CF, "c", 3);
        await Client.MutateRowAsync(TN, "dvr-none",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(10000), new BigtableVersion(20000))));
        var rows = await ReadAll(RowSet.FromRowKeys("dvr-none"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task Delete_column_range_single_version()
    {
        await SeedRow("dvr-one", CF, "c", 5);
        // Delete exactly version 3000: [3000, 4000)
        await Client.MutateRowAsync(TN, "dvr-one",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(3000), new BigtableVersion(3001))));
        var rows = await ReadAll(RowSet.FromRowKeys("dvr-one"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(4);
    }

    [Fact]
    public async Task Delete_column_range_all_versions_empties_row()
    {
        await SeedRow("dvr-allr", CF, "c", 3);
        await Client.MutateRowAsync(TN, "dvr-allr",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(1000), new BigtableVersion(4000))));
        var rows = await ReadAll(RowSet.FromRowKeys("dvr-allr"));
        rows.Should().BeEmpty();
    }

    #endregion

    #region DeleteFromFamily

    [Fact]
    public async Task Delete_family_removes_all_columns()
    {
        await Client.MutateRowAsync(TN, "dvr-fam",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "x", "3", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "dvr-fam", Mutations.DeleteFromFamily(CF));
        var rows = await ReadAll(RowSet.FromRowKeys("dvr-fam"));
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF2);
    }

    [Fact]
    public async Task Delete_family_all_families_empties_row()
    {
        await Client.MutateRowAsync(TN, "dvr-fam2",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "x", "2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "dvr-fam2",
            Mutations.DeleteFromFamily(CF),
            Mutations.DeleteFromFamily(CF2));
        var rows = await ReadAll(RowSet.FromRowKeys("dvr-fam2"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_family_preserves_versions_in_other_family()
    {
        await Client.MutateRowAsync(TN, "dvr-fam3",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "x", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "x", "v2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, "dvr-fam3", Mutations.DeleteFromFamily(CF));
        var rows = await ReadAll(RowSet.FromRowKeys("dvr-fam3"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    #endregion

    #region DeleteFromRow

    [Fact]
    public async Task Delete_row_removes_all_families_and_columns()
    {
        await Client.MutateRowAsync(TN, "dvr-row",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "x", "3", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "dvr-row", Mutations.DeleteFromRow());
        var rows = await ReadAll(RowSet.FromRowKeys("dvr-row"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_row_then_re_add()
    {
        await Client.MutateRowAsync(TN, "dvr-readd",
            Mutations.SetCell(CF, "a", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "dvr-readd", Mutations.DeleteFromRow());
        await Client.MutateRowAsync(TN, "dvr-readd",
            Mutations.SetCell(CF, "a", "new", new BigtableVersion(2000)));
        var rows = await ReadAll(RowSet.FromRowKeys("dvr-readd"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Delete_nonexistent_row_is_noop()
    {
        // Should not throw
        await Client.MutateRowAsync(TN, "dvr-noexist", Mutations.DeleteFromRow());
        var rows = await ReadAll(RowSet.FromRowKeys("dvr-noexist"));
        rows.Should().BeEmpty();
    }

    #endregion

    #region Combined delete and write in same mutation

    [Fact]
    public async Task Delete_row_then_set_cell_in_same_mutation()
    {
        await Client.MutateRowAsync(TN, "dvr-comb1",
            Mutations.SetCell(CF, "old", "1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "dvr-comb1",
            Mutations.DeleteFromRow(),
            Mutations.SetCell(CF, "new", "2", new BigtableVersion(2000)));
        var rows = await ReadAll(RowSet.FromRowKeys("dvr-comb1"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Delete_column_then_set_same_column()
    {
        await SeedRow("dvr-comb2", CF, "c", 3);
        await Client.MutateRowAsync(TN, "dvr-comb2",
            Mutations.DeleteFromColumn(CF, "c"),
            Mutations.SetCell(CF, "c", "fresh", new BigtableVersion(5000)));
        var rows = await ReadAll(RowSet.FromRowKeys("dvr-comb2"));
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("fresh");
    }

    [Fact]
    public async Task Delete_family_then_add_to_same_family()
    {
        await Client.MutateRowAsync(TN, "dvr-comb3",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "dvr-comb3",
            Mutations.DeleteFromFamily(CF),
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(2000)));
        var rows = await ReadAll(RowSet.FromRowKeys("dvr-comb3"));
        rows[0].Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("c");
    }

    [Fact]
    public async Task Delete_range_then_add_within_that_range()
    {
        await SeedRow("dvr-comb4", CF, "c", 5);
        await Client.MutateRowAsync(TN, "dvr-comb4",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(5000))),
            Mutations.SetCell(CF, "c", "mid", new BigtableVersion(3000)));
        var rows = await ReadAll(RowSet.FromRowKeys("dvr-comb4"));
        var cells = rows[0].Families[0].Columns[0].Cells;
        // Should have 1000, 3000 (re-added), 5000
        cells.Should().HaveCount(3);
    }

    #endregion

    #region Delete patterns with multiple columns

    [Fact]
    public async Task Delete_one_column_preserves_others()
    {
        await Client.MutateRowAsync(TN, "dvr-multi1",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "dvr-multi1", Mutations.DeleteFromColumn(CF, "b"));
        var rows = await ReadAll(RowSet.FromRowKeys("dvr-multi1"));
        var cols = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().BeEquivalentTo(new[] { "a", "c" });
    }

    [Fact]
    public async Task Delete_multiple_columns_in_one_mutation()
    {
        await Client.MutateRowAsync(TN, "dvr-multi2",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "dvr-multi2",
            Mutations.DeleteFromColumn(CF, "a"),
            Mutations.DeleteFromColumn(CF, "c"));
        var rows = await ReadAll(RowSet.FromRowKeys("dvr-multi2"));
        rows[0].Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("b");
    }

    [Fact]
    public async Task Delete_all_columns_empties_row()
    {
        await Client.MutateRowAsync(TN, "dvr-multi3",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "dvr-multi3",
            Mutations.DeleteFromColumn(CF, "a"),
            Mutations.DeleteFromColumn(CF, "b"));
        var rows = await ReadAll(RowSet.FromRowKeys("dvr-multi3"));
        rows.Should().BeEmpty();
    }

    #endregion

    #region Delete via batch (MutateRows)

    [Fact]
    public async Task Batch_delete_multiple_rows()
    {
        for (int i = 0; i < 5; i++)
            await Client.MutateRowAsync(TN, $"dvr-batch-{i}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)));
        var entries = Enumerable.Range(0, 5).Select(i =>
            Mutations.CreateEntry($"dvr-batch-{i}", Mutations.DeleteFromRow())
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("dvr-batch-", "dvr-batch~")));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Batch_mixed_delete_and_set()
    {
        await Client.MutateRowAsync(TN, "dvr-bm-a",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));
        var entries = new[]
        {
            Mutations.CreateEntry("dvr-bm-a", Mutations.DeleteFromRow()),
            Mutations.CreateEntry("dvr-bm-b", Mutations.SetCell(CF, "c", "new", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        (await ReadAll(RowSet.FromRowKeys("dvr-bm-a"))).Should().BeEmpty();
        (await ReadAll(RowSet.FromRowKeys("dvr-bm-b"))).Should().ContainSingle();
    }

    #endregion
}

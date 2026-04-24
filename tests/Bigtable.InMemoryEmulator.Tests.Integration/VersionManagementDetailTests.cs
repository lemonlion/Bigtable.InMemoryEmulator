using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for version management: writes, reads, deletes with version semantics.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class VersionManagementDetailTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "vmd-test";
    private const string CF = "cf";

    public VersionManagementDetailTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
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

    private int CellCount(Row row) =>
        row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();

    #region Write multiple versions

    [Fact]
    public async Task Write_3_versions_returns_all()
    {
        for (int v = 1; v <= 3; v++)
            await Client.MutateRowAsync(TN, "vmd-01",
                Mutations.SetCell(CF, "c", $"v{v}", new BigtableVersion(v)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("vmd-01"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task Write_10_versions_returns_all()
    {
        for (int v = 1; v <= 10; v++)
            await Client.MutateRowAsync(TN, "vmd-02",
                Mutations.SetCell(CF, "c", $"v{v}", new BigtableVersion(v)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("vmd-02"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(10);
    }

    [Fact]
    public async Task Versions_ordered_descending()
    {
        for (int v = 1; v <= 5; v++)
            await Client.MutateRowAsync(TN, "vmd-03",
                Mutations.SetCell(CF, "c", $"v{v}", new BigtableVersion(v)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("vmd-03"));
        var timestamps = rows[0].Families[0].Columns[0].Cells
            .Select(c => c.TimestampMicros).ToList();
        timestamps.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Latest_version_first()
    {
        for (int v = 1; v <= 5; v++)
            await Client.MutateRowAsync(TN, "vmd-04",
                Mutations.SetCell(CF, "c", $"v{v}", new BigtableVersion(v)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("vmd-04"));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v5");
    }

    #endregion

    #region Same version overwrite

    [Fact]
    public async Task Same_version_overwrites()
    {
        await Client.MutateRowAsync(TN, "vmd-05",
            Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "vmd-05",
            Mutations.SetCell(CF, "c", "second", new BigtableVersion(1000)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("vmd-05"));
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task Same_version_in_single_call()
    {
        await Client.MutateRowAsync(TN, "vmd-06",
            Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "second", new BigtableVersion(1000)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("vmd-06"));
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("second");
    }

    #endregion

    #region CellsPerColumnLimit with versions

    [Fact]
    public async Task CellsPerColumnLimit_1_latest()
    {
        for (int v = 1; v <= 5; v++)
            await Client.MutateRowAsync(TN, "vmd-07",
                Mutations.SetCell(CF, "c", $"v{v}", new BigtableVersion(v)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("vmd-07"),
            filter: RowFilters.CellsPerColumnLimit(1));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v5");
    }

    [Fact]
    public async Task CellsPerColumnLimit_3_returns_latest_3()
    {
        for (int v = 1; v <= 5; v++)
            await Client.MutateRowAsync(TN, "vmd-08",
                Mutations.SetCell(CF, "c", $"v{v}", new BigtableVersion(v)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("vmd-08"),
            filter: RowFilters.CellsPerColumnLimit(3));
        var values = rows[0].Families[0].Columns[0].Cells
            .Select(c => c.Value.ToStringUtf8()).ToList();
        values.Should().ContainInConsecutiveOrder("v5", "v4", "v3");
    }

    #endregion

    #region Delete versions

    [Fact]
    public async Task Delete_specific_version_range()
    {
        for (int v = 1; v <= 5; v++)
            await Client.MutateRowAsync(TN, "vmd-09",
                Mutations.SetCell(CF, "c", $"v{v}", new BigtableVersion(v)));
        // Delete versions 2-3 (end exclusive means v3 not included with BigtableVersion(4))
        await Client.MutateRowAsync(TN, "vmd-09",
            Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(new BigtableVersion(2), new BigtableVersion(4))));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("vmd-09"));
        var versions = rows[0].Families[0].Columns[0].Cells
            .Select(c => c.TimestampMicros / 1000).ToList();
        versions.Should().Contain(5); // v5 not deleted
        versions.Should().Contain(1); // v1 not deleted
        versions.Should().NotContain(2);
        versions.Should().NotContain(3);
    }

    [Fact]
    public async Task Delete_all_versions()
    {
        for (int v = 1; v <= 3; v++)
            await Client.MutateRowAsync(TN, "vmd-10",
                Mutations.SetCell(CF, "c", $"v{v}", new BigtableVersion(v)));
        await Client.MutateRowAsync(TN, "vmd-10",
            Mutations.DeleteFromColumn(CF, "c"));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("vmd-10"));
        rows.Should().BeEmpty();
    }

    #endregion

    #region Multiple columns with different version counts

    [Fact]
    public async Task Different_version_counts_per_column()
    {
        // col-a: 2 versions, col-b: 5 versions, col-c: 1 version
        for (int v = 1; v <= 2; v++)
            await Client.MutateRowAsync(TN, "vmd-11",
                Mutations.SetCell(CF, "col-a", $"a{v}", new BigtableVersion(v)));
        for (int v = 1; v <= 5; v++)
            await Client.MutateRowAsync(TN, "vmd-11",
                Mutations.SetCell(CF, "col-b", $"b{v}", new BigtableVersion(v)));
        await Client.MutateRowAsync(TN, "vmd-11",
            Mutations.SetCell(CF, "col-c", "c1", new BigtableVersion(1)));

        var rows = await ReadAll(rows: RowSet.FromRowKeys("vmd-11"));
        var colA = rows[0].Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "col-a");
        var colB = rows[0].Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "col-b");
        var colC = rows[0].Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "col-c");
        colA.Cells.Should().HaveCount(2);
        colB.Cells.Should().HaveCount(5);
        colC.Cells.Should().HaveCount(1);
    }

    [Fact]
    public async Task CellsPerColumnLimit_applied_per_column()
    {
        for (int v = 1; v <= 5; v++)
        {
            await Client.MutateRowAsync(TN, "vmd-12",
                Mutations.SetCell(CF, "a", $"a{v}", new BigtableVersion(v)),
                Mutations.SetCell(CF, "b", $"b{v}", new BigtableVersion(v)));
        }
        var rows = await ReadAll(rows: RowSet.FromRowKeys("vmd-12"),
            filter: RowFilters.CellsPerColumnLimit(2));
        foreach (var col in rows[0].Families[0].Columns)
            col.Cells.Should().HaveCount(2);
    }

    #endregion

    #region Version with timestamp verification

    [Fact]
    public async Task BigtableVersion_1_is_1000_micros()
    {
        await Client.MutateRowAsync(TN, "vmd-13",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("vmd-13"));
        rows[0].Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(1000);
    }

    [Fact]
    public async Task BigtableVersion_1000_is_1000000_micros()
    {
        await Client.MutateRowAsync(TN, "vmd-14",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("vmd-14"));
        rows[0].Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(1_000_000);
    }

    [Fact]
    public async Task Large_version_number()
    {
        await Client.MutateRowAsync(TN, "vmd-15",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1_000_000)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("vmd-15"));
        rows[0].Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(1_000_000_000);
    }

    #endregion

    #region Write then read then write more

    [Fact]
    public async Task Incremental_version_growth()
    {
        await Client.MutateRowAsync(TN, "vmd-16",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1)));
        var rows1 = await ReadAll(rows: RowSet.FromRowKeys("vmd-16"));
        rows1[0].Families[0].Columns[0].Cells.Should().HaveCount(1);

        await Client.MutateRowAsync(TN, "vmd-16",
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2)));
        var rows2 = await ReadAll(rows: RowSet.FromRowKeys("vmd-16"));
        rows2[0].Families[0].Columns[0].Cells.Should().HaveCount(2);

        await Client.MutateRowAsync(TN, "vmd-16",
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3)));
        var rows3 = await ReadAll(rows: RowSet.FromRowKeys("vmd-16"));
        rows3[0].Families[0].Columns[0].Cells.Should().HaveCount(3);
    }

    #endregion
}

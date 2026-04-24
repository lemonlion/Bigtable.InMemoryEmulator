using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for MutateRow (single row) operations with detailed mutation patterns.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class SingleRowMutationDetailTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "srm-test";
    private const string CF = "cf";

    public SingleRowMutationDetailTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
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

    #region SetCell basics

    [Fact]
    public async Task SetCell_simple_write_read()
    {
        await Client.MutateRowAsync(TN, "srm-01",
            Mutations.SetCell(CF, "c", "hello", new BigtableVersion(1000)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("srm-01"));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("hello");
    }

    [Fact]
    public async Task SetCell_empty_value()
    {
        await Client.MutateRowAsync(TN, "srm-02",
            Mutations.SetCell(CF, "c", "", new BigtableVersion(1000)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("srm-02"));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().BeEmpty();
    }

    [Fact]
    public async Task SetCell_binary_value()
    {
        var bytes = new byte[] { 0x00, 0x01, 0xFF, 0xFE };
        await Client.MutateRowAsync(TN, "srm-03",
            Mutations.SetCell(CF, ByteString.CopyFromUtf8("c"),
                ByteString.CopyFrom(bytes), new BigtableVersion(1000)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("srm-03"));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToByteArray().Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public async Task SetCell_large_value()
    {
        var large = new string('A', 50_000);
        await Client.MutateRowAsync(TN, "srm-04",
            Mutations.SetCell(CF, "c", large, new BigtableVersion(1000)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("srm-04"));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Length.Should().Be(50_000);
    }

    #endregion

    #region Multiple mutations in single call

    [Fact]
    public async Task Multiple_set_cells_same_column_different_versions()
    {
        await Client.MutateRowAsync(TN, "srm-05",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("srm-05"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task Multiple_set_cells_different_columns()
    {
        await Client.MutateRowAsync(TN, "srm-06",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("srm-06"));
        rows[0].Families[0].Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task Multiple_set_cells_cross_family()
    {
        await Client.MutateRowAsync(TN, "srm-07",
            Mutations.SetCell(CF, "c", "cf1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "cf2", new BigtableVersion(1000)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("srm-07"));
        rows[0].Families.Should().HaveCount(2);
    }

    #endregion

    #region Overwrite patterns

    [Fact]
    public async Task Overwrite_same_version()
    {
        await Client.MutateRowAsync(TN, "srm-08",
            Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "srm-08",
            Mutations.SetCell(CF, "c", "second", new BigtableVersion(1000)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("srm-08"),
            filter: RowFilters.CellsPerColumnLimit(1));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task Write_new_version_preserves_old()
    {
        await Client.MutateRowAsync(TN, "srm-09",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1)));
        await Client.MutateRowAsync(TN, "srm-09",
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(2)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("srm-09"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(2);
        // Latest first (descending timestamp)
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
        rows[0].Families[0].Columns[0].Cells[1].Value.ToStringUtf8().Should().Be("old");
    }

    [Fact]
    public async Task Add_column_to_existing_row()
    {
        await Client.MutateRowAsync(TN, "srm-10",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "srm-10",
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("srm-10"));
        rows[0].Families[0].Columns.Should().HaveCount(2);
    }

    #endregion

    #region Delete mutations

    [Fact]
    public async Task DeleteFromRow_removes_all()
    {
        await Client.MutateRowAsync(TN, "srm-11",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "srm-11", Mutations.DeleteFromRow());
        var rows = await ReadAll(rows: RowSet.FromRowKeys("srm-11"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteFromFamily_removes_family_only()
    {
        await Client.MutateRowAsync(TN, "srm-12",
            Mutations.SetCell(CF, "c", "cf1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "cf2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "srm-12", Mutations.DeleteFromFamily(CF));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("srm-12"));
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be("cf2");
    }

    [Fact]
    public async Task DeleteFromColumn_removes_column()
    {
        await Client.MutateRowAsync(TN, "srm-13",
            Mutations.SetCell(CF, "keep", "yes", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "remove", "bye", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "srm-13",
            Mutations.DeleteFromColumn(CF, "remove"));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("srm-13"));
        var cols = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().ContainSingle().Which.Should().Be("keep");
    }

    [Fact]
    public async Task DeleteFromColumn_with_version_range()
    {
        // Write 3 versions
        await Client.MutateRowAsync(TN, "srm-14",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3)));
        // Delete version 2 only
        await Client.MutateRowAsync(TN, "srm-14",
            Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(new BigtableVersion(2), new BigtableVersion(3))));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("srm-14"));
        var cells = rows[0].Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2);
    }

    #endregion

    #region Set + Delete in same call

    [Fact]
    public async Task Set_and_delete_in_same_mutation()
    {
        await Client.MutateRowAsync(TN, "srm-15",
            Mutations.SetCell(CF, "old", "data", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "srm-15",
            Mutations.DeleteFromColumn(CF, "old"),
            Mutations.SetCell(CF, "new", "data", new BigtableVersion(2000)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("srm-15"));
        var cols = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("new");
        cols.Should().NotContain("old");
    }

    [Fact]
    public async Task Delete_from_row_then_set_in_same_call()
    {
        await Client.MutateRowAsync(TN, "srm-16",
            Mutations.SetCell(CF, "old", "data", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "srm-16",
            Mutations.DeleteFromRow(),
            Mutations.SetCell(CF, "fresh", "start", new BigtableVersion(2000)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("srm-16"));
        rows[0].Families[0].Columns.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("fresh");
    }

    #endregion

    #region Timestamp verification

    [Fact]
    public async Task Cell_timestamp_matches_version()
    {
        await Client.MutateRowAsync(TN, "srm-17",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(5000)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("srm-17"));
        // BigtableVersion(5000) = 5000 * 1000 micros = 5_000_000
        rows[0].Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(5_000_000);
    }

    [Fact]
    public async Task Multiple_versions_ordered_descending()
    {
        await Client.MutateRowAsync(TN, "srm-18",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1)),
            Mutations.SetCell(CF, "c", "mid", new BigtableVersion(5)),
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(10)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("srm-18"));
        var cells = rows[0].Families[0].Columns[0].Cells;
        cells[0].TimestampMicros.Should().BeGreaterThan(cells[1].TimestampMicros);
        cells[1].TimestampMicros.Should().BeGreaterThan(cells[2].TimestampMicros);
    }

    #endregion
}

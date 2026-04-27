using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for delete operations — DeleteFromRow, DeleteFromFamily,
/// DeleteFromColumn with and without time ranges, and their interactions.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class DeleteOperationComprehensiveTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "del-comp-tests";
    private const string CF = "cf";

    public DeleteOperationComprehensiveTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
    //   "DeleteFromRow: Deletes all cells from the row."
    [Fact]
    public async Task DeleteFromRow_removes_all_families()
    {
        var rk = new BigtableByteString("del-c-allfam");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("cf2", "b", "2", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromRow());

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().BeNull();
    }

    [Fact]
    public async Task DeleteFromRow_on_nonexistent_row_is_noop()
    {
        var rk = new BigtableByteString("del-c-nonexist");
        // Should not throw
        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromRow());

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().BeNull();
    }

    // Ref: "DeleteFromFamily: Deletes all cells from all columns in the specified family."
    [Fact]
    public async Task DeleteFromFamily_preserves_other_family()
    {
        var rk = new BigtableByteString("del-c-fampres");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("cf2", "b", "2", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromFamily(CF));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.Should().HaveCount(1);
        row.Families[0].Name.Should().Be("cf2");
    }

    [Fact]
    public async Task DeleteFromFamily_removes_all_columns_in_family()
    {
        var rk = new BigtableByteString("del-c-famcols");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromFamily(CF));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().BeNull();
    }

    // Ref: "DeleteFromColumn: Deletes cells from the specified column."
    [Fact]
    public async Task DeleteFromColumn_preserves_other_columns()
    {
        var rk = new BigtableByteString("del-c-colpres");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "keep", "yes", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "remove", "bye", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromColumn(CF, "remove"));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        var cols = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("keep");
        cols.Should().NotContain("remove");
    }

    [Fact]
    public async Task DeleteFromColumn_removes_all_versions()
    {
        var rk = new BigtableByteString("del-c-colvers");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v3", new BigtableVersion(3000)));

        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromColumn(CF, "col"));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().BeNull();
    }

    // Ref: "DeleteFromColumn: time_range — The range of timestamps to delete."
    [Fact]
    public async Task DeleteFromColumn_with_time_range_deletes_in_range()
    {
        var rk = new BigtableByteString("del-c-tr");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v3", new BigtableVersion(3000)));

        // Delete timestamps in [2000ms, 3000ms) — only v2 removed
        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "col",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(3000))));

        var row = await Client.ReadRowAsync(TN, rk);
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2);
        cells.Select(c => c.Value.ToStringUtf8()).Should().Contain("v1");
        cells.Select(c => c.Value.ToStringUtf8()).Should().Contain("v3");
    }

    [Fact]
    public async Task DeleteFromColumn_time_range_start_inclusive()
    {
        var rk = new BigtableByteString("del-c-trsincl");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "at-boundary", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "before", new BigtableVersion(1000)));

        // Start inclusive at 2000ms
        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "col",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(3000))));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("before");
    }

    [Fact]
    public async Task DeleteFromColumn_time_range_end_exclusive()
    {
        var rk = new BigtableByteString("del-c-trexcl");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "at-end", new BigtableVersion(3000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "in-range", new BigtableVersion(2000)));

        // End exclusive at 3000ms — should NOT delete timestamp 3000ms
        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "col",
                new BigtableVersionRange(new BigtableVersion(1000), new BigtableVersion(3000))));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("at-end");
    }

    [Fact]
    public async Task Delete_then_write_same_cell_shows_new_value()
    {
        var rk = new BigtableByteString("del-c-rewrite");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "first", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromColumn(CF, "col"));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "second", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task Delete_and_set_in_same_mutation_set_wins()
    {
        var rk = new BigtableByteString("del-c-combo");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "old", new BigtableVersion(1000)));

        // Ref: Mutations are applied in order
        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "col"),
            Mutations.SetCell(CF, "col", "new", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Multiple_deletes_in_single_mutation()
    {
        var rk = new BigtableByteString("del-c-mdel");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "a"),
            Mutations.DeleteFromColumn(CF, "c"));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns.Should().HaveCount(1);
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("b");
    }

    [Fact]
    public async Task DeleteFromRow_after_set_in_same_mutation()
    {
        var rk = new BigtableByteString("del-c-sdr");
        // Mutations are applied in order, so set then delete means empty
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "val", new BigtableVersion(1000)),
            Mutations.DeleteFromRow());

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().BeNull();
    }

    [Fact]
    public async Task Set_after_DeleteFromRow_in_same_mutation()
    {
        var rk = new BigtableByteString("del-c-drs");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "old", "val", new BigtableVersion(1000)));

        // Delete all, then set new — should have only the new cell
        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromRow(),
            Mutations.SetCell(CF, "new", "val", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families[0].Columns.Should().HaveCount(1);
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task DeleteFromColumn_nonexistent_column_is_noop()
    {
        var rk = new BigtableByteString("del-c-noop");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "exists", "val", new BigtableVersion(1000)));

        // Should not throw
        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "nonexistent"));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("exists");
    }

    [Fact]
    public async Task DeleteFromFamily_nonexistent_row_is_noop()
    {
        var rk = new BigtableByteString("del-c-famnorow");
        // Should not throw on nonexistent row
        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromFamily(CF));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().BeNull();
    }

    [Fact]
    public async Task Delete_one_row_does_not_affect_another()
    {
        var rk1 = new BigtableByteString("del-c-iso1");
        var rk2 = new BigtableByteString("del-c-iso2");
        await Client.MutateRowAsync(TN, rk1,
            Mutations.SetCell(CF, "col", "val1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk2,
            Mutations.SetCell(CF, "col", "val2", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk1, Mutations.DeleteFromRow());

        var row1 = await Client.ReadRowAsync(TN, rk1);
        row1.Should().BeNull();
        var row2 = await Client.ReadRowAsync(TN, rk2);
        row2.Should().NotBeNull();
        row2!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("val2");
    }

    [Fact]
    public async Task DeleteFromColumn_specific_version_keeps_others()
    {
        var rk = new BigtableByteString("del-c-specver");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v3", new BigtableVersion(3000)));

        // Delete only timestamp 2000ms
        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "col",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(2001))));

        var row = await Client.ReadRowAsync(TN, rk);
        var values = row!.Families[0].Columns[0].Cells.Select(c => c.Value.ToStringUtf8()).ToList();
        values.Should().Contain("v1");
        values.Should().Contain("v3");
        values.Should().NotContain("v2");
    }

    [Fact]
    public async Task DeleteFromFamily_removes_all_versions_of_all_columns()
    {
        var rk = new BigtableByteString("del-c-famver");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "b", "3", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("cf2", "c", "4", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromFamily(CF));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.Should().HaveCount(1);
        row.Families[0].Name.Should().Be("cf2");
    }

    [Fact]
    public async Task Delete_row_then_rewrite_fully_replaces()
    {
        var rk = new BigtableByteString("del-c-replace");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "old1", "a", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "old2", "b", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromRow());
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "new1", "x", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns.Should().HaveCount(1);
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("new1");
    }
}

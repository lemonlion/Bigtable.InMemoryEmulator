using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for row isolation — mutations to one row must not affect other rows.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowrequest
///   "Mutations are applied atomically and in order to the specified row."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class RowIsolationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "row-iso";
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public RowIsolationTests(EmulatorSession session) => _fixture = session.CreateFixture();
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

    #region Mutation isolation between rows

    [Fact]
    public async Task SetCell_on_one_row_doesnt_affect_another()
    {
        await Client.MutateRowAsync(TN, "ri-a",
            Mutations.SetCell(CF, "c", "a-val", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ri-b",
            Mutations.SetCell(CF, "c", "b-val", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ri-a",
            Mutations.SetCell(CF, "c", "a-new", new BigtableVersion(2000)));
        // b should be unchanged
        var rowsB = await ReadAll(RowSet.FromRowKeys("ri-b"));
        rowsB[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("b-val");
    }

    [Fact]
    public async Task DeleteFromRow_on_one_row_doesnt_affect_another()
    {
        await Client.MutateRowAsync(TN, "ri-del-a",
            Mutations.SetCell(CF, "c", "a", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ri-del-b",
            Mutations.SetCell(CF, "c", "b", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ri-del-a", Mutations.DeleteFromRow());
        var rowsB = await ReadAll(RowSet.FromRowKeys("ri-del-b"));
        rowsB.Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteFromFamily_isolation()
    {
        await Client.MutateRowAsync(TN, "ri-fam-a",
            Mutations.SetCell(CF, "c", "a", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ri-fam-b",
            Mutations.SetCell(CF, "c", "b", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ri-fam-a", Mutations.DeleteFromFamily(CF));
        var rowsB = await ReadAll(RowSet.FromRowKeys("ri-fam-b"));
        rowsB.Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteFromColumn_isolation()
    {
        await Client.MutateRowAsync(TN, "ri-col-a",
            Mutations.SetCell(CF, "c", "a", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ri-col-b",
            Mutations.SetCell(CF, "c", "b", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ri-col-a", Mutations.DeleteFromColumn(CF, "c"));
        var rowsB = await ReadAll(RowSet.FromRowKeys("ri-col-b"));
        rowsB.Should().ContainSingle();
    }

    [Fact]
    public async Task Batch_mutation_isolation()
    {
        await Client.MutateRowAsync(TN, "ri-batch-a",
            Mutations.SetCell(CF, "c", "a", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ri-batch-b",
            Mutations.SetCell(CF, "c", "b", new BigtableVersion(1000)));
        var entries = new[] { Mutations.CreateEntry("ri-batch-a", Mutations.DeleteFromRow()) };
        await Client.MutateRowsAsync(TN, entries);
        var rowsB = await ReadAll(RowSet.FromRowKeys("ri-batch-b"));
        rowsB.Should().ContainSingle();
    }

    #endregion

    #region ReadModifyWrite isolation

    [Fact]
    public async Task RMW_increment_doesnt_affect_other_rows()
    {
        await Client.MutateRowAsync(TN, "ri-rmw-a",
            Mutations.SetCell(CF, "counter", ByteString.CopyFrom(BitConverter.GetBytes(10L).Reverse().ToArray()), new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ri-rmw-b",
            Mutations.SetCell(CF, "counter", ByteString.CopyFrom(BitConverter.GetBytes(20L).Reverse().ToArray()), new BigtableVersion(1000)));
        await Client.ReadModifyWriteRowAsync(TN, "ri-rmw-a",
            ReadModifyWriteRules.Increment(CF, "counter", 5));
        // b should still be 20
        var rowsB = await ReadAll(RowSet.FromRowKeys("ri-rmw-b"), RowFilters.CellsPerColumnLimit(1));
        var val = BitConverter.ToInt64(rowsB[0].Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray());
        val.Should().Be(20);
    }

    [Fact]
    public async Task RMW_append_doesnt_affect_other_rows()
    {
        await Client.MutateRowAsync(TN, "ri-rmw-ap-a",
            Mutations.SetCell(CF, "data", "hello", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ri-rmw-ap-b",
            Mutations.SetCell(CF, "data", "world", new BigtableVersion(1000)));
        await Client.ReadModifyWriteRowAsync(TN, "ri-rmw-ap-a",
            ReadModifyWriteRules.Append(CF, "data", "!"));
        var rowsB = await ReadAll(RowSet.FromRowKeys("ri-rmw-ap-b"), RowFilters.CellsPerColumnLimit(1));
        rowsB[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("world");
    }

    #endregion

    #region CheckAndMutate isolation

    [Fact]
    public async Task CAM_on_one_row_doesnt_affect_another()
    {
        await Client.MutateRowAsync(TN, "ri-cam-a",
            Mutations.SetCell(CF, "flag", "yes", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ri-cam-b",
            Mutations.SetCell(CF, "flag", "yes", new BigtableVersion(1000)));
        await Client.CheckAndMutateRowAsync(TN, "ri-cam-a",
            RowFilters.Chain(RowFilters.CellsPerColumnLimit(1), RowFilters.ValueExact("yes")),
            Mutations.SetCell(CF, "flag", "no", new BigtableVersion(2000)));
        // b should still be "yes"
        var rowsB = await ReadAll(RowSet.FromRowKeys("ri-cam-b"), RowFilters.CellsPerColumnLimit(1));
        rowsB[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("yes");
    }

    [Fact]
    public async Task CAM_false_mutation_isolation()
    {
        await Client.MutateRowAsync(TN, "ri-cam-f-a",
            Mutations.SetCell(CF, "flag", "no", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ri-cam-f-b",
            Mutations.SetCell(CF, "flag", "no", new BigtableVersion(1000)));
        await Client.CheckAndMutateRowAsync(TN, "ri-cam-f-a",
            RowFilters.Chain(RowFilters.CellsPerColumnLimit(1), RowFilters.ValueExact("yes")),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "flag", "changed", new BigtableVersion(2000)) });
        var rowsB = await ReadAll(RowSet.FromRowKeys("ri-cam-f-b"), RowFilters.CellsPerColumnLimit(1));
        rowsB[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("no");
    }

    #endregion

    #region Cross-family isolation

    [Fact]
    public async Task Different_families_same_column_name_isolated()
    {
        await Client.MutateRowAsync(TN, "ri-xf",
            Mutations.SetCell(CF, "c", "cf-val", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "cf2-val", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ri-xf",
            Mutations.DeleteFromFamily(CF));
        var rows = await ReadAll(RowSet.FromRowKeys("ri-xf"));
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF2);
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("cf2-val");
    }

    [Fact]
    public async Task Delete_column_in_one_family_preserves_same_name_in_other()
    {
        await Client.MutateRowAsync(TN, "ri-xf2",
            Mutations.SetCell(CF, "shared", "cf-val", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "shared", "cf2-val", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ri-xf2",
            Mutations.DeleteFromColumn(CF, "shared"));
        var rows = await ReadAll(RowSet.FromRowKeys("ri-xf2"));
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF2);
    }

    #endregion

    #region Table isolation

    [Fact]
    public async Task Different_tables_are_isolated()
    {
        var table2 = "row-iso-2";
        await _fixture.CreateTableAsync(table2, new[] { CF });
        var tn2 = _fixture.GetTableName(table2);
        await Client.MutateRowAsync(TN, "ri-tbl",
            Mutations.SetCell(CF, "c", "table1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(tn2, "ri-tbl",
            Mutations.SetCell(CF, "c", "table2", new BigtableVersion(1000)));
        // Delete from table 1
        await Client.MutateRowAsync(TN, "ri-tbl", Mutations.DeleteFromRow());
        // Table 2 should be unaffected
        var rows2 = new List<Row>();
        await foreach (var row in Client.ReadRows(tn2, RowSet.FromRowKeys("ri-tbl")))
            rows2.Add(row);
        rows2.Should().ContainSingle();
        rows2[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("table2");
    }

    #endregion

    #region Sequenced operations isolation

    [Fact]
    public async Task Interleaved_row_operations()
    {
        // Create row A v1, row B v1, update A v2, update B v2 — verify both correct
        await Client.MutateRowAsync(TN, "ri-inter-a",
            Mutations.SetCell(CF, "c", "a1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ri-inter-b",
            Mutations.SetCell(CF, "c", "b1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ri-inter-a",
            Mutations.SetCell(CF, "c", "a2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, "ri-inter-b",
            Mutations.SetCell(CF, "c", "b2", new BigtableVersion(2000)));
        var rowsA = await ReadAll(RowSet.FromRowKeys("ri-inter-a"), RowFilters.CellsPerColumnLimit(1));
        var rowsB = await ReadAll(RowSet.FromRowKeys("ri-inter-b"), RowFilters.CellsPerColumnLimit(1));
        rowsA[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("a2");
        rowsB[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("b2");
    }

    [Fact]
    public async Task Multiple_row_operations_preserve_all_data()
    {
        for (int i = 0; i < 10; i++)
            await Client.MutateRowAsync(TN, $"ri-mp-{i:D2}",
                Mutations.SetCell(CF, "c", $"val-{i}", new BigtableVersion(1000)));
        // Verify all 10 rows
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ri-mp-", "ri-mp~")));
        rows.Should().HaveCount(10);
        for (int i = 0; i < 10; i++)
            rows[i].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be($"val-{i}");
    }

    #endregion
}

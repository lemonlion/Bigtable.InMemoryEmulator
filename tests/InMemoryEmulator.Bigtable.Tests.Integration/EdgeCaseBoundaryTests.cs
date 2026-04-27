using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for edge cases with empty data, single-cell rows, and boundary conditions.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class EdgeCaseBoundaryTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "edge-bnd";
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public EdgeCaseBoundaryTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null, long? limit = null)
    {
        var list = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: rows, filter: filter, rowsLimit: limit))
            list.Add(row);
        return list;
    }

    #region Empty table operations

    [Fact]
    public async Task Read_empty_table()
    {
        var emptyTable = "edge-empty";
        await _fixture.CreateTableAsync(emptyTable, new[] { CF });
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(_fixture.GetTableName(emptyTable)))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Read_specific_key_from_empty_table()
    {
        var emptyTable = "edge-empty2";
        await _fixture.CreateTableAsync(emptyTable, new[] { CF });
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(_fixture.GetTableName(emptyTable), RowSet.FromRowKeys("nonexistent")))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Read_range_from_empty_table()
    {
        var emptyTable = "edge-empty3";
        await _fixture.CreateTableAsync(emptyTable, new[] { CF });
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(_fixture.GetTableName(emptyTable), RowSet.FromRowRanges(RowRange.ClosedOpen("a", "z"))))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    #endregion

    #region Single-cell row operations

    [Fact]
    public async Task Single_cell_row()
    {
        await Client.MutateRowAsync(TN, "eb-single",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("eb-single"));
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle();
        rows[0].Families[0].Columns.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle();
    }

    [Fact]
    public async Task Single_cell_with_empty_value()
    {
        await Client.MutateRowAsync(TN, "eb-emptyval",
            Mutations.SetCell(CF, "c", ByteString.Empty, new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("eb-emptyval"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task Single_cell_with_empty_column_qualifier()
    {
        await Client.MutateRowAsync(TN, "eb-emptyq",
            Mutations.SetCell(CF, "", "v", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("eb-emptyq"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Qualifier.Length.Should().Be(0);
    }

    #endregion

    #region Binary value edge cases

    [Fact]
    public async Task Null_bytes_in_value()
    {
        var bytes = new byte[] { 0, 1, 0, 2, 0 };
        await Client.MutateRowAsync(TN, "eb-null",
            Mutations.SetCell(CF, "c", ByteString.CopyFrom(bytes), new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("eb-null"));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToByteArray()
            .Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public async Task All_byte_values()
    {
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        await Client.MutateRowAsync(TN, "eb-allbytes",
            Mutations.SetCell(CF, "c", ByteString.CopyFrom(bytes), new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("eb-allbytes"));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToByteArray()
            .Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public async Task Binary_column_qualifier()
    {
        var qualBytes = new byte[] { 0xFF, 0x00, 0xAB };
        await Client.MutateRowAsync(TN, "eb-binq",
            Mutations.SetCell(CF, ByteString.CopyFrom(qualBytes), ByteString.CopyFromUtf8("v"), new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("eb-binq"));
        rows[0].Families[0].Columns[0].Qualifier.ToByteArray()
            .Should().BeEquivalentTo(qualBytes);
    }

    #endregion

    #region Row key edge cases

    [Fact]
    public async Task Single_byte_row_key()
    {
        await Client.MutateRowAsync(TN, "a",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("a"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Row_key_with_special_characters()
    {
        var key = "eb-special!@#$%^&*()";
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys(key));
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be(key);
    }

    [Fact]
    public async Task Row_key_with_unicode()
    {
        var key = "eb-ünïcödé-日本語";
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys(key));
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be(key);
    }

    [Fact]
    public async Task Row_key_with_hash_separator()
    {
        var key = "tenant#user#session#12345";
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys(key));
        rows.Should().ContainSingle();
    }

    #endregion

    #region Multiple families edge cases

    [Fact]
    public async Task Row_with_data_in_only_one_of_two_families()
    {
        await Client.MutateRowAsync(TN, "eb-onefam",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("eb-onefam"));
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF);
    }

    [Fact]
    public async Task Row_with_data_in_both_families()
    {
        await Client.MutateRowAsync(TN, "eb-bothfam",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "v2", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("eb-bothfam"));
        rows[0].Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Delete_only_family_removes_row()
    {
        await Client.MutateRowAsync(TN, "eb-delfam",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "eb-delfam", Mutations.DeleteFromFamily(CF));
        var rows = await ReadAll(RowSet.FromRowKeys("eb-delfam"));
        rows.Should().BeEmpty();
    }

    #endregion

    #region Limit edge cases

    [Fact]
    public async Task RowsLimit_1()
    {
        for (int i = 0; i < 5; i++)
            await Client.MutateRowAsync(TN, $"eb-lim-{i}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("eb-lim-", "eb-lim~")), limit: 1);
        rows.Should().ContainSingle();
    }

    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task CellsPerColumnLimit_0_returns_no_cells()
    {
        await Client.MutateRowAsync(TN, "eb-cpc0",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));
        var rows = await ReadAll(RowSet.FromRowKeys("eb-cpc0"), RowFilters.CellsPerColumnLimit(0));
        rows.Should().BeEmpty();
    }

    #endregion

    #region Overwrite patterns

    [Fact]
    public async Task Many_overwrites_same_version()
    {
        for (int i = 0; i < 10; i++)
            await Client.MutateRowAsync(TN, "eb-ow",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("eb-ow"));
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("v9"); // last write wins
    }

    [Fact]
    public async Task Overwrite_with_empty_value()
    {
        await Client.MutateRowAsync(TN, "eb-ow-empty",
            Mutations.SetCell(CF, "c", "notempty", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "eb-ow-empty",
            Mutations.SetCell(CF, "c", ByteString.Empty, new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("eb-ow-empty"));
        rows[0].Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    #endregion

    #region ReadModifyWrite edge cases

    [Fact]
    public async Task RMW_on_nonexistent_row_creates_it()
    {
        var result = await Client.ReadModifyWriteRowAsync(TN, "eb-rmw-new",
            ReadModifyWriteRules.Append(CF, "c", "hello"));
        result.Should().NotBeNull();
        var rows = await ReadAll(RowSet.FromRowKeys("eb-rmw-new"), RowFilters.CellsPerColumnLimit(1));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("hello");
    }

    [Fact]
    public async Task RMW_increment_on_nonexistent_column()
    {
        await Client.MutateRowAsync(TN, "eb-rmw-nocol",
            Mutations.SetCell(CF, "other", "x", new BigtableVersion(1000)));
        var result = await Client.ReadModifyWriteRowAsync(TN, "eb-rmw-nocol",
            ReadModifyWriteRules.Increment(CF, "counter", 5));
        var rows = await ReadAll(RowSet.FromRowKeys("eb-rmw-nocol"), RowFilters.CellsPerColumnLimit(1));
        var counterCol = rows[0].Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "counter");
        var val = BitConverter.ToInt64(counterCol.Cells[0].Value.ToByteArray().Reverse().ToArray());
        val.Should().Be(5);
    }

    [Fact]
    public async Task RMW_append_empty_string()
    {
        await Client.MutateRowAsync(TN, "eb-rmw-emp",
            Mutations.SetCell(CF, "c", "existing", new BigtableVersion(1000)));
        await Client.ReadModifyWriteRowAsync(TN, "eb-rmw-emp",
            ReadModifyWriteRules.Append(CF, "c", ""));
        var rows = await ReadAll(RowSet.FromRowKeys("eb-rmw-emp"), RowFilters.CellsPerColumnLimit(1));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("existing");
    }

    #endregion

    #region CheckAndMutate edge cases

    [Fact]
    public async Task CAM_on_nonexistent_row_predicate_unmatched()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "eb-cam-norow",
            RowFilters.PassAllFilter(),
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        result.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task CAM_null_predicate_tests_row_existence()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#checkandmutaterowrequest
        //   "If no predicate_filter is provided, the check will be done on the existence of any cell in the row."
        await Client.MutateRowAsync(TN, "eb-cam-exist",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var result = await Client.CheckAndMutateRowAsync(TN, "eb-cam-exist",
            predicateFilter: null,
            trueMutations: new[] { Mutations.SetCell(CF, "exists", "true", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task CAM_null_predicate_nonexistent_row()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "eb-cam-noexist",
            predicateFilter: null,
            trueMutations: new[] { Mutations.SetCell(CF, "exists", "true", new BigtableVersion(1000)) });
        result.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task CAM_with_false_mutations_only()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "eb-cam-falseonly",
            RowFilters.PassAllFilter(),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "c", "created", new BigtableVersion(1000)) });
        result.PredicateMatched.Should().BeFalse();
        var rows = await ReadAll(RowSet.FromRowKeys("eb-cam-falseonly"));
        rows.Should().ContainSingle();
    }

    #endregion
}

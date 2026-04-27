using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for sparse row patterns: rows with different column sets.
///
/// Ref: https://cloud.google.com/bigtable/docs/schema-design
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class SparseRowPatternTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "sparse";
    private const string CF = "cf";

    public SparseRowPatternTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        // Row with only "name"
        await Client.MutateRowAsync(TN, "sp-01",
            Mutations.SetCell(CF, "name", "Alice", new BigtableVersion(1000)));
        // Row with "name" and "email"
        await Client.MutateRowAsync(TN, "sp-02",
            Mutations.SetCell(CF, "name", "Bob", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "email", "bob@test.com", new BigtableVersion(1000)));
        // Row with all columns
        await Client.MutateRowAsync(TN, "sp-03",
            Mutations.SetCell(CF, "name", "Charlie", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "email", "charlie@test.com", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "phone", "555-1234", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "address", "123 Main St", new BigtableVersion(1000)));
        // Row with only "email"
        await Client.MutateRowAsync(TN, "sp-04",
            Mutations.SetCell(CF, "email", "anon@test.com", new BigtableVersion(1000)));
        // Row with many columns (wide)
        var mutations = Enumerable.Range(0, 50).Select(i =>
            Mutations.SetCell(CF, $"attr{i:D2}", $"val{i}", new BigtableVersion(1000))
        ).ToArray();
        await Client.MutateRowAsync(TN, "sp-05", mutations);
        // Empty row (no cells)
        // (This can't be explicitly stored in Bigtable - write then delete)
        await Client.MutateRowAsync(TN, "sp-06",
            Mutations.SetCell(CF, "temp", "x", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "sp-06",
            Mutations.DeleteFromRow());
    }

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

    private int ColCount(Row row) =>
        row.Families.SelectMany(f => f.Columns).Count();

    #region Column presence filtering

    [Fact]
    public async Task Filter_rows_with_name_column()
    {
        var rows = await ReadAll(filter: RowFilters.ColumnQualifierExact("name"));
        rows.Should().HaveCount(3); // sp-01, sp-02, sp-03
    }

    [Fact]
    public async Task Filter_rows_with_email_column()
    {
        var rows = await ReadAll(filter: RowFilters.ColumnQualifierExact("email"));
        rows.Should().HaveCount(3); // sp-02, sp-03, sp-04
    }

    [Fact]
    public async Task Filter_rows_with_phone_column()
    {
        var rows = await ReadAll(filter: RowFilters.ColumnQualifierExact("phone"));
        rows.Should().ContainSingle().Which.Key.ToStringUtf8().Should().Be("sp-03");
    }

    [Fact]
    public async Task Filter_rows_with_nonexistent_column()
    {
        var rows = await ReadAll(filter: RowFilters.ColumnQualifierExact("nonexistent"));
        rows.Should().BeEmpty();
    }

    #endregion

    #region Multiple column conditions

    [Fact]
    public async Task Interleave_two_columns()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.ColumnQualifierExact("email"));
        var rows = await ReadAll(filter: filter);
        // sp-01 has name only, sp-02 has both, sp-03 has both, sp-04 has email only
        rows.Should().HaveCount(4);
    }

    [Fact]
    public async Task Interleave_preserves_columns()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.ColumnQualifierExact("email"));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("sp-02"), filter: filter);
        ColCount(rows[0]).Should().Be(2);
    }

    #endregion

    #region Sparse reads

    [Fact]
    public async Task Read_full_scan_skips_deleted_row()
    {
        var rows = await ReadAll();
        // sp-06 was deleted, should not appear
        rows.All(r => r.Key.ToStringUtf8() != "sp-06").Should().BeTrue();
    }

    [Fact]
    public async Task Read_all_returns_varied_column_counts()
    {
        var rows = await ReadAll();
        var colCounts = rows.Select(r => ColCount(r)).ToList();
        colCounts.Should().Contain(1);  // sp-01 OR sp-04
        colCounts.Should().Contain(2);  // sp-02
        colCounts.Should().Contain(4);  // sp-03
        colCounts.Should().Contain(50); // sp-05
    }

    [Fact]
    public async Task CellsPerRowLimit_on_sparse_rows()
    {
        var rows = await ReadAll(filter: RowFilters.CellsPerRowLimit(1));
        // Each row should have exactly 1 cell
        foreach (var row in rows)
            row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count()
                .Should().Be(1);
    }

    #endregion

    #region Wide row operations

    [Fact]
    public async Task Wide_row_column_count()
    {
        var rows = await ReadAll(rows: RowSet.FromRowKeys("sp-05"));
        ColCount(rows[0]).Should().Be(50);
    }

    [Fact]
    public async Task Wide_row_column_regex()
    {
        var rows = await ReadAll(
            rows: RowSet.FromRowKeys("sp-05"),
            filter: RowFilters.ColumnQualifierRegex("attr0.*"));
        ColCount(rows[0]).Should().Be(10); // attr00..attr09
    }

    [Fact]
    public async Task Wide_row_cells_per_row_offset()
    {
        var rows = await ReadAll(
            rows: RowSet.FromRowKeys("sp-05"),
            filter: RowFilters.CellsPerRowOffset(45));
        var cellCount = rows[0].Families.SelectMany(f => f.Columns)
            .SelectMany(c => c.Cells).Count();
        cellCount.Should().Be(5);
    }

    [Fact]
    public async Task Wide_row_strip_value()
    {
        var rows = await ReadAll(
            rows: RowSet.FromRowKeys("sp-05"),
            filter: RowFilters.StripValueTransformer());
        ColCount(rows[0]).Should().Be(50);
        foreach (var fam in rows[0].Families)
            foreach (var col in fam.Columns)
                foreach (var cell in col.Cells)
                    cell.Value.Length.Should().Be(0);
    }

    #endregion

    #region Adding columns to existing rows

    [Fact]
    public async Task Add_column_to_sparse_row()
    {
        await Client.MutateRowAsync(TN, "sp-01",
            Mutations.SetCell(CF, "phone", "555-9999", new BigtableVersion(2000)));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("sp-01"));
        ColCount(rows[0]).Should().Be(2); // name + phone
    }

    [Fact]
    public async Task Delete_column_makes_row_sparser()
    {
        await Client.MutateRowAsync(TN, "sp-03",
            Mutations.DeleteFromColumn(CF, "address"));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("sp-03"));
        var quals = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().NotContain("address");
    }

    #endregion

    #region CheckAndMutate on sparse rows

    [Fact]
    public async Task CaM_column_exists_on_sparse()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "sp-01",
            predicateFilter: RowFilters.Chain(
                RowFilters.ColumnQualifierExact("email"),
                RowFilters.CellsPerColumnLimit(1)),
            trueMutations: new[] { Mutations.SetCell(CF, "has_email", "true", new BigtableVersion(3000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "has_email", "false", new BigtableVersion(3000)) });
        // sp-01 originally had only "name" (we added phone in a previous test, but not email)
        result.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task CaM_column_exists_on_full()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "sp-02",
            predicateFilter: RowFilters.Chain(
                RowFilters.ColumnQualifierExact("email"),
                RowFilters.CellsPerColumnLimit(1)),
            trueMutations: new[] { Mutations.SetCell(CF, "has_email", "true", new BigtableVersion(3000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "has_email", "false", new BigtableVersion(3000)) });
        result.PredicateMatched.Should().BeTrue();
    }

    #endregion
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for chain filter with various depth and combination patterns.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ChainFilterComboTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "cfc-test";
    private const string CF = "cf";

    public ChainFilterComboTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
        // 10 rows, each with 5 columns, 3 versions per column
        for (int r = 0; r < 10; r++)
        {
            for (int c = 0; c < 5; c++)
                for (int v = 1; v <= 3; v++)
                    await Client.MutateRowAsync(TN, $"cfc-{r:D2}",
                        Mutations.SetCell(CF, $"col{c}", $"r{r}c{c}v{v}", new BigtableVersion(v * 1000)));
            await Client.MutateRowAsync(TN, $"cfc-{r:D2}",
                Mutations.SetCell("cf2", "x", $"cf2-{r}", new BigtableVersion(1000)));
        }
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

    private int CellCount(List<Row> rows) =>
        rows.SelectMany(r => r.Families).SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();

    #region Two-filter chains

    [Fact]
    public async Task Chain_rowkey_and_column()
    {
        var filter = RowFilters.Chain(
            RowFilters.RowKeyRegex("cfc-0[0-2]"),
            RowFilters.ColumnQualifierExact("col0"));
        var rows = await ReadAll(filter: filter);
        rows.Should().HaveCount(3);
        foreach (var row in rows)
            row.Families[0].Columns.Should().ContainSingle();
    }

    [Fact]
    public async Task Chain_column_and_limit()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("col0"),
            RowFilters.CellsPerColumnLimit(1));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("cfc-00"), filter: filter);
        CellCount(rows).Should().Be(1);
    }

    [Fact]
    public async Task Chain_family_and_column()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameRegex(CF),
            RowFilters.ColumnQualifierExact("col0"));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("cfc-00"), filter: filter);
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF);
    }

    [Fact]
    public async Task Chain_value_and_strip()
    {
        var filter = RowFilters.Chain(
            RowFilters.ValueRegex("r0c0v3"),
            RowFilters.StripValueTransformer());
        var rows = await ReadAll(rows: RowSet.FromRowKeys("cfc-00"), filter: filter);
        CellCount(rows).Should().Be(1);
        rows[0].Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    #endregion

    #region Three-filter chains

    [Fact]
    public async Task Chain_three_filters()
    {
        var filter = RowFilters.Chain(
            RowFilters.RowKeyRegex("cfc-0[0-4]"),
            RowFilters.ColumnQualifierExact("col1"),
            RowFilters.CellsPerColumnLimit(1));
        var rows = await ReadAll(filter: filter);
        rows.Should().HaveCount(5);
        foreach (var row in rows)
            CellCount(new List<Row> { row }).Should().Be(1);
    }

    [Fact]
    public async Task Chain_family_column_value()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameRegex(CF),
            RowFilters.ColumnQualifierExact("col2"),
            RowFilters.ValueRegex("r0c2v3"));
        var rows = await ReadAll(filter: filter);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Chain_column_limit_strip()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("col0"),
            RowFilters.CellsPerColumnLimit(2),
            RowFilters.StripValueTransformer());
        var rows = await ReadAll(rows: RowSet.FromRowKeys("cfc-00"), filter: filter);
        CellCount(rows).Should().Be(2);
        foreach (var fam in rows[0].Families)
            foreach (var col in fam.Columns)
                foreach (var cell in col.Cells)
                    cell.Value.Length.Should().Be(0);
    }

    #endregion

    #region Four-filter chains

    [Fact]
    public async Task Chain_four_filters()
    {
        var filter = RowFilters.Chain(
            RowFilters.RowKeyRegex("cfc-0[0-2]"),
            RowFilters.FamilyNameRegex(CF),
            RowFilters.ColumnQualifierExact("col0"),
            RowFilters.CellsPerColumnLimit(1));
        var rows = await ReadAll(filter: filter);
        rows.Should().HaveCount(3);
        CellCount(rows).Should().Be(3);
    }

    #endregion

    #region Chain with interleave inside

    [Fact]
    public async Task Chain_with_interleave()
    {
        var filter = RowFilters.Chain(
            RowFilters.Interleave(
                RowFilters.ColumnQualifierExact("col0"),
                RowFilters.ColumnQualifierExact("col4")),
            RowFilters.CellsPerColumnLimit(1));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("cfc-00"), filter: filter);
        var cols = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().HaveCount(2);
    }

    [Fact]
    public async Task Chain_rowkey_then_interleave()
    {
        var filter = RowFilters.Chain(
            RowFilters.RowKeyRegex("cfc-00"),
            RowFilters.Interleave(
                RowFilters.ColumnQualifierExact("col0"),
                RowFilters.ColumnQualifierExact("col1"),
                RowFilters.ColumnQualifierExact("col2")));
        var rows = await ReadAll(filter: filter);
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns.Should().HaveCount(3);
    }

    #endregion

    #region Chain with condition

    [Fact]
    public async Task Chain_with_condition()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("col0"),
            RowFilters.Condition(
                predicateFilter: RowFilters.ValueRegex("r0c0v3"),
                trueFilter: RowFilters.PassAllFilter(),
                falseFilter: RowFilters.BlockAllFilter()));
        var rows = await ReadAll(filter: filter);
        // Only cfc-00 has value "r0c0v3" in col0
        rows.Should().ContainSingle().Which.Key.ToStringUtf8().Should().Be("cfc-00");
    }

    #endregion

    #region Chain with timestamp range

    [Fact]
    public async Task Chain_column_and_timestamp()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("col0"),
            new RowFilter
            {
                TimestampRangeFilter = new TimestampRange
                {
                    StartTimestampMicros = 2_000_000,
                    EndTimestampMicros = 3_000_000
                }
            });
        var rows = await ReadAll(rows: RowSet.FromRowKeys("cfc-00"), filter: filter);
        CellCount(rows).Should().Be(1); // Only v2 (2_000_000 micros)
    }

    #endregion

    #region Chain with row offset and limit

    [Fact]
    public async Task Chain_offset_and_limit()
    {
        var filter = RowFilters.Chain(
            RowFilters.CellsPerRowOffset(2),
            RowFilters.CellsPerRowLimit(3));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("cfc-00"), filter: filter);
        CellCount(rows).Should().Be(3);
    }

    [Fact]
    public async Task Chain_column_offset_limit()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameRegex(CF),
            RowFilters.CellsPerRowOffset(5),
            RowFilters.CellsPerRowLimit(5));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("cfc-00"), filter: filter);
        CellCount(rows).Should().Be(5);
    }

    #endregion

    #region Pass/Block in chain

    [Fact]
    public async Task Chain_passall_preserves_data()
    {
        var filter = RowFilters.Chain(
            RowFilters.PassAllFilter(),
            RowFilters.ColumnQualifierExact("col0"),
            RowFilters.CellsPerColumnLimit(1));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("cfc-00"), filter: filter);
        CellCount(rows).Should().Be(1);
    }

    [Fact]
    public async Task Chain_blockall_kills_pipeline()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("col0"),
            RowFilters.BlockAllFilter(),
            RowFilters.CellsPerColumnLimit(1));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("cfc-00"), filter: filter);
        rows.Should().BeEmpty();
    }

    #endregion

    #region Cross-family chain

    [Fact]
    public async Task Chain_cross_family_interleave()
    {
        var filter = RowFilters.Chain(
            RowFilters.Interleave(
                RowFilters.FamilyNameRegex(CF),
                RowFilters.FamilyNameRegex("cf2")),
            RowFilters.CellsPerRowLimit(2));
        var rows = await ReadAll(rows: RowSet.FromRowKeys("cfc-00"), filter: filter);
        CellCount(rows).Should().Be(2);
    }

    #endregion
}

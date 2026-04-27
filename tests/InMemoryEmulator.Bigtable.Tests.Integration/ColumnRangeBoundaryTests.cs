using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for ColumnRange filtering — bounded, half-open, and edge cases.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#columnrange
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ColumnRangeBoundaryTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public ColumnRangeBoundaryTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync("colrange", new[] { CF, CF2 });
        // Seed a row with columns a..z
        var mutations = new List<Mutation>();
        for (char c = 'a'; c <= 'z'; c++)
            mutations.Add(Mutations.SetCell(CF, c.ToString(), $"val-{c}", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "r1", mutations.ToArray());
    }
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("colrange");

    private async Task<List<string>> ReadColumns(RowFilter filter)
    {
        var cols = new List<string>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter))
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    cols.Add(col.Qualifier.ToStringUtf8());
        return cols;
    }

    #region Closed range

    [Fact]
    public async Task Closed_range_a_to_e_returns_5()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "e"));
        var cols = await ReadColumns(filter);
        cols.Should().HaveCount(5);
        cols.Should().Contain("a").And.Contain("e");
    }

    [Fact]
    public async Task Closed_range_m_to_p_returns_4()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.Closed(CF, "m", "p"));
        var cols = await ReadColumns(filter);
        cols.Should().HaveCount(4);
        cols.Should().Contain("m").And.Contain("p");
    }

    [Fact]
    public async Task Closed_range_x_to_z_returns_3()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.Closed(CF, "x", "z"));
        var cols = await ReadColumns(filter);
        cols.Should().HaveCount(3);
    }

    [Fact]
    public async Task Closed_range_single_column()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.Closed(CF, "k", "k"));
        var cols = await ReadColumns(filter);
        cols.Should().ContainSingle().Which.Should().Be("k");
    }

    #endregion

    #region Open range

    [Fact]
    public async Task Open_range_a_to_e_excludes_endpoints()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.Open(CF, "a", "e"));
        var cols = await ReadColumns(filter);
        cols.Should().HaveCount(3); // b, c, d
        cols.Should().NotContain("a").And.NotContain("e");
    }

    [Fact]
    public async Task Open_range_excludes_both_endpoints()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.Open(CF, "m", "q"));
        var cols = await ReadColumns(filter);
        cols.Should().HaveCount(3); // n, o, p
        cols.Should().NotContain("m").And.NotContain("q");
    }

    #endregion

    #region ClosedOpen range

    [Fact]
    public async Task ClosedOpen_includes_start_excludes_end()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.ClosedOpen(CF, "d", "h"));
        var cols = await ReadColumns(filter);
        cols.Should().HaveCount(4); // d, e, f, g
        cols.Should().Contain("d").And.NotContain("h");
    }

    [Fact]
    public async Task ClosedOpen_single_char_gap()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.ClosedOpen(CF, "a", "b"));
        var cols = await ReadColumns(filter);
        cols.Should().ContainSingle().Which.Should().Be("a");
    }

    #endregion

    #region OpenClosed range

    [Fact]
    public async Task OpenClosed_excludes_start_includes_end()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.OpenClosed(CF, "d", "h"));
        var cols = await ReadColumns(filter);
        cols.Should().HaveCount(4); // e, f, g, h
        cols.Should().NotContain("d").And.Contain("h");
    }

    [Fact]
    public async Task OpenClosed_single_char_gap()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.OpenClosed(CF, "a", "b"));
        var cols = await ReadColumns(filter);
        cols.Should().ContainSingle().Which.Should().Be("b");
    }

    #endregion

    #region No-match ranges

    [Fact]
    public async Task Open_range_same_endpoints_returns_empty()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.Open(CF, "k", "k"));
        var cols = await ReadColumns(filter);
        cols.Should().BeEmpty();
    }

    [Fact]
    public async Task Range_beyond_all_columns_returns_empty()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.Closed(CF, "0", "9"));
        var cols = await ReadColumns(filter);
        // '0'-'9' are less than 'a' in UTF-8 — maybe some columns in that range
        // Actually ASCII: 0=0x30..9=0x39, a=0x61..z=0x7a, so no intersection
        cols.Should().BeEmpty();
    }

    #endregion

    #region Combined with other filters

    [Fact]
    public async Task ColumnRange_with_value_filter()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "c")),
            RowFilters.ValueRegex("val-b"));
        var cols = await ReadColumns(filter);
        cols.Should().ContainSingle().Which.Should().Be("b");
    }

    [Fact]
    public async Task ColumnRange_with_CellsPerColumnLimit()
    {
        // Write a second version to column "a"
        await Client.MutateRowAsync(TN, "r1",
            Mutations.SetCell(CF, "a", "val-a-v2", new BigtableVersion(2000)));
        var filter = RowFilters.Chain(
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "a")),
            RowFilters.CellsPerColumnLimit(1));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter))
            rows.Add(row);
        rows.Should().ContainSingle();
        var cells = rows[0].Families.SelectMany(f => f.Columns.SelectMany(c => c.Cells)).ToList();
        cells.Should().ContainSingle(); // Only latest version
    }

    [Fact]
    public async Task ColumnRange_across_multiple_rows()
    {
        // Add another row with same columns
        var mutations = new List<Mutation>();
        for (char c = 'a'; c <= 'c'; c++)
            mutations.Add(Mutations.SetCell(CF, c.ToString(), $"r2-{c}", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "r2", mutations.ToArray());

        var filter = RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "b"));
        var allCols = new List<string>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter))
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    allCols.Add(col.Qualifier.ToStringUtf8());
        allCols.Should().HaveCountGreaterThanOrEqualTo(4); // 2 cols × 2 rows
    }

    [Fact]
    public async Task ColumnRange_wrong_family_returns_empty()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.Closed(CF2, "a", "z"));
        var cols = await ReadColumns(filter);
        cols.Should().BeEmpty();
    }

    #endregion

    #region Full range

    [Fact]
    public async Task Closed_range_a_to_z_returns_all_26()
    {
        var filter = RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "z"));
        var cols = await ReadColumns(filter);
        cols.Should().HaveCount(26);
    }

    [Fact]
    public async Task ClosedOpen_a_to_tilde_returns_all_26()
    {
        // '~' (0x7E) is after 'z' (0x7A) in ASCII
        var filter = RowFilters.ColumnRange(ColumnRange.ClosedOpen(CF, "a", "~"));
        var cols = await ReadColumns(filter);
        cols.Should().HaveCount(26);
    }

    #endregion
}

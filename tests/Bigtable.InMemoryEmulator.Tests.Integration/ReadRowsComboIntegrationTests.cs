using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Comprehensive ReadRows combination tests — filters combined with ranges,
/// limits, multiple families, versions, and pagination patterns.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsComboIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "combo-tests";
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public ReadRowsComboIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
        await SeedData();
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task SeedData()
    {
        // 20 rows with various data patterns
        for (int i = 1; i <= 20; i++)
        {
            var rk = $"combo-{i:D3}";
            // 3 versions per column
            for (int v = 1; v <= 3; v++)
            {
                await Client.MutateRowAsync(TN, rk,
                    Mutations.SetCell(CF, "status", i % 2 == 0 ? "even" : "odd",
                        new BigtableVersion(v * 1000)),
                    Mutations.SetCell(CF, "value", $"row{i}-v{v}",
                        new BigtableVersion(v * 1000)),
                    Mutations.SetCell(CF2, "extra", $"extra-{i}",
                        new BigtableVersion(v * 1000)));
            }
        }
    }

    #region Range + filter combos

    [Fact]
    public async Task Range_with_value_filter()
    {
        var rowSet = RowSet.FromRowRanges(RowRange.ClosedOpen("combo-001", "combo-006"));
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierRegex("status"),
            RowFilters.ValueRegex("even"),
            RowFilters.CellsPerColumnLimit(1));
        var rows = await ReadAll(rowSet, filter);
        // Rows 2, 4 are even within range [1,6)
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Range_with_family_filter()
    {
        var rowSet = RowSet.FromRowRanges(RowRange.ClosedOpen("combo-001", "combo-004"));
        var filter = RowFilters.FamilyNameRegex("cf2");
        var rows = await ReadAll(rowSet, filter);
        rows.Should().HaveCount(3); // rows 1-3
        foreach (var row in rows)
        {
            row.Families.Should().ContainSingle().Which.Name.Should().Be("cf2");
        }
    }

    [Fact]
    public async Task Range_with_column_filter()
    {
        var rowSet = RowSet.FromRowRanges(RowRange.ClosedOpen("combo-001", "combo-004"));
        var filter = RowFilters.ColumnQualifierRegex("value");
        var rows = await ReadAll(rowSet, filter);
        rows.Should().HaveCount(3);
        foreach (var row in rows)
        {
            row.Families[0].Columns.Should().ContainSingle()
                .Which.Qualifier.ToStringUtf8().Should().Be("value");
        }
    }

    #endregion

    #region Limit + filter combos

    [Fact]
    public async Task Limit_with_filter()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierRegex("status"),
            RowFilters.ValueRegex("even"),
            RowFilters.CellsPerColumnLimit(1));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, filter: filter, rowsLimit: 3))
        {
            rows.Add(row);
        }
        rows.Should().HaveCount(3);
        // These should be the first 3 even rows
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("combo-002", "combo-004", "combo-006");
    }

    [Fact]
    public async Task Limit_1_returns_first_matching()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierRegex("status"),
            RowFilters.ValueRegex("odd"),
            RowFilters.CellsPerColumnLimit(1));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, filter: filter, rowsLimit: 1))
        {
            rows.Add(row);
        }
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("combo-001");
    }

    [Fact]
    public async Task Limit_exceeds_total_returns_all()
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowsLimit: 1000))
        {
            rows.Add(row);
        }
        rows.Should().HaveCount(20);
    }

    #endregion

    #region Range + limit combos

    [Fact]
    public async Task Range_with_limit()
    {
        var rowSet = RowSet.FromRowRanges(RowRange.ClosedOpen("combo-001", "combo-011"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet, rowsLimit: 5))
        {
            rows.Add(row);
        }
        rows.Should().HaveCount(5);
        rows[0].Key.ToStringUtf8().Should().Be("combo-001");
    }

    [Fact]
    public async Task Multiple_ranges_with_limit()
    {
        var rowSet = RowSet.FromRowRanges(
            RowRange.ClosedOpen("combo-001", "combo-004"),
            RowRange.ClosedOpen("combo-010", "combo-013"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet, rowsLimit: 4))
        {
            rows.Add(row);
        }
        rows.Should().HaveCount(4);
    }

    #endregion

    #region CellsPerColumnLimit with multi-version

    [Fact]
    public async Task CellsPerColumnLimit_1_returns_latest_version()
    {
        var filter = RowFilters.CellsPerColumnLimit(1);
        var rows = await ReadAll(RowSet.FromRowKeys("combo-001"), filter);
        rows.Should().ContainSingle();
        foreach (var col in rows[0].Families.SelectMany(f => f.Columns))
        {
            col.Cells.Should().ContainSingle();
        }
    }

    [Fact]
    public async Task CellsPerColumnLimit_2_returns_two_newest()
    {
        var filter = RowFilters.CellsPerColumnLimit(2);
        var rows = await ReadAll(RowSet.FromRowKeys("combo-001"), filter);
        rows.Should().ContainSingle();
        foreach (var col in rows[0].Families.SelectMany(f => f.Columns))
        {
            col.Cells.Should().HaveCount(2);
            col.Cells[0].TimestampMicros.Should().BeGreaterThan(col.Cells[1].TimestampMicros);
        }
    }

    [Fact]
    public async Task CellsPerColumnLimit_exceeds_version_count_returns_all()
    {
        var filter = RowFilters.CellsPerColumnLimit(100);
        var rows = await ReadAll(RowSet.FromRowKeys("combo-001"), filter);
        rows.Should().ContainSingle();
        foreach (var col in rows[0].Families.SelectMany(f => f.Columns))
        {
            col.Cells.Should().HaveCount(3); // all 3 versions
        }
    }

    #endregion

    #region Multiple keys + filter

    [Fact]
    public async Task Specific_keys_with_column_filter()
    {
        var rowSet = RowSet.FromRowKeys("combo-001", "combo-010", "combo-020");
        var filter = RowFilters.ColumnQualifierRegex("status");
        var rows = await ReadAll(rowSet, filter);
        rows.Should().HaveCount(3);
        foreach (var row in rows)
        {
            row.Families.First(f => f.Name == "cf").Columns.Should().ContainSingle()
                .Which.Qualifier.ToStringUtf8().Should().Be("status");
        }
    }

    [Fact]
    public async Task Specific_keys_with_strip_value()
    {
        var rowSet = RowSet.FromRowKeys("combo-001", "combo-002");
        var filter = RowFilters.StripValueTransformer();
        var rows = await ReadAll(rowSet, filter);
        rows.Should().HaveCount(2);
        foreach (var cell in rows.SelectMany(r => r.Families.SelectMany(f => f.Columns.SelectMany(c => c.Cells))))
        {
            cell.Value.Should().BeEmpty();
        }
    }

    #endregion

    #region Paginated reads

    [Fact]
    public async Task Pagination_with_limit()
    {
        // Page 1: first 5
        var rows1 = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowsLimit: 5))
        {
            rows1.Add(row);
        }
        rows1.Should().HaveCount(5);

        // Page 2: next 5 starting after last key
        var lastKey = rows1.Last().Key.ToStringUtf8();
        var nextStart = lastKey + "\0"; // next key after lastKey
        var rowSet = RowSet.FromRowRanges(RowRange.Open(lastKey, ""));
        var rows2 = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet, rowsLimit: 5))
        {
            rows2.Add(row);
        }
        rows2.Should().HaveCount(5);
        rows2[0].Key.ToStringUtf8().Should().NotBe(lastKey); // no overlap
    }

    [Fact]
    public async Task Paginated_read_covers_all_rows()
    {
        var allKeys = new List<string>();
        string? lastKey = null;
        while (true)
        {
            RowSet? rowSet = lastKey != null
                ? RowSet.FromRowRanges(RowRange.Open(lastKey, ""))
                : null;
            var batch = new List<Row>();
            await foreach (var row in Client.ReadRows(TN, rowSet, rowsLimit: 7))
            {
                batch.Add(row);
            }
            if (batch.Count == 0) break;
            allKeys.AddRange(batch.Select(r => r.Key.ToStringUtf8()));
            lastKey = batch.Last().Key.ToStringUtf8();
        }
        allKeys.Should().HaveCount(20);
        allKeys.Should().OnlyHaveUniqueItems();
    }

    #endregion

    #region Empty/null results

    [Fact]
    public async Task ReadRows_empty_range_returns_empty()
    {
        var rowSet = RowSet.FromRowRanges(RowRange.ClosedOpen("zzz", "zzz0"));
        var rows = await ReadAll(rowSet);
        rows.Should().BeEmpty();
    }

    // Go emulator divergence: throws InvalidArgument for inverted range (start > end) instead of returning empty.
    // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.RowRange
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task ReadRows_inverted_range_returns_empty()
    {
        // Start > end → empty result
        var rowSet = RowSet.FromRowRanges(RowRange.ClosedOpen("combo-010", "combo-005"));
        var rows = await ReadAll(rowSet);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadRows_filter_matches_nothing()
    {
        var filter = RowFilters.ValueRegex("NEVER_MATCHES_ANYTHING_xyz");
        var rows = await ReadAll(filter: filter);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadRow_nonexistent_returns_null()
    {
        var row = await Client.ReadRowAsync(TN, "nonexistent-row-xyz");
        row.Should().BeNull();
    }

    #endregion

    #region Read full table

    [Fact]
    public async Task ReadRows_no_filter_returns_all()
    {
        var rows = await ReadAll();
        rows.Should().HaveCount(20);
    }

    [Fact]
    public async Task ReadRows_all_in_lexicographic_order()
    {
        var rows = await ReadAll();
        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().BeInAscendingOrder();
    }

    #endregion

    #region Helpers

    private async Task<List<Row>> ReadAll(RowSet? rowSet = null, RowFilter? filter = null)
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet, filter: filter))
        {
            rows.Add(row);
        }
        return rows;
    }

    #endregion
}

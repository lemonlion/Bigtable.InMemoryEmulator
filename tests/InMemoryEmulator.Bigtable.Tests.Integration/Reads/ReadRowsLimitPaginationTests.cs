using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsLimitPaginationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "rr-lim-pg";
    private const string CF = "cf";

    public ReadRowsLimitPaginationTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        for (int i = 0; i < 50; i++)
            await Client.MutateRowAsync(TN, $"row-{i:D3}", Mutations.SetCell(CF, "v", $"{i}"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Limit_1_returns_first_row()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rowsLimit: 1)) rows.Add(r);
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("row-000");
    }

    [Fact]
    public async Task Limit_5_returns_5_rows()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rowsLimit: 5)) rows.Add(r);
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task Limit_larger_than_total_returns_all()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rowsLimit: 100)) rows.Add(r);
        rows.Should().HaveCount(50);
    }

    [Fact]
    public async Task Rows_returned_in_lexicographic_order()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rowsLimit: 10)) rows.Add(r);
        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Limit_with_row_range()
    {
        var rowSet = new RowSet { RowRanges = { RowRange.ClosedOpen("row-010", "row-030") } };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet, rowsLimit: 5)) rows.Add(r);
        rows.Should().HaveCount(5);
        rows[0].Key.ToStringUtf8().Should().Be("row-010");
    }

    [Fact]
    public async Task Limit_with_filter()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex("row-00.*"), rowsLimit: 3))
            rows.Add(r);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Manual_pagination_with_ranges()
    {
        // Page 1: first 10
        var page1 = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rowsLimit: 10)) page1.Add(r);
        page1.Should().HaveCount(10);

        // Page 2: start after last key of page 1
        var lastKey = page1.Last().Key;
        var rowSet = new RowSet { RowRanges = { new RowRange { StartKeyOpen = lastKey } } };
        var page2 = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet, rowsLimit: 10)) page2.Add(r);
        page2.Should().HaveCount(10);
        page2[0].Key.ToStringUtf8().Should().Be("row-010");
    }

    [Fact]
    public async Task Limit_0_means_no_limit()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rowsLimit: 0)) rows.Add(r);
        rows.Should().HaveCount(50);
    }

    [Fact]
    public async Task Limit_with_specific_row_keys()
    {
        var rowSet = RowSet.FromRowKeys("row-005", "row-010", "row-015", "row-020", "row-025");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet, rowsLimit: 3)) rows.Add(r);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Limit_with_block_all_filter()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.BlockAllFilter(), rowsLimit: 10))
            rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Multiple_row_ranges_with_limit()
    {
        var rowSet = new RowSet
        {
            RowRanges =
            {
                RowRange.ClosedOpen("row-000", "row-005"),
                RowRange.ClosedOpen("row-020", "row-025"),
            }
        };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet, rowsLimit: 7))
            rows.Add(r);
        rows.Should().HaveCount(7);
    }

    [Fact]
    public async Task Limit_with_cells_per_row()
    {
        // Add extra columns to some rows
        await Client.MutateRowAsync(TN, "row-000",
            Mutations.SetCell(CF, "extra1", "e1"),
            Mutations.SetCell(CF, "extra2", "e2"));
        var rows = new List<Row>();
        var filter = RowFilters.CellsPerRowLimit(1);
        await foreach (var r in Client.ReadRows(TN, filter: filter, rowsLimit: 3))
            rows.Add(r);
        rows.Should().HaveCount(3);
        foreach (var row in rows)
            row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().ContainSingle();
    }

    [Fact]
    public async Task Read_all_rows_no_limit()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN)) rows.Add(r);
        rows.Should().HaveCount(50);
    }

    [Fact]
    public async Task Full_scan_keys_are_sorted()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN)) rows.Add(r);
        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().BeInAscendingOrder();
        keys.Should().OnlyHaveUniqueItems();
    }
}

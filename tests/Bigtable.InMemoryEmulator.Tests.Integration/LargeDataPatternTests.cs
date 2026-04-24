using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for large datasets: many rows, many columns, many versions.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class LargeDataPatternTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "large-data";
    private const string CF = "cf";

    public LargeDataPatternTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
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

    #region Many rows

    [Fact]
    public async Task Write_and_read_200_rows()
    {
        var entries = Enumerable.Range(0, 200).Select(i =>
            Mutations.CreateEntry($"ld-200-{i:D3}", Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ld-200-", "ld-200~")));
        rows.Should().HaveCount(200);
    }

    [Fact]
    public async Task Read_200_rows_in_order()
    {
        // Re-read what the previous test might have seeded, or seed fresh
        var existing = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ld-ord-", "ld-ord~")));
        if (existing.Count == 0)
        {
            var entries = Enumerable.Range(0, 200).Select(i =>
                Mutations.CreateEntry($"ld-ord-{i:D3}",
                    Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))
            ).ToArray();
            await Client.MutateRowsAsync(TN, entries);
        }
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ld-ord-", "ld-ord~")));
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Read_200_rows_with_limit()
    {
        var entries = Enumerable.Range(0, 200).Select(i =>
            Mutations.CreateEntry($"ld-lim-{i:D3}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ld-lim-", "ld-lim~")), limit: 50);
        rows.Should().HaveCount(50);
    }

    [Fact]
    public async Task Read_200_rows_with_filter()
    {
        var entries = Enumerable.Range(0, 200).Select(i =>
            Mutations.CreateEntry($"ld-filt-{i:D3}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("ld-filt-", "ld-filt~")),
            RowFilters.RowKeyRegex("ld-filt-0.*"));
        // Rows 000-099 match "ld-filt-0.*"
        rows.Should().HaveCount(100);
    }

    #endregion

    #region Many columns per row

    [Fact]
    public async Task Row_with_50_columns()
    {
        var mutations = Enumerable.Range(0, 50).Select(i =>
            Mutations.SetCell(CF, $"col-{i:D3}", $"val-{i}", new BigtableVersion(1000))
        ).ToArray();
        await Client.MutateRowAsync(TN, "ld-50col", mutations);
        var rows = await ReadAll(RowSet.FromRowKeys("ld-50col"));
        rows[0].Families[0].Columns.Should().HaveCount(50);
    }

    [Fact]
    public async Task Row_with_100_columns()
    {
        var mutations = Enumerable.Range(0, 100).Select(i =>
            Mutations.SetCell(CF, $"col-{i:D3}", $"val-{i}", new BigtableVersion(1000))
        ).ToArray();
        await Client.MutateRowAsync(TN, "ld-100col", mutations);
        var rows = await ReadAll(RowSet.FromRowKeys("ld-100col"));
        rows[0].Families[0].Columns.Should().HaveCount(100);
    }

    [Fact]
    public async Task Filter_many_columns()
    {
        var mutations = Enumerable.Range(0, 50).Select(i =>
            Mutations.SetCell(CF, $"col-{i:D3}", $"val-{i}", new BigtableVersion(1000))
        ).ToArray();
        await Client.MutateRowAsync(TN, "ld-fcol", mutations);
        var rows = await ReadAll(RowSet.FromRowKeys("ld-fcol"),
            RowFilters.ColumnQualifierRegex("col-00[0-9]"));
        rows[0].Families[0].Columns.Should().HaveCount(10);
    }

    [Fact]
    public async Task Column_range_on_many_columns()
    {
        var mutations = Enumerable.Range(0, 50).Select(i =>
            Mutations.SetCell(CF, $"col-{i:D3}", $"val-{i}", new BigtableVersion(1000))
        ).ToArray();
        await Client.MutateRowAsync(TN, "ld-crcol", mutations);
        var rows = await ReadAll(RowSet.FromRowKeys("ld-crcol"),
            RowFilters.ColumnRange(ColumnRange.ClosedOpen(CF, "col-010", "col-020")));
        rows[0].Families[0].Columns.Should().HaveCount(10);
    }

    #endregion

    #region Many versions per cell

    [Fact]
    public async Task Cell_with_20_versions()
    {
        for (int v = 1; v <= 20; v++)
            await Client.MutateRowAsync(TN, "ld-20ver",
                Mutations.SetCell(CF, "c", $"v{v}", new BigtableVersion(v * 1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("ld-20ver"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(20);
    }

    [Fact]
    public async Task Cell_with_20_versions_limit_5()
    {
        for (int v = 1; v <= 20; v++)
            await Client.MutateRowAsync(TN, "ld-20vl",
                Mutations.SetCell(CF, "c", $"v{v}", new BigtableVersion(v * 1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("ld-20vl"), RowFilters.CellsPerColumnLimit(5));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(5);
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v20"); // latest
    }

    [Fact]
    public async Task Cell_with_20_versions_timestamp_range()
    {
        for (int v = 1; v <= 20; v++)
            await Client.MutateRowAsync(TN, "ld-20vt",
                Mutations.SetCell(CF, "c", $"v{v}", new BigtableVersion(v * 1000)));
        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                StartTimestampMicros = 5_000_000,
                EndTimestampMicros = 10_000_000
            }
        };
        var rows = await ReadAll(RowSet.FromRowKeys("ld-20vt"), filter);
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(5); // versions 5..9
    }

    #endregion

    #region Batch patterns

    [Fact]
    public async Task Multiple_batches_of_100()
    {
        for (int batch = 0; batch < 3; batch++)
        {
            var entries = Enumerable.Range(0, 100).Select(i =>
                Mutations.CreateEntry($"ld-mb-{batch}-{i:D3}",
                    Mutations.SetCell(CF, "c", $"b{batch}v{i}", new BigtableVersion(1000)))
            ).ToArray();
            await Client.MutateRowsAsync(TN, entries);
        }
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ld-mb-", "ld-mb~")));
        rows.Should().HaveCount(300);
    }

    [Fact]
    public async Task Batch_with_multi_column_entries()
    {
        var entries = Enumerable.Range(0, 50).Select(i =>
            Mutations.CreateEntry($"ld-bmc-{i:D3}",
                Mutations.SetCell(CF, "a", $"a{i}", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "b", $"b{i}", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "c", $"c{i}", new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ld-bmc-", "ld-bmc~")));
        rows.Should().HaveCount(50);
        foreach (var row in rows)
            row.Families[0].Columns.Should().HaveCount(3);
    }

    #endregion

    #region Mixed large data patterns

    [Fact]
    public async Task Many_rows_many_columns()
    {
        var entries = Enumerable.Range(0, 20).Select(r =>
        {
            var mutations = Enumerable.Range(0, 10).Select(c =>
                Mutations.SetCell(CF, $"col-{c}", $"r{r}c{c}", new BigtableVersion(1000))
            ).ToArray();
            return Mutations.CreateEntry($"ld-mr-mc-{r:D3}", mutations);
        }).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ld-mr-mc-", "ld-mr-mc~")));
        rows.Should().HaveCount(20);
        foreach (var row in rows)
            row.Families[0].Columns.Should().HaveCount(10);
    }

    [Fact]
    public async Task Many_rows_with_versions()
    {
        for (int r = 0; r < 20; r++)
            for (int v = 1; v <= 5; v++)
                await Client.MutateRowAsync(TN, $"ld-mr-v-{r:D3}",
                    Mutations.SetCell(CF, "c", $"r{r}v{v}", new BigtableVersion(v * 1000)));
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ld-mr-v-", "ld-mr-v~")));
        rows.Should().HaveCount(20);
        foreach (var row in rows)
            row.Families[0].Columns[0].Cells.Should().HaveCount(5);
    }

    [Fact]
    public async Task Filter_on_large_dataset()
    {
        var entries = Enumerable.Range(0, 100).Select(i =>
            Mutations.CreateEntry($"ld-fld-{i:D3}",
                Mutations.SetCell(CF, "type", i % 2 == 0 ? "even" : "odd", new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("ld-fld-", "ld-fld~")),
            RowFilters.ValueExact("even"));
        rows.Should().HaveCount(50);
    }

    [Fact]
    public async Task Delete_half_of_large_dataset()
    {
        var entries = Enumerable.Range(0, 50).Select(i =>
            Mutations.CreateEntry($"ld-del-{i:D3}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        // Delete odd rows
        var delEntries = Enumerable.Range(0, 50).Where(i => i % 2 != 0).Select(i =>
            Mutations.CreateEntry($"ld-del-{i:D3}", Mutations.DeleteFromRow())
        ).ToArray();
        await Client.MutateRowsAsync(TN, delEntries);
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ld-del-", "ld-del~")));
        rows.Should().HaveCount(25);
    }

    #endregion

    #region Scan with CellsPerRowLimit

    [Fact]
    public async Task CellsPerRowLimit_on_wide_rows()
    {
        var mutations = Enumerable.Range(0, 20).Select(i =>
            Mutations.SetCell(CF, $"col-{i:D3}", $"v{i}", new BigtableVersion(1000))
        ).ToArray();
        await Client.MutateRowAsync(TN, "ld-crl", mutations);
        var rows = await ReadAll(RowSet.FromRowKeys("ld-crl"), RowFilters.CellsPerRowLimit(5));
        rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count().Should().Be(5);
    }

    [Fact]
    public async Task CellsPerRowOffset_on_wide_rows()
    {
        var mutations = Enumerable.Range(0, 20).Select(i =>
            Mutations.SetCell(CF, $"col-{i:D3}", $"v{i}", new BigtableVersion(1000))
        ).ToArray();
        await Client.MutateRowAsync(TN, "ld-cro", mutations);
        var rows = await ReadAll(RowSet.FromRowKeys("ld-cro"), RowFilters.CellsPerRowOffset(15));
        rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count().Should().Be(5);
    }

    #endregion
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Large-scale scan tests: reading 1000+ rows with various filters and patterns.
///
/// Ref: https://cloud.google.com/bigtable/docs/reading-data
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class LargeScanTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const int TOTAL_ROWS = 1000;

    public LargeScanTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync("large-scan", new[] { CF, "cf2" });
        var tn = _fixture.GetTableName("large-scan");

        // Batch insert 1000 rows in chunks
        for (int batch = 0; batch < 10; batch++)
        {
            var entries = Enumerable.Range(batch * 100, 100).Select(i =>
                Mutations.CreateEntry($"ls-{i:D5}",
                    Mutations.SetCell(CF, "val", $"data-{i}", new BigtableVersion(1000)),
                    Mutations.SetCell(CF, "idx", $"{i}", new BigtableVersion(1000)),
                    Mutations.SetCell("cf2", "tag", i % 2 == 0 ? "even" : "odd", new BigtableVersion(1000))
                )).ToArray();
            await _fixture.Client.MutateRowsAsync(tn, entries);
        }
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("large-scan");

    [Fact]
    public async Task Full_scan_returns_all_1000_rows()
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN))
            rows.Add(row);
        rows.Should().HaveCount(TOTAL_ROWS);
    }

    [Fact]
    public async Task Full_scan_with_cells_per_column_limit()
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, RowFilters.CellsPerColumnLimit(1)))
            rows.Add(row);
        rows.Should().HaveCount(TOTAL_ROWS);
    }

    [Fact]
    public async Task Full_scan_with_family_filter()
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, RowFilters.FamilyNameExact(CF)))
            rows.Add(row);
        rows.Should().HaveCount(TOTAL_ROWS);
        foreach (var row in rows.Take(5))
            row.Families.Should().ContainSingle().Which.Name.Should().Be(CF);
    }

    [Fact]
    public async Task Scan_with_value_regex_filter()
    {
        // Match even-indexed rows via cf2/tag
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact("cf2"),
            RowFilters.ValueRegex("even"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter))
            rows.Add(row);
        rows.Should().HaveCount(500);
    }

    [Fact]
    public async Task Scan_with_row_key_regex()
    {
        // Match rows ls-00000 to ls-00099
        var filter = RowFilters.RowKeyRegex("ls-000[0-9]{2}");
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter))
            rows.Add(row);
        rows.Should().HaveCount(100);
    }

    [Fact]
    public async Task Scan_with_limit_500()
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter: null, rowsLimit: 500))
            rows.Add(row);
        rows.Should().HaveCount(500);
    }

    [Fact]
    public async Task Scan_range_middle_500()
    {
        var rowSet = new RowSet
        {
            RowRanges =
            {
                RowRange.ClosedOpen("ls-00250", "ls-00750")
            }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);
        rows.Should().HaveCount(500);
    }

    [Fact]
    public async Task Scan_specific_100_keys()
    {
        var keys = Enumerable.Range(0, 100)
            .Select(i => new BigtableByteString($"ls-{i * 10:D5}"))
            .ToList();
        var rowSet = RowSet.FromRowKeys(keys);
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);
        rows.Should().HaveCount(100);
    }

    [Fact]
    public async Task Scan_with_complex_filter_chain()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.ColumnQualifierExact("val"),
            RowFilters.CellsPerColumnLimit(1),
            RowFilters.ValueRegex("data-[0-9]{1,2}$"));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter))
            rows.Add(row);
        // Matches data-0 through data-99 (single and double digit suffixes only when they end the string)
        rows.Should().HaveCountGreaterThan(0);
    }

    [Fact]
    public async Task Scan_sorted_lexicographically()
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter: null, rowsLimit: 100))
            rows.Add(row);

        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Multiple_range_scan()
    {
        var rowSet = new RowSet
        {
            RowRanges =
            {
                RowRange.ClosedOpen("ls-00000", "ls-00100"),
                RowRange.ClosedOpen("ls-00500", "ls-00600"),
                RowRange.ClosedOpen("ls-00900", "ls-01000")
            }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);
        rows.Should().HaveCount(300);
    }

    [Fact]
    public async Task Interleave_filter_on_large_dataset()
    {
        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("val")),
            RowFilters.Chain(RowFilters.FamilyNameExact("cf2"), RowFilters.ColumnQualifierExact("tag")));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter, rowsLimit: 100))
            rows.Add(row);
        rows.Should().HaveCount(100);
        // Each row should have columns from both families
        foreach (var row in rows)
        {
            var families = row.Families.Select(f => f.Name).ToList();
            families.Should().Contain(CF);
            families.Should().Contain("cf2");
        }
    }

    [Fact]
    public async Task Strip_values_on_large_scan()
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null,
            RowFilters.StripValueTransformer(), rowsLimit: 100))
            rows.Add(row);
        rows.Should().HaveCount(100);
        foreach (var row in rows)
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    foreach (var cell in col.Cells)
                        cell.Value.Should().BeEmpty();
    }
}

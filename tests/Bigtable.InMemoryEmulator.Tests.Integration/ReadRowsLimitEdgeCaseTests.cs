using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for ReadRows limit edge cases: zero limit, limit=1, limit larger than total, scan with limit.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
///   "rows_limit: The maximum number of rows to return. 0 means no limit."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsLimitEdgeCaseTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public ReadRowsLimitEdgeCaseTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync("limit-ec", new[] { CF });
        var tn = _fixture.GetTableName("limit-ec");
        var entries = Enumerable.Range(0, 50).Select(i =>
            Mutations.CreateEntry($"lim-{i:D4}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))).ToArray();
        await _fixture.Client.MutateRowsAsync(tn, entries);
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("limit-ec");

    [Fact]
    public async Task Limit_0_returns_all_rows()
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter: null, rowsLimit: 0))
            rows.Add(row);
        rows.Should().HaveCount(50);
    }

    [Fact]
    public async Task Limit_1_returns_single_row()
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter: null, rowsLimit: 1))
            rows.Add(row);
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("lim-0000");
    }

    [Fact]
    public async Task Limit_larger_than_total_returns_all()
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter: null, rowsLimit: 1000))
            rows.Add(row);
        rows.Should().HaveCount(50);
    }

    [Fact]
    public async Task Limit_with_row_range()
    {
        var rowSet = new RowSet
        {
            RowRanges =
            {
                RowRange.ClosedOpen(
                    ByteString.CopyFromUtf8("lim-0010"),
                    ByteString.CopyFromUtf8("lim-0040"))
            }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet, filter: null, rowsLimit: 5))
            rows.Add(row);
        rows.Should().HaveCount(5);
        rows[0].Key.ToStringUtf8().Should().Be("lim-0010");
        rows[4].Key.ToStringUtf8().Should().Be("lim-0014");
    }

    [Fact]
    public async Task Limit_with_specific_row_keys()
    {
        var rowSet = RowSet.FromRowKeys("lim-0005", "lim-0015", "lim-0025", "lim-0035", "lim-0045");
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet, filter: null, rowsLimit: 3))
            rows.Add(row);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Limit_with_value_filter_applied_first()
    {
        // Value filter reduces rows, then limit applies to results
        var filter = RowFilters.ValueRegex("v1[0-9]");
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter, rowsLimit: 3))
            rows.Add(row);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Limit_exact_count()
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter: null, rowsLimit: 50))
            rows.Add(row);
        rows.Should().HaveCount(50);
    }

    [Fact]
    public async Task Limit_on_empty_table()
    {
        await _fixture.CreateTableAsync("limit-empty", new[] { CF });
        var emptyTn = _fixture.GetTableName("limit-empty");
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(emptyTn, rows: null, filter: null, rowsLimit: 10))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Limit_with_cells_per_column()
    {
        // Write multiple versions then limit
        var tn2 = _fixture.GetTableName("limit-ec");
        await Client.MutateRowAsync(tn2, "lim-0001",
            Mutations.SetCell(CF, "c", "extra", new BigtableVersion(2000)));

        var filter = RowFilters.CellsPerColumnLimit(1);
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter, rowsLimit: 5))
            rows.Add(row);
        rows.Should().HaveCount(5);
        // Each row should have only 1 cell per column
        foreach (var row in rows)
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    col.Cells.Should().ContainSingle();
    }

    [Fact]
    public async Task Multiple_row_ranges_with_limit()
    {
        var rowSet = new RowSet
        {
            RowRanges =
            {
                RowRange.ClosedOpen(
                    ByteString.CopyFromUtf8("lim-0000"),
                    ByteString.CopyFromUtf8("lim-0010")),
                RowRange.ClosedOpen(
                    ByteString.CopyFromUtf8("lim-0020"),
                    ByteString.CopyFromUtf8("lim-0030"))
            }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet, filter: null, rowsLimit: 15))
            rows.Add(row);
        rows.Should().HaveCount(15);
    }
}

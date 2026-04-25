using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for ReadRows with RowsLimit and interactions with filters and ranges.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
///   "rows_limit: The read will return no more than the given number of rows, scanned in the order of row key."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsLimitInteractionTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "rrl-inter";
    private const int RowCount = 50;

    public ReadRowsLimitInteractionTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        var entries = Enumerable.Range(0, RowCount).Select(i =>
            Mutations.CreateEntry($"rl-{i:D4}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "c", $"v{i}-old", new BigtableVersion(500)))).ToArray();
        await _fixture.Client.MutateRowsAsync(TN, entries);
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private BigtableServiceApiClient Api => _fixture.ServiceApiClient;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<int> CountRows(ReadRowsRequest request)
    {
        var count = 0;
        string? currentKey = null;
        var stream = Api.ReadRows(request);
        await foreach (var resp in stream.GetResponseStream())
            foreach (var chunk in resp.Chunks)
            {
                if (chunk.RowKey != null && !chunk.RowKey.IsEmpty)
                    currentKey = chunk.RowKey.ToStringUtf8();
                if (chunk.CommitRow && currentKey != null)
                {
                    count++;
                    currentKey = null;
                }
            }
        return count;
    }

    #region Basic limits

    [Fact]
    public async Task Limit_0_returns_all()
    {
        // Ref: A limit of 0 means no limit
        var count = await CountRows(new ReadRowsRequest { TableNameAsTableName = TN, RowsLimit = 0 });
        count.Should().Be(RowCount);
    }

    [Fact]
    public async Task Limit_1_returns_first()
    {
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(TN, filter: null, rowsLimit: 1))
            keys.Add(row.Key.ToStringUtf8());
        keys.Should().ContainSingle().Which.Should().Be("rl-0000");
    }

    [Fact]
    public async Task Limit_10_returns_10()
    {
        var count = 0;
        await foreach (var _ in Client.ReadRows(TN, rows: null, filter: null, rowsLimit: 10))
            count++;
        count.Should().Be(10);
    }

    [Fact]
    public async Task Limit_exceeding_total_returns_all()
    {
        var count = 0;
        await foreach (var _ in Client.ReadRows(TN, rows: null, filter: null, rowsLimit: 1000))
            count++;
        count.Should().Be(RowCount);
    }

    #endregion

    #region Limit with ranges

    [Fact]
    public async Task Limit_with_closed_range()
    {
        var count = 0;
        await foreach (var _ in Client.ReadRows(TN,
            RowSet.FromRowRanges(RowRange.Closed("rl-0010", "rl-0030")),
            filter: null, rowsLimit: 5))
            count++;
        count.Should().Be(5);
    }

    [Fact]
    public async Task Limit_larger_than_range()
    {
        var count = 0;
        await foreach (var _ in Client.ReadRows(TN,
            RowSet.FromRowRanges(RowRange.Closed("rl-0010", "rl-0012")),
            filter: null, rowsLimit: 100))
            count++;
        count.Should().Be(3);
    }

    [Fact]
    public async Task Limit_with_multiple_ranges()
    {
        var count = 0;
        await foreach (var _ in Client.ReadRows(TN,
            RowSet.FromRowRanges(
                RowRange.Closed("rl-0000", "rl-0009"),
                RowRange.Closed("rl-0040", "rl-0049")),
            filter: null, rowsLimit: 5))
            count++;
        count.Should().Be(5);
    }

    #endregion

    #region Limit with filters

    [Fact]
    public async Task Limit_with_value_regex()
    {
        // Read with filter that matches specific rows + limit
        var count = 0;
        await foreach (var _ in Client.ReadRows(TN,
            rows: null,
            filter: RowFilters.Chain(
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueRegex("v[0-9]")),
            rowsLimit: 5))
            count++;
        count.Should().Be(5);
    }

    [Fact]
    public async Task Limit_with_cells_per_column()
    {
        var cellCount = 0;
        await foreach (var row in Client.ReadRows(TN,
            rows: null,
            filter: RowFilters.CellsPerColumnLimit(1),
            rowsLimit: 3))
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    cellCount += col.Cells.Count;
        cellCount.Should().Be(3); // 1 cell per row × 3 rows
    }

    [Fact]
    public async Task Limit_with_row_key_regex()
    {
        var count = 0;
        await foreach (var _ in Client.ReadRows(TN,
            rows: null,
            filter: RowFilters.RowKeyRegex("rl-00[0-1]."),
            rowsLimit: 5))
            count++;
        count.Should().Be(5);
    }

    #endregion

    #region Limit with specific keys

    [Fact]
    public async Task Limit_with_row_keys()
    {
        var rowSet = RowSet.FromRowKeys("rl-0001", "rl-0005", "rl-0010", "rl-0020", "rl-0030");
        var count = 0;
        await foreach (var _ in Client.ReadRows(TN, rowSet, filter: null, rowsLimit: 3))
            count++;
        count.Should().Be(3);
    }

    [Fact]
    public async Task Limit_1_with_many_keys()
    {
        var rowSet = RowSet.FromRowKeys("rl-0001", "rl-0002", "rl-0003");
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(TN, rowSet, filter: null, rowsLimit: 1))
            keys.Add(row.Key.ToStringUtf8());
        keys.Should().ContainSingle().Which.Should().Be("rl-0001");
    }

    #endregion

    #region Limit with reversed

    [Fact]
    public async Task Reversed_limit_3()
    {
        var count = await CountRows(new ReadRowsRequest
        {
            TableNameAsTableName = TN, Reversed = true, RowsLimit = 3
        });
        count.Should().Be(3);
    }

    [Fact]
    public async Task Reversed_limit_with_range()
    {
        var count = await CountRows(new ReadRowsRequest
        {
            TableNameAsTableName = TN, Reversed = true, RowsLimit = 5,
            Rows = new RowSet { RowRanges = { new RowRange
            {
                StartKeyClosed = ByteString.CopyFromUtf8("rl-0010"),
                EndKeyClosed = ByteString.CopyFromUtf8("rl-0040")
            }}}
        });
        count.Should().Be(5);
    }

    #endregion

    #region Consecutive reads with limits (pagination pattern)

    [Fact]
    public async Task Manual_pagination_pattern()
    {
        var allKeys = new List<string>();
        string? lastKey = null;
        for (int page = 0; page < 5; page++)
        {
            var rowSet = lastKey != null
                ? RowSet.FromRowRanges(RowRange.Open(lastKey, "rl-9999"))
                : null;
            var pageKeys = new List<string>();
            await foreach (var row in Client.ReadRows(TN, rowSet, filter: null, rowsLimit: 10))
            {
                pageKeys.Add(row.Key.ToStringUtf8());
                lastKey = row.Key.ToStringUtf8();
            }
            allKeys.AddRange(pageKeys);
            if (pageKeys.Count < 10) break;
        }
        allKeys.Should().HaveCount(RowCount);
        allKeys.Should().OnlyHaveUniqueItems();
    }

    #endregion
}

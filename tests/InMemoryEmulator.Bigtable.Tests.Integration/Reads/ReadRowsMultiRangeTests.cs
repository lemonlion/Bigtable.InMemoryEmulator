using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsMultiRangeTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "rr-mrange";
    private const string CF = "cf";

    public ReadRowsMultiRangeTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        for (int i = 0; i < 100; i++)
            await Client.MutateRowAsync(TN, $"row-{i:D3}", Mutations.SetCell(CF, "v", $"{i}"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Two_disjoint_ranges()
    {
        var rowSet = new RowSet
        {
            RowRanges = {
                RowRange.ClosedOpen("row-000", "row-005"),
                RowRange.ClosedOpen("row-050", "row-055"),
            }
        };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task Three_ranges()
    {
        var rowSet = new RowSet
        {
            RowRanges = {
                RowRange.ClosedOpen("row-000", "row-003"),
                RowRange.ClosedOpen("row-010", "row-013"),
                RowRange.ClosedOpen("row-090", "row-093"),
            }
        };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Should().HaveCount(9);
    }

    [Fact]
    public async Task Overlapping_ranges_no_duplicates()
    {
        var rowSet = new RowSet
        {
            RowRanges = {
                RowRange.ClosedOpen("row-005", "row-015"),
                RowRange.ClosedOpen("row-010", "row-020"),
            }
        };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().OnlyHaveUniqueItems();
        keys.Should().HaveCount(15); // row-005..row-019
    }

    [Fact]
    public async Task Range_plus_specific_keys()
    {
        var rowSet = new RowSet
        {
            RowRanges = { RowRange.ClosedOpen("row-000", "row-003") },
            RowKeys = { ByteString.CopyFromUtf8("row-050"), ByteString.CopyFromUtf8("row-099") }
        };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Should().HaveCount(5); // 3 from range + 2 specific
    }

    [Fact]
    public async Task Empty_range_returns_nothing()
    {
        var rowSet = new RowSet
        {
            RowRanges = { RowRange.ClosedOpen("zzz-000", "zzz-999") }
        };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Open_ended_range()
    {
        var rowSet = new RowSet
        {
            RowRanges = { new RowRange { StartKeyClosed = ByteString.CopyFromUtf8("row-095") } }
        };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Should().HaveCount(5); // 095-099
    }

    [Fact]
    public async Task Open_start_range()
    {
        var rowSet = new RowSet
        {
            RowRanges = { new RowRange { EndKeyOpen = ByteString.CopyFromUtf8("row-003") } }
        };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Should().HaveCount(3); // 000, 001, 002
    }

    [Fact]
    public async Task Closed_range_includes_end()
    {
        var rowSet = new RowSet
        {
            RowRanges = { RowRange.Closed("row-010", "row-012") }
        };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Open_range_excludes_both()
    {
        var rowSet = new RowSet
        {
            RowRanges = { RowRange.Open("row-010", "row-013") }
        };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Should().HaveCount(2); // 011, 012
    }

    [Fact]
    public async Task Multiple_specific_keys()
    {
        var rowSet = RowSet.FromRowKeys("row-001", "row-025", "row-050", "row-075", "row-099");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task Specific_keys_missing_some()
    {
        var rowSet = RowSet.FromRowKeys("row-001", "missing-key", "row-050");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Multi_range_with_limit()
    {
        var rowSet = new RowSet
        {
            RowRanges = {
                RowRange.ClosedOpen("row-000", "row-010"),
                RowRange.ClosedOpen("row-050", "row-060"),
            }
        };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet, rowsLimit: 15))
            rows.Add(r);
        rows.Should().HaveCount(15);
    }

    [Fact]
    public async Task Multi_range_with_filter()
    {
        var rowSet = new RowSet
        {
            RowRanges = {
                RowRange.ClosedOpen("row-000", "row-010"),
                RowRange.ClosedOpen("row-050", "row-060"),
            }
        };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet, filter: RowFilters.CellsPerRowLimit(1)))
            rows.Add(r);
        rows.Should().HaveCount(20);
    }

    [Fact]
    public async Task Results_sorted_across_ranges()
    {
        var rowSet = new RowSet
        {
            RowRanges = {
                RowRange.ClosedOpen("row-050", "row-052"),
                RowRange.ClosedOpen("row-010", "row-012"),
            }
        };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Duplicate_specific_keys_no_duplicate_rows()
    {
        var rowSet = new RowSet
        {
            RowKeys = {
                ByteString.CopyFromUtf8("row-005"),
                ByteString.CopyFromUtf8("row-005"),
                ByteString.CopyFromUtf8("row-010"),
            }
        };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().OnlyHaveUniqueItems();
    }
}

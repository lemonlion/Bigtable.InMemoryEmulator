using System.Collections.Generic;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for reading multiple rows via RowSet and various scan patterns.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
///   "rows: The row keys and/or ranges to read sequentially."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsMultiRowTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "multi-row";

    public ReadRowsMultiRowTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task SeedRows()
    {
        for (int i = 1; i <= 10; i++)
            await Client.MutateRowAsync(TN, $"mr-r{i:D2}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)));
    }

    [Fact]
    public async Task Read_specific_keys()
    {
        await SeedRows();
        var rowSet = RowSet.FromRowKeys("mr-r02", "mr-r05", "mr-r08");
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN, rowSet))
            rows.Add(__row);
        rows.Should().HaveCount(3);
        rows[0].Key.ToStringUtf8().Should().Be("mr-r02");
        rows[1].Key.ToStringUtf8().Should().Be("mr-r05");
        rows[2].Key.ToStringUtf8().Should().Be("mr-r08");
    }

    [Fact]
    public async Task Read_nonexistent_keys_returns_empty()
    {
        await SeedRows();
        var rowSet = RowSet.FromRowKeys("mr-missing1", "mr-missing2");
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN, rowSet))
            rows.Add(__row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Read_mix_of_existing_and_missing()
    {
        await SeedRows();
        var rowSet = RowSet.FromRowKeys("mr-r01", "mr-missing", "mr-r10");
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN, rowSet))
            rows.Add(__row);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Read_single_key_via_rowset()
    {
        await SeedRows();
        var rowSet = RowSet.FromRowKeys("mr-r03");
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN, rowSet))
            rows.Add(__row);
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("mr-r03");
    }

    [Fact]
    public async Task Full_scan_returns_ordered()
    {
        await SeedRows();
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN))
            rows.Add(__row);
        var myRows = rows.Where(r => r.Key.ToStringUtf8().StartsWith("mr-r")).ToList();
        myRows.Count.Should().BeGreaterThanOrEqualTo(10);
        for (int i = 1; i < myRows.Count; i++)
            string.Compare(myRows[i - 1].Key.ToStringUtf8(), myRows[i].Key.ToStringUtf8(), StringComparison.Ordinal)
                .Should().BeLessThan(0);
    }

    [Fact]
    public async Task Read_with_limit()
    {
        await SeedRows();
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN, filter: null, rowsLimit: 3))
            rows.Add(__row);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Read_with_limit_and_filter()
    {
        await SeedRows();
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN,
            filter: RowFilters.ValueRegex("v[1-3]"),
            rowsLimit: 2))
            rows.Add(__row);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Read_range_open_end()
    {
        await SeedRows();
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(new RowRange { StartKeyClosed = Google.Protobuf.ByteString.CopyFromUtf8("mr-r08") });
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN, rowSet))
            rows.Add(__row);
        var myRows = rows.Where(r => r.Key.ToStringUtf8().StartsWith("mr-r")).ToList();
        myRows.Count.Should().BeGreaterThanOrEqualTo(3); // r08, r09, r10
    }

    [Fact]
    public async Task Read_range_open_start()
    {
        await SeedRows();
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(new RowRange { EndKeyOpen = Google.Protobuf.ByteString.CopyFromUtf8("mr-r03") });
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN, rowSet))
            rows.Add(__row);
        var myRows = rows.Where(r => r.Key.ToStringUtf8().StartsWith("mr-r")).ToList();
        myRows.Should().HaveCount(2); // r01, r02
    }

    [Fact]
    public async Task Read_empty_table_returns_empty()
    {
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN, RowSet.FromRowKeys("no-such-key")))
            rows.Add(__row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Read_duplicate_keys_returns_once()
    {
        await SeedRows();
        var rowSet = RowSet.FromRowKeys("mr-r01", "mr-r01");
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN, rowSet))
            rows.Add(__row);
        // Bigtable deduplicates row keys in the request
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Read_keys_plus_range()
    {
        await SeedRows();
        var rowSet = RowSet.FromRowKeys("mr-r01");
        rowSet.RowRanges.Add(new RowRange
        {
            StartKeyClosed = Google.Protobuf.ByteString.CopyFromUtf8("mr-r09"),
            EndKeyClosed = Google.Protobuf.ByteString.CopyFromUtf8("mr-r10")
        });
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN, rowSet))
            rows.Add(__row);
        rows.Count.Should().BeGreaterThanOrEqualTo(3); // r01, r09, r10
    }

    [Fact]
    public async Task Read_with_filter_ValueExact()
    {
        await SeedRows();
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN,
            filter: RowFilters.ValueExact("v5")))
            rows.Add(__row);
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("mr-r05");
    }

    [Fact]
    public async Task Read_with_cells_per_row_limit()
    {
        await Client.MutateRowAsync(TN, "mr-multi",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "mr-multi",
            RowFilters.CellsPerRowLimit(2));
        var totalCells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();
        totalCells.Should().Be(2);
    }
}

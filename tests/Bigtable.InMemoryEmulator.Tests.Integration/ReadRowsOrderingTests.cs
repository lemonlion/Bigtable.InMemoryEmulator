using System.Collections.Generic;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for ReadRows ordering guarantees and edge cases.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsresponse
///   "Rows are returned in order."
///   Within a row: families sorted by name, columns sorted by qualifier, cells by descending timestamp.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsOrderingTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string CF2 = "cf2";
    private const string Table = "order-test";

    public ReadRowsOrderingTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task Rows_in_lexicographic_order()
    {
        await Client.MutateRowAsync(TN, "ord-c",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ord-a",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ord-b",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN,
            RowSet.FromRowKeys("ord-a", "ord-b", "ord-c")))
            rows.Add(__row);
        rows[0].Key.ToStringUtf8().Should().Be("ord-a");
        rows[1].Key.ToStringUtf8().Should().Be("ord-b");
        rows[2].Key.ToStringUtf8().Should().Be("ord-c");
    }

    [Fact]
    public async Task Families_sorted_alphabetically()
    {
        await Client.MutateRowAsync(TN, "ord-fam1",
            Mutations.SetCell(CF2, "c", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "ord-fam1");
        row!.Families[0].Name.Should().Be(CF);
        row.Families[1].Name.Should().Be(CF2);
    }

    [Fact]
    public async Task Columns_sorted_by_qualifier()
    {
        await Client.MutateRowAsync(TN, "ord-col1",
            Mutations.SetCell(CF, "z", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "m", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "ord-col1");
        var quals = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Cells_descending_by_timestamp()
    {
        await Client.MutateRowAsync(TN, "ord-ts1",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "ord-ts1");
        var timestamps = row!.Families[0].Columns[0].Cells.Select(c => c.TimestampMicros).ToList();
        timestamps.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Scan_respects_row_order()
    {
        for (int i = 5; i >= 1; i--)
            await Client.MutateRowAsync(TN, $"ord-scan-{i}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)));
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(new RowRange
        {
            StartKeyClosed = Google.Protobuf.ByteString.CopyFromUtf8("ord-scan-1"),
            EndKeyClosed = Google.Protobuf.ByteString.CopyFromUtf8("ord-scan-5")
        });
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN, rowSet))
            rows.Add(__row);
        rows.Should().HaveCount(5);
        for (int i = 1; i < rows.Count; i++)
            string.Compare(rows[i - 1].Key.ToStringUtf8(), rows[i].Key.ToStringUtf8(), StringComparison.Ordinal)
                .Should().BeLessThan(0);
    }

    [Fact]
    public async Task Multiple_columns_multiple_versions_ordered()
    {
        await Client.MutateRowAsync(TN, "ord-multi1",
            Mutations.SetCell(CF, "b", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(3000)),
            Mutations.SetCell(CF, "a", "v2", new BigtableVersion(4000)));
        var row = await Client.ReadRowAsync(TN, "ord-multi1");
        // Columns sorted: a before b
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("a");
        row.Families[0].Columns[1].Qualifier.ToStringUtf8().Should().Be("b");
        // Within each column, cells descending by timestamp
        row.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(4_000_000);
        row.Families[0].Columns[1].Cells[0].TimestampMicros.Should().Be(2_000_000);
    }

    [Fact]
    public async Task Limit_respects_cell_order()
    {
        // When limiting cells per row, the first cells in order should be returned
        await Client.MutateRowAsync(TN, "ord-lim1",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "a", "v2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v3", new BigtableVersion(3000)));
        var row = await Client.ReadRowAsync(TN, "ord-lim1",
            RowFilters.CellsPerRowLimit(2));
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(2);
        // First cell: col a, ts 2000 (highest in column a)
        cells[0].TimestampMicros.Should().Be(2_000_000);
    }

    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task Empty_scan_returns_no_rows()
    {
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(new RowRange
        {
            StartKeyClosed = Google.Protobuf.ByteString.CopyFromUtf8("ord-zzz-noexist-start"),
            EndKeyClosed = Google.Protobuf.ByteString.CopyFromUtf8("ord-zzz-noexist-end")
        });
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN, rowSet))
            rows.Add(__row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task RowKeys_requested_in_wrong_order_returned_sorted()
    {
        await Client.MutateRowAsync(TN, "ord-wrong-c",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ord-wrong-a",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ord-wrong-b",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        // Request in reverse order
        var rowSet = RowSet.FromRowKeys("ord-wrong-c", "ord-wrong-a", "ord-wrong-b");
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN, rowSet))
            rows.Add(__row);
        rows[0].Key.ToStringUtf8().Should().Be("ord-wrong-a");
        rows[1].Key.ToStringUtf8().Should().Be("ord-wrong-b");
        rows[2].Key.ToStringUtf8().Should().Be("ord-wrong-c");
    }
}

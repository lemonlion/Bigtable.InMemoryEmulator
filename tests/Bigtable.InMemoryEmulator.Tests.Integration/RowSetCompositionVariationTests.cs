using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for ReadRows with various RowSet combinations.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
///   "rows: The row keys and/or ranges to read."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class RowSetCompositionVariationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "rowset-var";

    public RowSetCompositionVariationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task SeedRows(string prefix, int count)
    {
        for (int i = 0; i < count; i++)
            await Client.MutateRowAsync(TN, $"{prefix}-{i:D3}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)));
    }

    private async Task<List<Row>> ReadAll(RowSet rowSet)
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);
        return rows;
    }

    [Fact]
    public async Task Single_key_returns_one()
    {
        await SeedRows("rsv", 5);
        var rows = await ReadAll(RowSet.FromRowKeys("rsv-002"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Multiple_disjoint_keys()
    {
        await SeedRows("rsv2", 5);
        var rows = await ReadAll(RowSet.FromRowKeys("rsv2-001", "rsv2-003"));
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Closed_range()
    {
        await SeedRows("rsv3", 5);
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(new RowRange
        {
            StartKeyClosed = ByteString.CopyFromUtf8("rsv3-001"),
            EndKeyClosed = ByteString.CopyFromUtf8("rsv3-003")
        });
        var rows = await ReadAll(rowSet);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Open_range()
    {
        await SeedRows("rsv4", 5);
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(new RowRange
        {
            StartKeyOpen = ByteString.CopyFromUtf8("rsv4-001"),
            EndKeyOpen = ByteString.CopyFromUtf8("rsv4-003")
        });
        var rows = await ReadAll(rowSet);
        rows.Should().ContainSingle(); // only 002
    }

    [Fact]
    public async Task ClosedOpen_range()
    {
        await SeedRows("rsv5", 5);
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(new RowRange
        {
            StartKeyClosed = ByteString.CopyFromUtf8("rsv5-001"),
            EndKeyOpen = ByteString.CopyFromUtf8("rsv5-003")
        });
        var rows = await ReadAll(rowSet);
        rows.Should().HaveCount(2); // 001, 002
    }

    [Fact]
    public async Task OpenClosed_range()
    {
        await SeedRows("rsv6", 5);
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(new RowRange
        {
            StartKeyOpen = ByteString.CopyFromUtf8("rsv6-001"),
            EndKeyClosed = ByteString.CopyFromUtf8("rsv6-003")
        });
        var rows = await ReadAll(rowSet);
        rows.Should().HaveCount(2); // 002, 003
    }

    [Fact]
    public async Task Range_no_start()
    {
        await SeedRows("rsv7", 5);
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(new RowRange
        {
            EndKeyOpen = ByteString.CopyFromUtf8("rsv7-002")
        });
        var rows = await ReadAll(rowSet);
        var myRows = rows.Where(r => r.Key.ToStringUtf8().StartsWith("rsv7-")).ToList();
        myRows.Should().HaveCount(2); // 000, 001
    }

    [Fact]
    public async Task Range_no_end()
    {
        await SeedRows("rsv8", 5);
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(new RowRange
        {
            StartKeyClosed = ByteString.CopyFromUtf8("rsv8-003")
        });
        var rows = await ReadAll(rowSet);
        var myRows = rows.Where(r => r.Key.ToStringUtf8().StartsWith("rsv8-")).ToList();
        myRows.Should().HaveCount(2); // 003, 004
    }

    [Fact]
    public async Task Two_disjoint_ranges()
    {
        await SeedRows("rsv9", 10);
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(new RowRange
        {
            StartKeyClosed = ByteString.CopyFromUtf8("rsv9-001"),
            EndKeyClosed = ByteString.CopyFromUtf8("rsv9-002")
        });
        rowSet.RowRanges.Add(new RowRange
        {
            StartKeyClosed = ByteString.CopyFromUtf8("rsv9-007"),
            EndKeyClosed = ByteString.CopyFromUtf8("rsv9-008")
        });
        var rows = await ReadAll(rowSet);
        rows.Should().HaveCount(4);
    }

    [Fact]
    public async Task Overlapping_ranges_no_duplicate()
    {
        await SeedRows("rsv10", 5);
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(new RowRange
        {
            StartKeyClosed = ByteString.CopyFromUtf8("rsv10-001"),
            EndKeyClosed = ByteString.CopyFromUtf8("rsv10-003")
        });
        rowSet.RowRanges.Add(new RowRange
        {
            StartKeyClosed = ByteString.CopyFromUtf8("rsv10-002"),
            EndKeyClosed = ByteString.CopyFromUtf8("rsv10-004")
        });
        var rows = await ReadAll(rowSet);
        rows.Should().HaveCount(4); // 001, 002, 003, 004
    }

    [Fact]
    public async Task Keys_and_range_combined()
    {
        await SeedRows("rsv11", 10);
        var rowSet = RowSet.FromRowKeys("rsv11-000");
        rowSet.RowRanges.Add(new RowRange
        {
            StartKeyClosed = ByteString.CopyFromUtf8("rsv11-008"),
            EndKeyClosed = ByteString.CopyFromUtf8("rsv11-009")
        });
        var rows = await ReadAll(rowSet);
        rows.Should().HaveCount(3); // 000, 008, 009
    }

    [Fact]
    public async Task Key_inside_range_not_duplicated()
    {
        await SeedRows("rsv12", 5);
        var rowSet = RowSet.FromRowKeys("rsv12-002");
        rowSet.RowRanges.Add(new RowRange
        {
            StartKeyClosed = ByteString.CopyFromUtf8("rsv12-001"),
            EndKeyClosed = ByteString.CopyFromUtf8("rsv12-003")
        });
        var rows = await ReadAll(rowSet);
        rows.Should().HaveCount(3); // 001, 002, 003
    }

    [Fact]
    public async Task Adjacent_ranges()
    {
        await SeedRows("rsv13", 5);
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(new RowRange
        {
            StartKeyClosed = ByteString.CopyFromUtf8("rsv13-000"),
            EndKeyOpen = ByteString.CopyFromUtf8("rsv13-002")
        });
        rowSet.RowRanges.Add(new RowRange
        {
            StartKeyClosed = ByteString.CopyFromUtf8("rsv13-002"),
            EndKeyClosed = ByteString.CopyFromUtf8("rsv13-004")
        });
        var rows = await ReadAll(rowSet);
        rows.Should().HaveCount(5);
    }

    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task Range_no_match()
    {
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(new RowRange
        {
            StartKeyClosed = ByteString.CopyFromUtf8("rsv-zzz-no-match-start"),
            EndKeyClosed = ByteString.CopyFromUtf8("rsv-zzz-no-match-end")
        });
        var rows = await ReadAll(rowSet);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Range_with_filter()
    {
        await SeedRows("rsv14", 5);
        var rowSet = RowSet.FromRowKeys("rsv14-001", "rsv14-003");
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet, RowFilters.StripValueTransformer()))
            rows.Add(row);
        rows.Should().HaveCount(2);
        foreach (var row in rows)
            row.Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task Range_with_limit()
    {
        await SeedRows("rsv15", 10);
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex("rsv15-.*"), rowsLimit: 3))
            rows.Add(row);
        rows.Should().HaveCount(3);
    }
}

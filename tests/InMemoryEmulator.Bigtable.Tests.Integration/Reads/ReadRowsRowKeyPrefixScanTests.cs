using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsRowKeyPrefixScanTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "rr-prefix";
    private const string CF = "cf";

    public ReadRowsRowKeyPrefixScanTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        var prefixes = new[] { "user#", "order#", "product#" };
        foreach (var prefix in prefixes)
            for (int i = 0; i < 10; i++)
                await Client.MutateRowAsync(TN, $"{prefix}{i:D3}", Mutations.SetCell(CF, "v", $"{prefix}{i}"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Prefix_scan_user()
    {
        // Prefix scan: start at "user#", end before "user$" (next char after #)
        var rowSet = new RowSet { RowRanges = { RowRange.ClosedOpen("user#", "user$") } };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task Prefix_scan_order()
    {
        var rowSet = new RowSet { RowRanges = { RowRange.ClosedOpen("order#", "order$") } };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task Prefix_scan_with_limit()
    {
        var rowSet = new RowSet { RowRanges = { RowRange.ClosedOpen("user#", "user$") } };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet, rowsLimit: 5)) rows.Add(r);
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task Prefix_scan_sorted()
    {
        var rowSet = new RowSet { RowRanges = { RowRange.ClosedOpen("product#", "product$") } };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Prefix_scan_nonexistent()
    {
        var rowSet = new RowSet { RowRanges = { RowRange.ClosedOpen("xyz#", "xyz$") } };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Multiple_prefix_scans()
    {
        var rowSet = new RowSet
        {
            RowRanges = {
                RowRange.ClosedOpen("user#", "user$"),
                RowRange.ClosedOpen("order#", "order$"),
            }
        };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Should().HaveCount(20);
    }

    [Fact]
    public async Task Prefix_with_filter()
    {
        var rowSet = new RowSet { RowRanges = { RowRange.ClosedOpen("user#", "user$") } };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet, filter: RowFilters.CellsPerRowLimit(1)))
            rows.Add(r);
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task Regex_prefix_equivalent()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex("user#.*")))
            rows.Add(r);
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task Full_scan_all_prefixes()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN)) rows.Add(r);
        rows.Should().HaveCount(30);
    }

    [Fact]
    public async Task Prefix_first_and_last()
    {
        var rowSet = new RowSet { RowRanges = { RowRange.ClosedOpen("user#", "user$") } };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.First().Key.ToStringUtf8().Should().Be("user#000");
        rows.Last().Key.ToStringUtf8().Should().Be("user#009");
    }

    [Fact]
    public async Task Prefix_scan_with_value_filter()
    {
        var rowSet = new RowSet { RowRanges = { RowRange.ClosedOpen("user#", "user$") } };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet, filter: RowFilters.ValueRegex("user#0")))
            rows.Add(r);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Delete_prefix_rows()
    {
        for (int i = 0; i < 10; i++)
            await Client.MutateRowAsync(TN, $"product#{i:D3}", Mutations.DeleteFromRow());
        var rowSet = new RowSet { RowRanges = { RowRange.ClosedOpen("product#", "product$") } };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Prefix_scan_with_specific_keys()
    {
        var rowSet = new RowSet
        {
            RowRanges = { RowRange.ClosedOpen("user#", "user$") },
            RowKeys = { ByteString.CopyFromUtf8("order#000") }
        };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Should().HaveCount(11);
    }
}

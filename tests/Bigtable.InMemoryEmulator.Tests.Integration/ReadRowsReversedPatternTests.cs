using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for ReadRows reversed mode, returning rows in reverse lexicographic order.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
///   "reversed: If true, rows are returned in reverse order."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.GcpOnly)]
public sealed class ReadRowsReversedPatternTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "rev-pat";

    public ReadRowsReversedPatternTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private BigtableServiceApiClient ServiceApiClient => _fixture.ServiceApiClient;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task SeedRows()
    {
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, $"rp-row-{i}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)));
    }

    [Fact]
    public async Task Reversed_range_returns_descending()
    {
        await SeedRows();
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Reversed = true,
            Rows = new RowSet()
        };
        req.Rows.RowRanges.Add(new RowRange
        {
            StartKeyClosed = Google.Protobuf.ByteString.CopyFromUtf8("rp-row-1"),
            EndKeyClosed = Google.Protobuf.ByteString.CopyFromUtf8("rp-row-5")
        });
        var stream = ServiceApiClient.ReadRows(req);
        var rows = new List<string>();
        await foreach (var resp in stream.GetResponseStream())
            foreach (var chunk in resp.Chunks)
                if (chunk.RowKey != null && chunk.RowKey.Length > 0)
                    if (!rows.Contains(chunk.RowKey.ToStringUtf8()))
                        rows.Add(chunk.RowKey.ToStringUtf8());
        rows.Should().HaveCount(5);
        rows.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Reversed_with_limit()
    {
        await SeedRows();
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Reversed = true,
            RowsLimit = 2,
            Rows = new RowSet()
        };
        req.Rows.RowRanges.Add(new RowRange
        {
            StartKeyClosed = Google.Protobuf.ByteString.CopyFromUtf8("rp-row-1"),
            EndKeyClosed = Google.Protobuf.ByteString.CopyFromUtf8("rp-row-5")
        });
        var stream = ServiceApiClient.ReadRows(req);
        var rows = new List<string>();
        await foreach (var resp in stream.GetResponseStream())
            foreach (var chunk in resp.Chunks)
                if (chunk.RowKey != null && chunk.RowKey.Length > 0)
                    if (!rows.Contains(chunk.RowKey.ToStringUtf8()))
                        rows.Add(chunk.RowKey.ToStringUtf8());
        rows.Should().HaveCount(2);
        rows[0].Should().Be("rp-row-5");
        rows[1].Should().Be("rp-row-4");
    }

    [Fact]
    public async Task Reversed_specific_keys()
    {
        await SeedRows();
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Reversed = true,
            Rows = RowSet.FromRowKeys("rp-row-1", "rp-row-3", "rp-row-5")
        };
        var stream = ServiceApiClient.ReadRows(req);
        var rows = new List<string>();
        await foreach (var resp in stream.GetResponseStream())
            foreach (var chunk in resp.Chunks)
                if (chunk.RowKey != null && chunk.RowKey.Length > 0)
                    if (!rows.Contains(chunk.RowKey.ToStringUtf8()))
                        rows.Add(chunk.RowKey.ToStringUtf8());
        rows.Should().HaveCount(3);
        rows.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Reversed_with_filter()
    {
        await SeedRows();
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Reversed = true,
            Filter = RowFilters.ValueRegex("v[35]"),
            Rows = new RowSet()
        };
        req.Rows.RowRanges.Add(new RowRange
        {
            StartKeyClosed = Google.Protobuf.ByteString.CopyFromUtf8("rp-row-1"),
            EndKeyClosed = Google.Protobuf.ByteString.CopyFromUtf8("rp-row-5")
        });
        var stream = ServiceApiClient.ReadRows(req);
        var rows = new List<string>();
        await foreach (var resp in stream.GetResponseStream())
            foreach (var chunk in resp.Chunks)
                if (chunk.RowKey != null && chunk.RowKey.Length > 0)
                    if (!rows.Contains(chunk.RowKey.ToStringUtf8()))
                        rows.Add(chunk.RowKey.ToStringUtf8());
        rows.Should().HaveCount(2);
        rows[0].Should().Be("rp-row-5");
        rows[1].Should().Be("rp-row-3");
    }

    [Fact]
    public async Task Reversed_empty_result()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Reversed = true,
            Rows = RowSet.FromRowKeys("rp-nonexistent")
        };
        var stream = ServiceApiClient.ReadRows(req);
        var rows = new List<string>();
        await foreach (var resp in stream.GetResponseStream())
            foreach (var chunk in resp.Chunks)
                if (chunk.RowKey != null && chunk.RowKey.Length > 0)
                    rows.Add(chunk.RowKey.ToStringUtf8());
        rows.Should().BeEmpty();
    }
}

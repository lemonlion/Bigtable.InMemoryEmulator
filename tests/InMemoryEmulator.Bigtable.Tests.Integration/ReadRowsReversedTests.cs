using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for ReadRows with reversed=true (descending row key order).
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
///   "reversed: If true, rows are returned in reverse order of their key."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.GcpOnly)]
public sealed class ReadRowsReversedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public ReadRowsReversedTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync("rev-test", new[] { CF });
        var tn = _fixture.GetTableName("rev-test");
        // Seed rows a-j
        for (char c = 'a'; c <= 'j'; c++)
            await _fixture.Client.MutateRowAsync(tn, c.ToString(),
                Mutations.SetCell(CF, "c", $"val-{c}", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("rev-test");

    private async Task<List<Row>> ReadReversed(ReadRowsRequest request)
    {
        var rows = new List<Row>();
        var stream = _fixture.ServiceApiClient.ReadRows(request);
        await foreach (var response in stream.GetResponseStream())
            foreach (var chunk in response.Chunks)
            {
                // Simple assembly: each chunk with CommitRow is a row
                if (chunk.CommitRow)
                {
                    // We need to use the full readrows API for reversed
                    // Fall back to collecting from the high-level client is better
                }
            }
        return rows;
    }

    [Fact]
    public async Task Reversed_full_scan_returns_descending_order()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Reversed = true,
        };
        var rows = new List<Row>();
        var stream = _fixture.ServiceApiClient.ReadRows(request);
        // Collect row keys from raw response chunks
        var rowKeys = new List<string>();
        string? currentRowKey = null;
        await foreach (var response in stream.GetResponseStream())
        {
            foreach (var chunk in response.Chunks)
            {
                if (chunk.RowKey != null && !chunk.RowKey.IsEmpty)
                    currentRowKey = chunk.RowKey.ToStringUtf8();
                if (chunk.CommitRow && currentRowKey != null)
                {
                    rowKeys.Add(currentRowKey);
                    currentRowKey = null;
                }
            }
        }

        rowKeys.Should().HaveCount(10);
        rowKeys.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Reversed_with_row_range_returns_descending()
    {
        // Range [c, g) reversed should give f, e, d, c
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Reversed = true,
            Rows = new RowSet
            {
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("c"),
                        EndKeyOpen = ByteString.CopyFromUtf8("g")
                    }
                }
            }
        };
        var rowKeys = new List<string>();
        string? currentRowKey = null;
        await foreach (var response in _fixture.ServiceApiClient.ReadRows(request).GetResponseStream())
        {
            foreach (var chunk in response.Chunks)
            {
                if (chunk.RowKey != null && !chunk.RowKey.IsEmpty)
                    currentRowKey = chunk.RowKey.ToStringUtf8();
                if (chunk.CommitRow && currentRowKey != null)
                {
                    rowKeys.Add(currentRowKey);
                    currentRowKey = null;
                }
            }
        }

        rowKeys.Should().BeEquivalentTo(new[] { "f", "e", "d", "c" });
        rowKeys.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Reversed_with_rows_limit()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Reversed = true,
            RowsLimit = 3,
        };
        var rowKeys = new List<string>();
        string? currentRowKey = null;
        await foreach (var response in _fixture.ServiceApiClient.ReadRows(request).GetResponseStream())
        {
            foreach (var chunk in response.Chunks)
            {
                if (chunk.RowKey != null && !chunk.RowKey.IsEmpty)
                    currentRowKey = chunk.RowKey.ToStringUtf8();
                if (chunk.CommitRow && currentRowKey != null)
                {
                    rowKeys.Add(currentRowKey);
                    currentRowKey = null;
                }
            }
        }

        rowKeys.Should().HaveCount(3);
        rowKeys[0].Should().Be("j");
        rowKeys[1].Should().Be("i");
        rowKeys[2].Should().Be("h");
    }

    [Fact]
    public async Task Reversed_with_specific_row_keys()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Reversed = true,
            Rows = new RowSet
            {
                RowKeys = { ByteString.CopyFromUtf8("a"), ByteString.CopyFromUtf8("e"), ByteString.CopyFromUtf8("h") }
            }
        };
        var rowKeys = new List<string>();
        string? currentRowKey = null;
        await foreach (var response in _fixture.ServiceApiClient.ReadRows(request).GetResponseStream())
        {
            foreach (var chunk in response.Chunks)
            {
                if (chunk.RowKey != null && !chunk.RowKey.IsEmpty)
                    currentRowKey = chunk.RowKey.ToStringUtf8();
                if (chunk.CommitRow && currentRowKey != null)
                {
                    rowKeys.Add(currentRowKey);
                    currentRowKey = null;
                }
            }
        }

        rowKeys.Should().HaveCount(3);
        rowKeys.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Reversed_with_filter()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Reversed = true,
            Filter = RowFilters.ValueRegex("val-[a-c]"),
        };
        var rowKeys = new List<string>();
        string? currentRowKey = null;
        await foreach (var response in _fixture.ServiceApiClient.ReadRows(request).GetResponseStream())
        {
            foreach (var chunk in response.Chunks)
            {
                if (chunk.RowKey != null && !chunk.RowKey.IsEmpty)
                    currentRowKey = chunk.RowKey.ToStringUtf8();
                if (chunk.CommitRow && currentRowKey != null)
                {
                    rowKeys.Add(currentRowKey);
                    currentRowKey = null;
                }
            }
        }

        rowKeys.Should().HaveCount(3);
        rowKeys.Should().BeEquivalentTo(new[] { "c", "b", "a" });
    }

    [Fact]
    public async Task Reversed_empty_table_returns_nothing()
    {
        await _fixture.CreateTableAsync("rev-empty", new[] { CF });
        var emptyTn = _fixture.GetTableName("rev-empty");
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = emptyTn,
            Reversed = true,
        };
        var rowKeys = new List<string>();
        await foreach (var response in _fixture.ServiceApiClient.ReadRows(request).GetResponseStream())
        {
            foreach (var chunk in response.Chunks)
            {
                if (chunk.CommitRow)
                    rowKeys.Add("found");
            }
        }
        rowKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task Reversed_single_row_returns_that_row()
    {
        await _fixture.CreateTableAsync("rev-single", new[] { CF });
        var singleTn = _fixture.GetTableName("rev-single");
        await Client.MutateRowAsync(singleTn, "only",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = singleTn,
            Reversed = true,
        };
        var rowKeys = new List<string>();
        string? currentRowKey = null;
        await foreach (var response in _fixture.ServiceApiClient.ReadRows(request).GetResponseStream())
        {
            foreach (var chunk in response.Chunks)
            {
                if (chunk.RowKey != null && !chunk.RowKey.IsEmpty)
                    currentRowKey = chunk.RowKey.ToStringUtf8();
                if (chunk.CommitRow && currentRowKey != null)
                {
                    rowKeys.Add(currentRowKey);
                    currentRowKey = null;
                }
            }
        }
        rowKeys.Should().ContainSingle().Which.Should().Be("only");
    }
}

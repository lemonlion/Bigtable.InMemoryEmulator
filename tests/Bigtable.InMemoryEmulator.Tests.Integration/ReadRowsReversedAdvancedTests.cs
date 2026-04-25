using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Advanced reversed read tests covering ranges, limits, filters, and combinations.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
///   "reversed: Return rows in lexicographical descending order of the row keys."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsReversedAdvancedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string CF2 = "cf2";
    private const string Table = "rev-adv";
    private const int RowCount = 50;

    public ReadRowsReversedAdvancedTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
        var tn = TN;
        var entries = Enumerable.Range(0, RowCount).Select(i =>
            Mutations.CreateEntry($"rev-{i:D4}",
                Mutations.SetCell(CF, "c1", $"v{i}-1", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "c1", $"v{i}-2", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "c2", $"w{i}", new BigtableVersion(1000)),
                Mutations.SetCell(CF2, "d1", $"x{i}", new BigtableVersion(1000)))).ToArray();
        await _fixture.Client.MutateRowsAsync(tn, entries);
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableServiceApiClient Api => _fixture.ServiceApiClient;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<string>> ReadKeys(ReadRowsRequest request)
    {
        var rowKeys = new List<string>();
        string? currentRowKey = null;
        var stream = Api.ReadRows(request);
        await foreach (var response in stream.GetResponseStream())
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
        return rowKeys;
    }

    #region Range combinations

    [Fact]
    public async Task Reversed_open_start_range()
    {
        var keys = await ReadKeys(new ReadRowsRequest
        {
            TableNameAsTableName = TN, Reversed = true,
            Rows = new RowSet { RowRanges = { new RowRange { EndKeyClosed = ByteString.CopyFromUtf8("rev-0010") } } }
        });
        keys.Should().HaveCount(11);
        keys[0].Should().Be("rev-0010");
        keys.Last().Should().Be("rev-0000");
    }

    [Fact]
    public async Task Reversed_open_end_range()
    {
        var keys = await ReadKeys(new ReadRowsRequest
        {
            TableNameAsTableName = TN, Reversed = true,
            Rows = new RowSet { RowRanges = { new RowRange { StartKeyClosed = ByteString.CopyFromUtf8("rev-0040") } } }
        });
        keys.Should().HaveCount(10);
        keys[0].Should().Be("rev-0049");
        keys.Last().Should().Be("rev-0040");
    }

    [Fact]
    public async Task Reversed_closed_closed_range()
    {
        var keys = await ReadKeys(new ReadRowsRequest
        {
            TableNameAsTableName = TN, Reversed = true,
            Rows = new RowSet { RowRanges = { new RowRange
            {
                StartKeyClosed = ByteString.CopyFromUtf8("rev-0020"),
                EndKeyClosed = ByteString.CopyFromUtf8("rev-0025")
            }}}
        });
        keys.Should().HaveCount(6);
        keys[0].Should().Be("rev-0025");
        keys[5].Should().Be("rev-0020");
    }

    [Fact]
    public async Task Reversed_open_open_range()
    {
        var keys = await ReadKeys(new ReadRowsRequest
        {
            TableNameAsTableName = TN, Reversed = true,
            Rows = new RowSet { RowRanges = { new RowRange
            {
                StartKeyOpen = ByteString.CopyFromUtf8("rev-0020"),
                EndKeyOpen = ByteString.CopyFromUtf8("rev-0025")
            }}}
        });
        keys.Should().HaveCount(4);
        keys[0].Should().Be("rev-0024");
        keys[3].Should().Be("rev-0021");
    }

    [Fact]
    public async Task Reversed_multiple_ranges()
    {
        var keys = await ReadKeys(new ReadRowsRequest
        {
            TableNameAsTableName = TN, Reversed = true,
            Rows = new RowSet { RowRanges =
            {
                new RowRange { StartKeyClosed = ByteString.CopyFromUtf8("rev-0005"), EndKeyClosed = ByteString.CopyFromUtf8("rev-0007") },
                new RowRange { StartKeyClosed = ByteString.CopyFromUtf8("rev-0015"), EndKeyClosed = ByteString.CopyFromUtf8("rev-0017") }
            }}
        });
        keys.Should().HaveCount(6);
        keys[0].Should().Be("rev-0017");
    }

    [Fact]
    public async Task Reversed_empty_range()
    {
        var keys = await ReadKeys(new ReadRowsRequest
        {
            TableNameAsTableName = TN, Reversed = true,
            Rows = new RowSet { RowRanges = { new RowRange
            {
                StartKeyClosed = ByteString.CopyFromUtf8("zzz-0001"),
                EndKeyClosed = ByteString.CopyFromUtf8("zzz-0002")
            }}}
        });
        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task Reversed_start_open_end_closed()
    {
        var keys = await ReadKeys(new ReadRowsRequest
        {
            TableNameAsTableName = TN, Reversed = true,
            Rows = new RowSet { RowRanges = { new RowRange
            {
                StartKeyOpen = ByteString.CopyFromUtf8("rev-0010"),
                EndKeyClosed = ByteString.CopyFromUtf8("rev-0015")
            }}}
        });
        keys.Should().HaveCount(5); // 11,12,13,14,15
        keys[0].Should().Be("rev-0015");
        keys[4].Should().Be("rev-0011");
    }

    #endregion

    #region Row keys

    [Fact]
    public async Task Reversed_specific_keys_desc()
    {
        var keys = await ReadKeys(new ReadRowsRequest
        {
            TableNameAsTableName = TN, Reversed = true,
            Rows = new RowSet
            {
                RowKeys = { ByteString.CopyFromUtf8("rev-0005"), ByteString.CopyFromUtf8("rev-0010"), ByteString.CopyFromUtf8("rev-0001") }
            }
        });
        keys.Should().HaveCount(3);
        keys.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Reversed_keys_and_ranges()
    {
        var keys = await ReadKeys(new ReadRowsRequest
        {
            TableNameAsTableName = TN, Reversed = true,
            Rows = new RowSet
            {
                RowKeys = { ByteString.CopyFromUtf8("rev-0030") },
                RowRanges = { new RowRange { StartKeyClosed = ByteString.CopyFromUtf8("rev-0001"), EndKeyClosed = ByteString.CopyFromUtf8("rev-0003") } }
            }
        });
        keys.Should().HaveCount(4);
        keys[0].Should().Be("rev-0030");
    }

    [Fact]
    public async Task Reversed_nonexistent_key_skipped()
    {
        var keys = await ReadKeys(new ReadRowsRequest
        {
            TableNameAsTableName = TN, Reversed = true,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("rev-0005"), ByteString.CopyFromUtf8("nope"), ByteString.CopyFromUtf8("rev-0001") } }
        });
        keys.Should().HaveCount(2);
    }

    [Fact]
    public async Task Reversed_single_key()
    {
        var keys = await ReadKeys(new ReadRowsRequest
        {
            TableNameAsTableName = TN, Reversed = true,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("rev-0025") } }
        });
        keys.Should().ContainSingle().Which.Should().Be("rev-0025");
    }

    #endregion

    #region Limits

    [Fact]
    public async Task Reversed_limit_5()
    {
        var keys = await ReadKeys(new ReadRowsRequest
        {
            TableNameAsTableName = TN, Reversed = true, RowsLimit = 5
        });
        keys.Should().HaveCount(5);
        keys[0].Should().Be("rev-0049");
        keys[4].Should().Be("rev-0045");
    }

    [Fact]
    public async Task Reversed_limit_1()
    {
        var keys = await ReadKeys(new ReadRowsRequest
        {
            TableNameAsTableName = TN, Reversed = true, RowsLimit = 1
        });
        keys.Should().ContainSingle().Which.Should().Be("rev-0049");
    }

    [Fact]
    public async Task Reversed_limit_with_range()
    {
        var keys = await ReadKeys(new ReadRowsRequest
        {
            TableNameAsTableName = TN, Reversed = true, RowsLimit = 3,
            Rows = new RowSet { RowRanges = { new RowRange
            {
                StartKeyClosed = ByteString.CopyFromUtf8("rev-0010"),
                EndKeyClosed = ByteString.CopyFromUtf8("rev-0030")
            }}}
        });
        keys.Should().HaveCount(3);
        keys[0].Should().Be("rev-0030");
    }

    [Fact]
    public async Task Reversed_limit_exceeds_total()
    {
        var keys = await ReadKeys(new ReadRowsRequest
        {
            TableNameAsTableName = TN, Reversed = true, RowsLimit = 1000
        });
        keys.Should().HaveCount(RowCount);
    }

    #endregion

    #region Filters

    [Fact]
    public async Task Reversed_with_row_key_regex()
    {
        var keys = await ReadKeys(new ReadRowsRequest
        {
            TableNameAsTableName = TN, Reversed = true,
            Filter = RowFilters.RowKeyRegex("rev-004[0-5]")
        });
        keys.Should().HaveCount(6);
        keys[0].Should().Be("rev-0045");
        keys[5].Should().Be("rev-0040");
    }

    [Fact]
    public async Task Reversed_block_all_empty()
    {
        var keys = await ReadKeys(new ReadRowsRequest
        {
            TableNameAsTableName = TN, Reversed = true,
            Filter = RowFilters.BlockAllFilter()
        });
        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task Reversed_pass_all_returns_all()
    {
        var keys = await ReadKeys(new ReadRowsRequest
        {
            TableNameAsTableName = TN, Reversed = true,
            Filter = RowFilters.PassAllFilter()
        });
        keys.Should().HaveCount(RowCount);
        keys[0].Should().Be("rev-0049");
    }

    [Fact]
    public async Task Reversed_filter_and_limit()
    {
        var keys = await ReadKeys(new ReadRowsRequest
        {
            TableNameAsTableName = TN, Reversed = true, RowsLimit = 3,
            Filter = RowFilters.RowKeyRegex("rev-00[0-2].")
        });
        keys.Should().HaveCount(3);
        keys[0].Should().Be("rev-0029");
    }

    [Fact]
    public async Task Reversed_filter_range_and_limit()
    {
        var keys = await ReadKeys(new ReadRowsRequest
        {
            TableNameAsTableName = TN, Reversed = true, RowsLimit = 2,
            Rows = new RowSet { RowRanges = { new RowRange
            {
                StartKeyClosed = ByteString.CopyFromUtf8("rev-0020"),
                EndKeyClosed = ByteString.CopyFromUtf8("rev-0040")
            }}},
            Filter = RowFilters.RowKeyRegex("rev-003.")
        });
        keys.Should().HaveCount(2);
        keys[0].Should().Be("rev-0039");
        keys[1].Should().Be("rev-0038");
    }

    #endregion
}

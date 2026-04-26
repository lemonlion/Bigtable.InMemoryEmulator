using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for row key prefix scans and range scans with various patterns.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class RowKeyPrefixScanTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "rkps-tests";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public RowKeyPrefixScanTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });

        // Create rows with hierarchical keys
        var prefixes = new[] { "usa#ca#", "usa#ny#", "usa#tx#", "gbr#ldn#", "gbr#man#", "deu#ber#" };
        foreach (var p in prefixes)
            for (int i = 0; i < 5; i++)
                await Client.MutateRowAsync(TN, $"{p}{i:D3}",
                    Mutations.SetCell(CF, "val", $"{p}{i}", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Full_scan_returns_all_rows()
    {
        var count = await CountRows(new ReadRowsRequest { TableNameAsTableName = TN });
        count.Should().Be(30);
    }

    [Fact]
    public async Task Range_scan_usa_prefix()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges = { new RowRange
                {
                    StartKeyClosed = ByteString.CopyFromUtf8("usa#"),
                    EndKeyOpen = ByteString.CopyFromUtf8("usa$") // $ > # in ASCII
                }}
            }
        };
        var count = await CountRows(request);
        count.Should().Be(15); // 3 USA states × 5 each
    }

    [Fact]
    public async Task Range_scan_single_state()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges = { new RowRange
                {
                    StartKeyClosed = ByteString.CopyFromUtf8("usa#ca#"),
                    EndKeyOpen = ByteString.CopyFromUtf8("usa#ca$")
                }}
            }
        };
        var count = await CountRows(request);
        count.Should().Be(5);
    }

    [Fact]
    public async Task Range_scan_gbr_prefix()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges = { new RowRange
                {
                    StartKeyClosed = ByteString.CopyFromUtf8("gbr#"),
                    EndKeyOpen = ByteString.CopyFromUtf8("gbr$")
                }}
            }
        };
        var count = await CountRows(request);
        count.Should().Be(10);
    }

    [Fact]
    public async Task Multiple_ranges_in_single_request()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("usa#ca#"),
                        EndKeyOpen = ByteString.CopyFromUtf8("usa#ca$")
                    },
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("deu#"),
                        EndKeyOpen = ByteString.CopyFromUtf8("deu$")
                    }
                }
            }
        };
        var count = await CountRows(request);
        count.Should().Be(10); // 5 usa#ca# + 5 deu#ber#
    }

    [Fact]
    public async Task Range_with_limit()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges = { new RowRange
                {
                    StartKeyClosed = ByteString.CopyFromUtf8("usa#"),
                    EndKeyOpen = ByteString.CopyFromUtf8("usa$")
                }}
            },
            RowsLimit = 3
        };
        var count = await CountRows(request);
        count.Should().Be(3);
    }

    [Fact]
    public async Task Specific_row_keys_lookup()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowKeys =
                {
                    ByteString.CopyFromUtf8("usa#ca#000"),
                    ByteString.CopyFromUtf8("gbr#ldn#002"),
                    ByteString.CopyFromUtf8("deu#ber#004")
                }
            }
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());

        keys.Should().HaveCount(3);
        keys.Should().Contain("usa#ca#000");
        keys.Should().Contain("gbr#ldn#002");
        keys.Should().Contain("deu#ber#004");
    }

    [Fact]
    public async Task Mixed_keys_and_ranges()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowKeys = { ByteString.CopyFromUtf8("usa#ca#000") },
                RowRanges = { new RowRange
                {
                    StartKeyClosed = ByteString.CopyFromUtf8("deu#"),
                    EndKeyOpen = ByteString.CopyFromUtf8("deu$")
                }}
            }
        };
        var count = await CountRows(request);
        count.Should().Be(6); // 1 specific + 5 deu range
    }

    [Fact]
    public async Task Empty_range_returns_nothing()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges = { new RowRange
                {
                    StartKeyClosed = ByteString.CopyFromUtf8("zzz#"),
                    EndKeyOpen = ByteString.CopyFromUtf8("zzz$")
                }}
            }
        };
        var count = await CountRows(request);
        count.Should().Be(0);
    }

    [Fact]
    public async Task Open_start_range()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges = { new RowRange
                {
                    StartKeyOpen = ByteString.CopyFromUtf8("usa#ca#002"),
                    EndKeyOpen = ByteString.CopyFromUtf8("usa#ca$")
                }}
            }
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());

        keys.Should().NotContain("usa#ca#002"); // open start excludes
        keys.Should().Contain("usa#ca#003");
        keys.Should().Contain("usa#ca#004");
    }

    [Fact]
    public async Task Closed_end_range()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges = { new RowRange
                {
                    StartKeyClosed = ByteString.CopyFromUtf8("usa#ca#002"),
                    EndKeyClosed = ByteString.CopyFromUtf8("usa#ca#003")
                }}
            }
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());

        keys.Should().HaveCount(2);
        keys.Should().Contain("usa#ca#002");
        keys.Should().Contain("usa#ca#003");
    }

    [Fact]
    public async Task Nonexistent_specific_keys_return_nothing()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowKeys =
                {
                    ByteString.CopyFromUtf8("nonexistent1"),
                    ByteString.CopyFromUtf8("nonexistent2")
                }
            }
        };
        var count = await CountRows(request);
        count.Should().Be(0);
    }

    [Fact]
    public async Task Row_key_filter_with_prefix_regex()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.RowKeyRegex("usa#ny#.*")
        };
        var count = await CountRows(request);
        count.Should().Be(5);
    }

    [Fact]
    public async Task Scan_with_filter_and_limit()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.RowKeyRegex("usa#.*"),
            RowsLimit = 7
        };
        var count = await CountRows(request);
        count.Should().Be(7);
    }

    [Fact]
    public async Task Range_scan_results_are_sorted()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges = { new RowRange
                {
                    StartKeyClosed = ByteString.CopyFromUtf8("usa#"),
                    EndKeyOpen = ByteString.CopyFromUtf8("usa$")
                }}
            }
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());

        keys.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Single_row_range_exact()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges = { new RowRange
                {
                    StartKeyClosed = ByteString.CopyFromUtf8("usa#ca#002"),
                    EndKeyClosed = ByteString.CopyFromUtf8("usa#ca#002")
                }}
            }
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());
        keys.Should().ContainSingle("usa#ca#002");
    }

    [Fact]
    public async Task RowKey_exact_match()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.RowKeyExact("gbr#man#001")
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());
        keys.Should().ContainSingle("gbr#man#001");
    }

    private async Task<int> CountRows(ReadRowsRequest request)
    {
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        return count;
    }
}

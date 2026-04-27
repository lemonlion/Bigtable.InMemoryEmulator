using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for ReadRows RowsLimit interactions with different RowSet configurations.
/// Verifies limit is applied after range selection and filtering.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsLimitVariationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "rrl-var";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public ReadRowsLimitVariationTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });

        for (int i = 0; i < 20; i++)
            await Client.MutateRowAsync(TN, $"rrl-{i:D3}",
                Mutations.SetCell(CF, "c", $"val{i}", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Limit_zero_returns_all()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 0
        };
        var keys = await CollectKeys(request);
        keys.Should().HaveCount(20);
    }

    [Fact]
    public async Task Limit_one()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 1
        };
        var keys = await CollectKeys(request);
        keys.Should().ContainSingle("rrl-000");
    }

    [Fact]
    public async Task Limit_five()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 5
        };
        var keys = await CollectKeys(request);
        keys.Should().HaveCount(5);
        keys[0].Should().Be("rrl-000");
        keys[4].Should().Be("rrl-004");
    }

    [Fact]
    public async Task Limit_exceeds_total_rows()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 100
        };
        var keys = await CollectKeys(request);
        keys.Should().HaveCount(20);
    }

    [Fact]
    public async Task Limit_with_range()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 3,
            Rows = new RowSet
            {
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("rrl-005"),
                        EndKeyClosed = ByteString.CopyFromUtf8("rrl-015")
                    }
                }
            }
        };
        var keys = await CollectKeys(request);
        keys.Should().HaveCount(3);
        keys[0].Should().Be("rrl-005");
    }

    [Fact]
    public async Task Limit_with_explicit_keys()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 2,
            Rows = new RowSet
            {
                RowKeys =
                {
                    ByteString.CopyFromUtf8("rrl-000"),
                    ByteString.CopyFromUtf8("rrl-010"),
                    ByteString.CopyFromUtf8("rrl-019")
                }
            }
        };
        var keys = await CollectKeys(request);
        keys.Should().HaveCount(2);
        keys.Should().BeEquivalentTo("rrl-000", "rrl-010");
    }

    [Fact]
    public async Task Limit_with_filter()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 3,
            Filter = RowFilters.RowKeyRegex("rrl-01.*")
        };
        var keys = await CollectKeys(request);
        keys.Should().HaveCount(3);
        keys[0].Should().Be("rrl-010");
    }

    [Fact]
    public async Task Limit_with_multiple_ranges()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 4,
            Rows = new RowSet
            {
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("rrl-000"),
                        EndKeyClosed = ByteString.CopyFromUtf8("rrl-002")
                    },
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("rrl-010"),
                        EndKeyClosed = ByteString.CopyFromUtf8("rrl-012")
                    }
                }
            }
        };
        var keys = await CollectKeys(request);
        keys.Should().HaveCount(4);
        keys.Should().BeEquivalentTo("rrl-000", "rrl-001", "rrl-002", "rrl-010");
    }

    [Fact]
    public async Task Limit_with_CellsPerRowLimit_filter()
    {
        // Add multi-version data
        for (int i = 0; i < 3; i++)
            await Client.MutateRowAsync(TN, $"rrl-mv-{i}",
                Mutations.SetCell(CF, "c", $"v1", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "c", $"v2", new BigtableVersion(2000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 2,
            Filter = RowFilters.CellsPerColumnLimit(1),
            Rows = new RowSet
            {
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("rrl-mv-"),
                        EndKeyOpen = ByteString.CopyFromUtf8("rrl-mv.")
                    }
                }
            }
        };
        var keys = await CollectKeys(request);
        keys.Should().HaveCount(2);
    }

    [Fact]
    public async Task Limit_equals_row_count()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 20
        };
        var keys = await CollectKeys(request);
        keys.Should().HaveCount(20);
    }

    [Fact]
    public async Task Limit_with_RowKeyRegex_matching_subset()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 2,
            Filter = RowFilters.RowKeyRegex("rrl-00[05]")
        };
        var keys = await CollectKeys(request);
        keys.Should().HaveCount(2);
        keys.Should().BeEquivalentTo("rrl-000", "rrl-005");
    }

    [Fact]
    public async Task Limit_with_value_filter()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 2,
            Filter = RowFilters.ValueExact("val5")
        };
        var keys = await CollectKeys(request);
        keys.Should().ContainSingle("rrl-005");
    }

    [Fact]
    public async Task Limit_open_range_start()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 3,
            Rows = new RowSet
            {
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyOpen = ByteString.CopyFromUtf8("rrl-015")
                    }
                }
            }
        };
        var keys = await CollectKeys(request);
        keys.Should().HaveCount(3);
        keys[0].Should().Be("rrl-016");
    }

    [Fact]
    public async Task Limit_open_range_end()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 3,
            Rows = new RowSet
            {
                RowRanges =
                {
                    new RowRange
                    {
                        EndKeyOpen = ByteString.CopyFromUtf8("rrl-005")
                    }
                }
            }
        };
        var keys = await CollectKeys(request);
        keys.Should().HaveCount(3);
        keys[0].Should().Be("rrl-000");
    }

    [Fact]
    public async Task Keys_and_ranges_with_limit()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 3,
            Rows = new RowSet
            {
                RowKeys = { ByteString.CopyFromUtf8("rrl-019") },
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("rrl-000"),
                        EndKeyClosed = ByteString.CopyFromUtf8("rrl-002")
                    }
                }
            }
        };
        var keys = await CollectKeys(request);
        keys.Should().HaveCount(3);
        // Gets rrl-000, rrl-001, rrl-002 first (range comes before key lexicographically)
    }

    private async Task<List<string>> CollectKeys(ReadRowsRequest request)
    {
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());
        return keys;
    }
}

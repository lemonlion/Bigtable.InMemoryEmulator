using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for ReadRows with various RowSet compositions — keys, ranges,
/// overlapping ranges, and their interactions with limits and filters.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsRowSetAdvancedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "rr-rowset-adv";
    private const string CF = "cf";

    public ReadRowsRowSetAdvancedTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        // Seed 26 rows: row-a through row-z
        var entries = Enumerable.Range(0, 26)
            .Select(i =>
            {
                var ch = (char)('a' + i);
                return Mutations.CreateEntry(
                    new BigtableByteString($"row-{ch}"),
                    Mutations.SetCell(CF, "col", $"val-{ch}", new BigtableVersion(1000)));
            }).ToList();
        await _fixture.Client.MutateRowsAsync(_fixture.GetTableName(Table), entries);
    }
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<string>> ReadKeys(ReadRowsRequest request)
    {
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());
        return keys;
    }

    [Fact]
    public async Task Single_key_returns_one_row()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("row-m") } }
        };
        var keys = await ReadKeys(req);
        keys.Should().BeEquivalentTo(new[] { "row-m" });
    }

    [Fact]
    public async Task Multiple_specific_keys()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowKeys =
                {
                    ByteString.CopyFromUtf8("row-a"),
                    ByteString.CopyFromUtf8("row-m"),
                    ByteString.CopyFromUtf8("row-z")
                }
            }
        };
        var keys = await ReadKeys(req);
        keys.Should().BeEquivalentTo(new[] { "row-a", "row-m", "row-z" });
    }

    [Fact]
    public async Task Nonexistent_key_returns_empty()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("row-nonexistent") } }
        };
        var keys = await ReadKeys(req);
        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task Mixed_existing_and_nonexistent_keys()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowKeys =
                {
                    ByteString.CopyFromUtf8("row-a"),
                    ByteString.CopyFromUtf8("row-nonexistent"),
                    ByteString.CopyFromUtf8("row-z")
                }
            }
        };
        var keys = await ReadKeys(req);
        keys.Should().BeEquivalentTo(new[] { "row-a", "row-z" });
    }

    [Fact]
    public async Task Closed_open_range()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("row-c"),
                        EndKeyOpen = ByteString.CopyFromUtf8("row-f")
                    }
                }
            }
        };
        var keys = await ReadKeys(req);
        keys.Should().BeEquivalentTo(new[] { "row-c", "row-d", "row-e" });
    }

    [Fact]
    public async Task Open_closed_range()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyOpen = ByteString.CopyFromUtf8("row-c"),
                        EndKeyClosed = ByteString.CopyFromUtf8("row-f")
                    }
                }
            }
        };
        var keys = await ReadKeys(req);
        keys.Should().BeEquivalentTo(new[] { "row-d", "row-e", "row-f" });
    }

    [Fact]
    public async Task Closed_closed_range()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("row-d"),
                        EndKeyClosed = ByteString.CopyFromUtf8("row-g")
                    }
                }
            }
        };
        var keys = await ReadKeys(req);
        keys.Should().BeEquivalentTo(new[] { "row-d", "row-e", "row-f", "row-g" });
    }

    [Fact]
    public async Task Open_open_range()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyOpen = ByteString.CopyFromUtf8("row-d"),
                        EndKeyOpen = ByteString.CopyFromUtf8("row-g")
                    }
                }
            }
        };
        var keys = await ReadKeys(req);
        keys.Should().BeEquivalentTo(new[] { "row-e", "row-f" });
    }

    [Fact]
    public async Task Unbounded_start_range()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges =
                {
                    new RowRange { EndKeyOpen = ByteString.CopyFromUtf8("row-d") }
                }
            }
        };
        var keys = await ReadKeys(req);
        keys.Should().BeEquivalentTo(new[] { "row-a", "row-b", "row-c" });
    }

    [Fact]
    public async Task Unbounded_end_range()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges =
                {
                    new RowRange { StartKeyClosed = ByteString.CopyFromUtf8("row-x") }
                }
            }
        };
        var keys = await ReadKeys(req);
        keys.Should().BeEquivalentTo(new[] { "row-x", "row-y", "row-z" });
    }

    [Fact]
    public async Task Multiple_non_overlapping_ranges()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("row-a"),
                        EndKeyOpen = ByteString.CopyFromUtf8("row-c")
                    },
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("row-x"),
                        EndKeyClosed = ByteString.CopyFromUtf8("row-z")
                    }
                }
            }
        };
        var keys = await ReadKeys(req);
        keys.Should().BeEquivalentTo(new[] { "row-a", "row-b", "row-x", "row-y", "row-z" });
    }

    [Fact]
    public async Task Keys_and_ranges_combined()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowKeys = { ByteString.CopyFromUtf8("row-m") },
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("row-a"),
                        EndKeyOpen = ByteString.CopyFromUtf8("row-c")
                    }
                }
            }
        };
        var keys = await ReadKeys(req);
        keys.Should().BeEquivalentTo(new[] { "row-a", "row-b", "row-m" });
    }

    [Fact]
    public async Task Range_with_limit()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("row-a"),
                        EndKeyClosed = ByteString.CopyFromUtf8("row-z")
                    }
                }
            },
            RowsLimit = 5
        };
        var keys = await ReadKeys(req);
        keys.Should().HaveCount(5);
        keys.Should().BeEquivalentTo(new[] { "row-a", "row-b", "row-c", "row-d", "row-e" });
    }

    [Fact]
    public async Task Full_scan_with_limit_3()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 3
        };
        var keys = await ReadKeys(req);
        keys.Should().HaveCount(3);
        keys.Should().BeEquivalentTo(new[] { "row-a", "row-b", "row-c" });
    }

    [Fact]
    public async Task Full_scan_returns_all_26_rows()
    {
        var req = new ReadRowsRequest { TableNameAsTableName = TN };
        var keys = await ReadKeys(req);
        keys.Should().HaveCount(26);
    }

    [Fact]
    public async Task Full_scan_rows_in_ascending_key_order()
    {
        var req = new ReadRowsRequest { TableNameAsTableName = TN };
        var keys = await ReadKeys(req);
        keys.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Range_with_filter()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("row-a"),
                        EndKeyClosed = ByteString.CopyFromUtf8("row-e")
                    }
                }
            },
            Filter = RowFilters.ValueRegex("val-[ace]")
        };
        var keys = await ReadKeys(req);
        keys.Should().BeEquivalentTo(new[] { "row-a", "row-c", "row-e" });
    }

    [Fact]
    public async Task Duplicate_key_in_row_set_returns_once()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowKeys =
                {
                    ByteString.CopyFromUtf8("row-a"),
                    ByteString.CopyFromUtf8("row-a"),
                    ByteString.CopyFromUtf8("row-a")
                }
            }
        };
        var keys = await ReadKeys(req);
        // Duplicates should not produce duplicate output
        keys.Should().HaveCount(1);
        keys[0].Should().Be("row-a");
    }

    [Fact]
    public async Task Key_inside_range_not_duplicated()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowKeys = { ByteString.CopyFromUtf8("row-b") },
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("row-a"),
                        EndKeyClosed = ByteString.CopyFromUtf8("row-c")
                    }
                }
            }
        };
        var keys = await ReadKeys(req);
        // row-b appears in both key set and range, but should not be duplicated
        keys.Should().BeEquivalentTo(new[] { "row-a", "row-b", "row-c" });
    }

    [Fact]
    public async Task Empty_row_set_returns_all_rows()
    {
        // Ref: ReadRowsRequest — empty rows field means read all rows
        var req = new ReadRowsRequest { TableNameAsTableName = TN };
        var keys = await ReadKeys(req);
        keys.Should().HaveCount(26);
    }

    [Fact]
    [Trait(TestTraits.Target, TestTraits.GcpOnly)] // Go emulator rejects ranges where start_key > end_key
    public async Task Range_beyond_data_returns_empty()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("zzz-start"),
                        EndKeyClosed = ByteString.CopyFromUtf8("zzz-end")
                    }
                }
            }
        };
        var keys = await ReadKeys(req);
        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task Limit_larger_than_result_set()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 1000
        };
        var keys = await ReadKeys(req);
        keys.Should().HaveCount(26);
    }

    [Fact]
    public async Task Limit_1_returns_first_row()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 1
        };
        var keys = await ReadKeys(req);
        keys.Should().HaveCount(1);
        keys[0].Should().Be("row-a");
    }

    [Fact]
    public async Task Range_and_limit_and_filter_combined()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("row-a"),
                        EndKeyClosed = ByteString.CopyFromUtf8("row-z")
                    }
                }
            },
            Filter = RowFilters.ValueRegex("val-[aeiou]"),
            RowsLimit = 3
        };
        var keys = await ReadKeys(req);
        keys.Should().HaveCount(3);
        keys.Should().BeEquivalentTo(new[] { "row-a", "row-e", "row-i" });
    }
}

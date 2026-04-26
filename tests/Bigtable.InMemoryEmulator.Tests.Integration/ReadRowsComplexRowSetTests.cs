using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for ReadRows with complex RowSet compositions — multiple row keys,
/// multiple ranges, mixed keys+ranges, overlapping ranges.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsComplexRowSetTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "rrcrs-tests";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public ReadRowsComplexRowSetTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });

        // Seed rows a through j
        for (char c = 'a'; c <= 'j'; c++)
            await Client.MutateRowAsync(TN, c.ToString(),
                Mutations.SetCell(CF, "c", $"val-{c}", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Single_explicit_key()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("c") } }
        };
        var keys = await CollectKeys(request);
        keys.Should().ContainSingle("c");
    }

    [Fact]
    public async Task Multiple_explicit_keys()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowKeys =
                {
                    ByteString.CopyFromUtf8("a"),
                    ByteString.CopyFromUtf8("e"),
                    ByteString.CopyFromUtf8("j")
                }
            }
        };
        var keys = await CollectKeys(request);
        keys.Should().BeEquivalentTo("a", "e", "j");
    }

    [Fact]
    public async Task Nonexistent_keys_ignored()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowKeys =
                {
                    ByteString.CopyFromUtf8("a"),
                    ByteString.CopyFromUtf8("zzz"),
                    ByteString.CopyFromUtf8("b")
                }
            }
        };
        var keys = await CollectKeys(request);
        keys.Should().BeEquivalentTo("a", "b");
    }

    [Fact]
    public async Task Closed_closed_range()
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
                        StartKeyClosed = ByteString.CopyFromUtf8("c"),
                        EndKeyClosed = ByteString.CopyFromUtf8("f")
                    }
                }
            }
        };
        var keys = await CollectKeys(request);
        keys.Should().BeEquivalentTo("c", "d", "e", "f");
    }

    [Fact]
    public async Task Open_open_range()
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
                        StartKeyOpen = ByteString.CopyFromUtf8("c"),
                        EndKeyOpen = ByteString.CopyFromUtf8("f")
                    }
                }
            }
        };
        var keys = await CollectKeys(request);
        keys.Should().BeEquivalentTo("d", "e");
    }

    [Fact]
    public async Task Closed_open_range()
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
                        StartKeyClosed = ByteString.CopyFromUtf8("c"),
                        EndKeyOpen = ByteString.CopyFromUtf8("f")
                    }
                }
            }
        };
        var keys = await CollectKeys(request);
        keys.Should().BeEquivalentTo("c", "d", "e");
    }

    [Fact]
    public async Task Open_closed_range()
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
                        StartKeyOpen = ByteString.CopyFromUtf8("c"),
                        EndKeyClosed = ByteString.CopyFromUtf8("f")
                    }
                }
            }
        };
        var keys = await CollectKeys(request);
        keys.Should().BeEquivalentTo("d", "e", "f");
    }

    [Fact]
    public async Task Multiple_non_overlapping_ranges()
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
                        StartKeyClosed = ByteString.CopyFromUtf8("a"),
                        EndKeyClosed = ByteString.CopyFromUtf8("b")
                    },
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("h"),
                        EndKeyClosed = ByteString.CopyFromUtf8("j")
                    }
                }
            }
        };
        var keys = await CollectKeys(request);
        keys.Should().BeEquivalentTo("a", "b", "h", "i", "j");
    }

    [Fact]
    public async Task Overlapping_ranges_no_duplicates()
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
                        StartKeyClosed = ByteString.CopyFromUtf8("c"),
                        EndKeyClosed = ByteString.CopyFromUtf8("f")
                    },
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("e"),
                        EndKeyClosed = ByteString.CopyFromUtf8("h")
                    }
                }
            }
        };
        var keys = await CollectKeys(request);
        // c, d, e, f, g, h — no duplicates for e,f overlap
        keys.Should().BeEquivalentTo("c", "d", "e", "f", "g", "h");
    }

    [Fact]
    public async Task Keys_and_ranges_mixed()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowKeys = { ByteString.CopyFromUtf8("a"), ByteString.CopyFromUtf8("j") },
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("d"),
                        EndKeyClosed = ByteString.CopyFromUtf8("f")
                    }
                }
            }
        };
        var keys = await CollectKeys(request);
        keys.Should().BeEquivalentTo("a", "d", "e", "f", "j");
    }

    [Fact]
    public async Task Key_inside_range_no_duplicate()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowKeys = { ByteString.CopyFromUtf8("e") },
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("d"),
                        EndKeyClosed = ByteString.CopyFromUtf8("f")
                    }
                }
            }
        };
        var keys = await CollectKeys(request);
        keys.Should().BeEquivalentTo("d", "e", "f");
    }

    [Fact]
    public async Task Empty_range_returns_nothing()
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
                        StartKeyClosed = ByteString.CopyFromUtf8("x"),
                        EndKeyClosed = ByteString.CopyFromUtf8("z")
                    }
                }
            }
        };
        var keys = await CollectKeys(request);
        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task Open_ended_range_from_start()
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
                        EndKeyClosed = ByteString.CopyFromUtf8("c")
                    }
                }
            }
        };
        var keys = await CollectKeys(request);
        keys.Should().BeEquivalentTo("a", "b", "c");
    }

    [Fact]
    public async Task Open_ended_range_to_end()
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
                        StartKeyClosed = ByteString.CopyFromUtf8("h")
                    }
                }
            }
        };
        var keys = await CollectKeys(request);
        keys.Should().BeEquivalentTo("h", "i", "j");
    }

    [Fact]
    public async Task RowsLimit_applied_to_range()
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
                        StartKeyClosed = ByteString.CopyFromUtf8("c"),
                        EndKeyClosed = ByteString.CopyFromUtf8("j")
                    }
                }
            }
        };
        var keys = await CollectKeys(request);
        keys.Should().HaveCount(3);
        keys.Should().BeEquivalentTo("c", "d", "e");
    }

    [Fact]
    public async Task RowsLimit_applied_across_multiple_ranges()
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
                        StartKeyClosed = ByteString.CopyFromUtf8("a"),
                        EndKeyClosed = ByteString.CopyFromUtf8("b")
                    },
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("h"),
                        EndKeyClosed = ByteString.CopyFromUtf8("j")
                    }
                }
            }
        };
        var keys = await CollectKeys(request);
        keys.Should().HaveCount(4);
        keys.Should().BeEquivalentTo("a", "b", "h", "i");
    }

    [Fact]
    public async Task Filter_applied_with_range()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.CellsPerRowLimit(1),
            Rows = new RowSet
            {
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("c"),
                        EndKeyClosed = ByteString.CopyFromUtf8("e")
                    }
                }
            }
        };
        var keys = await CollectKeys(request);
        keys.Should().BeEquivalentTo("c", "d", "e");
    }

    [Fact]
    public async Task Three_separate_ranges()
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
                        StartKeyClosed = ByteString.CopyFromUtf8("a"),
                        EndKeyClosed = ByteString.CopyFromUtf8("a")
                    },
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("e"),
                        EndKeyClosed = ByteString.CopyFromUtf8("e")
                    },
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("j"),
                        EndKeyClosed = ByteString.CopyFromUtf8("j")
                    }
                }
            }
        };
        var keys = await CollectKeys(request);
        keys.Should().BeEquivalentTo("a", "e", "j");
    }

    [Fact]
    public async Task Duplicate_explicit_keys_no_duplicate_rows()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowKeys =
                {
                    ByteString.CopyFromUtf8("c"),
                    ByteString.CopyFromUtf8("c"),
                    ByteString.CopyFromUtf8("c")
                }
            }
        };
        var keys = await CollectKeys(request);
        keys.Should().ContainSingle("c");
    }

    [Fact]
    public async Task Results_ordered_lexicographically()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowKeys =
                {
                    ByteString.CopyFromUtf8("j"),
                    ByteString.CopyFromUtf8("a"),
                    ByteString.CopyFromUtf8("e")
                }
            }
        };
        var keys = await CollectKeys(request);
        keys.Should().ContainInOrder("a", "e", "j");
    }

    private async Task<List<string>> CollectKeys(ReadRowsRequest request)
    {
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());
        return keys;
    }
}

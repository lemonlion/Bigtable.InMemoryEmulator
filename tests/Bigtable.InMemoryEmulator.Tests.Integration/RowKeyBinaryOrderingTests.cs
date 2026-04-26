using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for row key binary ordering — verifying lexicographic sort with
/// various byte patterns, prefixes, and edge cases.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
///   "Rows are returned in lexicographic order of the row keys."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class RowKeyBinaryOrderingTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "rkbo-tests";
    private const string CF = "cf";

    public RowKeyBinaryOrderingTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private async Task<List<string>> ReadAllKeys(RowSet rows)
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = rows
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());
        return keys;
    }

    [Fact]
    public async Task Simple_alpha_keys_sorted()
    {
        foreach (var k in new[] { "c", "a", "b" })
            await Client.MutateRowAsync(TN, k,
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var keys = await ReadAllKeys(new RowSet
        {
            RowRanges = { new RowRange
            {
                StartKeyClosed = ByteString.CopyFromUtf8("a"),
                EndKeyClosed = ByteString.CopyFromUtf8("c")
            }}
        });
        keys.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Numeric_string_keys_sorted_lexicographically()
    {
        foreach (var k in new[] { "9", "10", "2", "1" })
            await Client.MutateRowAsync(TN, $"rkbo-num-{k}",
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var keys = await ReadAllKeys(new RowSet
        {
            RowRanges = { new RowRange
            {
                StartKeyClosed = ByteString.CopyFromUtf8("rkbo-num-"),
                EndKeyOpen = ByteString.CopyFromUtf8("rkbo-num-~")
            }}
        });
        // Lexicographic: "1" < "10" < "2" < "9"
        keys.Should().BeInAscendingOrder();
        keys[0].Should().Be("rkbo-num-1");
        keys[1].Should().Be("rkbo-num-10");
        keys[2].Should().Be("rkbo-num-2");
        keys[3].Should().Be("rkbo-num-9");
    }

    [Fact]
    public async Task Prefix_scoped_range()
    {
        foreach (var k in new[] { "rkbo-pfx-a", "rkbo-pfx-b", "rkbo-pfx-c", "rkbo-other-x" })
            await Client.MutateRowAsync(TN, k,
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var keys = await ReadAllKeys(new RowSet
        {
            RowRanges = { new RowRange
            {
                StartKeyClosed = ByteString.CopyFromUtf8("rkbo-pfx-"),
                EndKeyOpen = ByteString.CopyFromUtf8("rkbo-pfx-~")
            }}
        });
        keys.Should().HaveCount(3);
        keys.Should().AllSatisfy(k => k.Should().StartWith("rkbo-pfx-"));
    }

    [Fact]
    public async Task Empty_range_returns_nothing()
    {
        await Client.MutateRowAsync(TN, "rkbo-empty-test",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var keys = await ReadAllKeys(new RowSet
        {
            RowRanges = { new RowRange
            {
                StartKeyClosed = ByteString.CopyFromUtf8("rkbo-zzz-a"),
                EndKeyOpen = ByteString.CopyFromUtf8("rkbo-zzz-b")
            }}
        });
        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task Specific_key_set_returned_in_order()
    {
        foreach (var k in new[] { "rkbo-set-c", "rkbo-set-a", "rkbo-set-b" })
            await Client.MutateRowAsync(TN, k,
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var keys = await ReadAllKeys(new RowSet
        {
            RowKeys =
            {
                ByteString.CopyFromUtf8("rkbo-set-c"),
                ByteString.CopyFromUtf8("rkbo-set-a"),
                ByteString.CopyFromUtf8("rkbo-set-b")
            }
        });
        keys.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Mixed_case_keys_sorted_by_byte_value()
    {
        foreach (var k in new[] { "rkbo-case-a", "rkbo-case-A", "rkbo-case-B", "rkbo-case-b" })
            await Client.MutateRowAsync(TN, k,
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var keys = await ReadAllKeys(new RowSet
        {
            RowRanges = { new RowRange
            {
                StartKeyClosed = ByteString.CopyFromUtf8("rkbo-case-"),
                EndKeyOpen = ByteString.CopyFromUtf8("rkbo-case-~")
            }}
        });
        // ASCII: 'A'=0x41 < 'B'=0x42 < 'a'=0x61 < 'b'=0x62
        keys[0].Should().Be("rkbo-case-A");
        keys[1].Should().Be("rkbo-case-B");
        keys[2].Should().Be("rkbo-case-a");
        keys[3].Should().Be("rkbo-case-b");
    }

    [Fact]
    public async Task Padded_keys_sorted_correctly()
    {
        foreach (var k in new[] { "rkbo-pad-001", "rkbo-pad-010", "rkbo-pad-100" })
            await Client.MutateRowAsync(TN, k,
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var keys = await ReadAllKeys(new RowSet
        {
            RowRanges = { new RowRange
            {
                StartKeyClosed = ByteString.CopyFromUtf8("rkbo-pad-"),
                EndKeyOpen = ByteString.CopyFromUtf8("rkbo-pad-~")
            }}
        });
        keys.Should().Equal("rkbo-pad-001", "rkbo-pad-010", "rkbo-pad-100");
    }

    [Fact]
    public async Task Long_key_with_short_key()
    {
        foreach (var k in new[] { "rkbo-len-a", "rkbo-len-ab", "rkbo-len-abc" })
            await Client.MutateRowAsync(TN, k,
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var keys = await ReadAllKeys(new RowSet
        {
            RowRanges = { new RowRange
            {
                StartKeyClosed = ByteString.CopyFromUtf8("rkbo-len-"),
                EndKeyOpen = ByteString.CopyFromUtf8("rkbo-len-~")
            }}
        });
        keys.Should().Equal("rkbo-len-a", "rkbo-len-ab", "rkbo-len-abc");
    }

    [Fact]
    public async Task Special_characters_in_keys()
    {
        foreach (var k in new[] { "rkbo-sp-!", "rkbo-sp-#", "rkbo-sp-$", "rkbo-sp-@" })
            await Client.MutateRowAsync(TN, k,
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var keys = await ReadAllKeys(new RowSet
        {
            RowRanges = { new RowRange
            {
                StartKeyClosed = ByteString.CopyFromUtf8("rkbo-sp-"),
                EndKeyOpen = ByteString.CopyFromUtf8("rkbo-sp-~")
            }}
        });
        keys.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Open_start_key_excludes_boundary()
    {
        foreach (var k in new[] { "rkbo-open-a", "rkbo-open-b", "rkbo-open-c" })
            await Client.MutateRowAsync(TN, k,
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var keys = await ReadAllKeys(new RowSet
        {
            RowRanges = { new RowRange
            {
                StartKeyOpen = ByteString.CopyFromUtf8("rkbo-open-a"),
                EndKeyClosed = ByteString.CopyFromUtf8("rkbo-open-c")
            }}
        });
        keys.Should().NotContain("rkbo-open-a");
        keys.Should().Contain("rkbo-open-b");
        keys.Should().Contain("rkbo-open-c");
    }

    [Fact]
    public async Task Closed_start_open_end()
    {
        foreach (var k in new[] { "rkbo-co-a", "rkbo-co-b", "rkbo-co-c" })
            await Client.MutateRowAsync(TN, k,
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var keys = await ReadAllKeys(new RowSet
        {
            RowRanges = { new RowRange
            {
                StartKeyClosed = ByteString.CopyFromUtf8("rkbo-co-a"),
                EndKeyOpen = ByteString.CopyFromUtf8("rkbo-co-c")
            }}
        });
        keys.Should().Contain("rkbo-co-a");
        keys.Should().Contain("rkbo-co-b");
        keys.Should().NotContain("rkbo-co-c");
    }

    [Fact]
    public async Task Multiple_ranges_in_rowset()
    {
        foreach (var k in new[] { "rkbo-mr-a", "rkbo-mr-b", "rkbo-mr-m", "rkbo-mr-n", "rkbo-mr-z" })
            await Client.MutateRowAsync(TN, k,
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var keys = await ReadAllKeys(new RowSet
        {
            RowRanges =
            {
                new RowRange
                {
                    StartKeyClosed = ByteString.CopyFromUtf8("rkbo-mr-a"),
                    EndKeyClosed = ByteString.CopyFromUtf8("rkbo-mr-b")
                },
                new RowRange
                {
                    StartKeyClosed = ByteString.CopyFromUtf8("rkbo-mr-m"),
                    EndKeyClosed = ByteString.CopyFromUtf8("rkbo-mr-n")
                }
            }
        });
        keys.Should().Contain("rkbo-mr-a");
        keys.Should().Contain("rkbo-mr-b");
        keys.Should().Contain("rkbo-mr-m");
        keys.Should().Contain("rkbo-mr-n");
        keys.Should().NotContain("rkbo-mr-z");
    }

    [Fact]
    public async Task RowsLimit_with_ordering()
    {
        foreach (var k in new[] { "rkbo-lim-a", "rkbo-lim-b", "rkbo-lim-c", "rkbo-lim-d" })
            await Client.MutateRowAsync(TN, k,
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 2,
            Rows = new RowSet
            {
                RowRanges = { new RowRange
                {
                    StartKeyClosed = ByteString.CopyFromUtf8("rkbo-lim-"),
                    EndKeyOpen = ByteString.CopyFromUtf8("rkbo-lim-~")
                }}
            }
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());

        keys.Should().HaveCount(2);
        keys[0].Should().Be("rkbo-lim-a");
        keys[1].Should().Be("rkbo-lim-b");
    }

    [Fact]
    public async Task Keys_and_ranges_combined()
    {
        foreach (var k in new[] { "rkbo-kr-a", "rkbo-kr-b", "rkbo-kr-c" })
            await Client.MutateRowAsync(TN, k,
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var keys = await ReadAllKeys(new RowSet
        {
            RowKeys = { ByteString.CopyFromUtf8("rkbo-kr-c") },
            RowRanges = { new RowRange
            {
                StartKeyClosed = ByteString.CopyFromUtf8("rkbo-kr-a"),
                EndKeyOpen = ByteString.CopyFromUtf8("rkbo-kr-b")
            }}
        });
        keys.Should().Contain("rkbo-kr-a");
        keys.Should().Contain("rkbo-kr-c");
        keys.Should().NotContain("rkbo-kr-b");
    }

    [Fact]
    public async Task Nonexistent_key_in_key_set_is_ignored()
    {
        await Client.MutateRowAsync(TN, "rkbo-exist",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var keys = await ReadAllKeys(new RowSet
        {
            RowKeys =
            {
                ByteString.CopyFromUtf8("rkbo-exist"),
                ByteString.CopyFromUtf8("rkbo-ghost")
            }
        });
        keys.Should().HaveCount(1);
        keys[0].Should().Be("rkbo-exist");
    }
}

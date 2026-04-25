using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for RowSet composition: combining row keys with row ranges, multiple ranges,
/// overlapping ranges, empty sets.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowset
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class RowSetCompositionAdvancedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public RowSetCompositionAdvancedTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync("rowset-adv", new[] { CF });
        var tn = _fixture.GetTableName("rowset-adv");
        var entries = Enumerable.Range(0, 26).Select(i =>
            Mutations.CreateEntry(((char)('a' + i)).ToString(),
                Mutations.SetCell(CF, "c", $"v{(char)('a' + i)}", new BigtableVersion(1000)))).ToArray();
        await _fixture.Client.MutateRowsAsync(tn, entries);
    }
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("rowset-adv");

    [Fact]
    public async Task Keys_and_ranges_combined()
    {
        var rowSet = new RowSet
        {
            RowKeys = { ByteString.CopyFromUtf8("a"), ByteString.CopyFromUtf8("z") },
            RowRanges = { RowRange.ClosedOpen("m", "p") }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);

        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().Contain("a");
        keys.Should().Contain("z");
        keys.Should().Contain("m");
        keys.Should().Contain("n");
        keys.Should().Contain("o");
    }

    [Fact]
    public async Task Multiple_non_overlapping_ranges()
    {
        var rowSet = new RowSet
        {
            RowRanges =
            {
                RowRange.ClosedOpen("a", "c"), // a, b
                RowRange.ClosedOpen("m", "o"), // m, n
                RowRange.ClosedOpen("x", "z~")  // x, y, z
            }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);

        rows.Should().HaveCount(7);
    }

    [Fact]
    public async Task Overlapping_ranges_dedup()
    {
        var rowSet = new RowSet
        {
            RowRanges =
            {
                RowRange.ClosedOpen("a", "f"), // a-e
                RowRange.ClosedOpen("c", "h")  // c-g (overlaps with a-f)
            }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);

        // Should be a-g (union), deduplicated
        var keys = rows.Select(r => r.Key.ToStringUtf8()).Distinct().ToList();
        keys.Should().HaveCount(rows.Count); // No duplicates in output
    }

    [Fact]
    public async Task Empty_row_set_returns_all()
    {
        // No row keys and no ranges = full table scan
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, new RowSet()))
            rows.Add(row);
        rows.Should().HaveCount(26);
    }

    [Fact]
    public async Task Row_key_not_found_in_set()
    {
        var rowSet = RowSet.FromRowKeys("not-a-key", "also-missing");
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Range_with_no_matching_rows()
    {
        var rowSet = new RowSet
        {
            RowRanges = { RowRange.ClosedOpen("zzz", "zzz~") }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Open_start_range()
    {
        var rowSet = new RowSet
        {
            RowRanges =
            {
                new RowRange
                {
                    EndKeyOpen = ByteString.CopyFromUtf8("c")
                }
            }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);

        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().Contain("a");
        keys.Should().Contain("b");
        keys.Should().NotContain("c");
    }

    [Fact]
    public async Task Open_end_range()
    {
        var rowSet = new RowSet
        {
            RowRanges =
            {
                new RowRange
                {
                    StartKeyClosed = ByteString.CopyFromUtf8("x")
                }
            }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);

        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().Contain("x");
        keys.Should().Contain("y");
        keys.Should().Contain("z");
    }

    [Fact]
    public async Task Closed_closed_range()
    {
        var rowSet = new RowSet
        {
            RowRanges =
            {
                new RowRange
                {
                    StartKeyClosed = ByteString.CopyFromUtf8("d"),
                    EndKeyClosed = ByteString.CopyFromUtf8("f")
                }
            }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);

        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().BeEquivalentTo("d", "e", "f");
    }

    [Fact]
    public async Task Open_open_range()
    {
        var rowSet = new RowSet
        {
            RowRanges =
            {
                new RowRange
                {
                    StartKeyOpen = ByteString.CopyFromUtf8("d"),
                    EndKeyOpen = ByteString.CopyFromUtf8("g")
                }
            }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);

        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().BeEquivalentTo("e", "f");
    }

    [Fact]
    public async Task Duplicate_row_keys_in_set_dedup()
    {
        var rowSet = RowSet.FromRowKeys("a", "a", "b", "b", "c");
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);

        // SDK/server may or may not dedup, but results should not contain duplicates
        var uniqueKeys = rows.Select(r => r.Key.ToStringUtf8()).Distinct().ToList();
        uniqueKeys.Should().HaveCount(rows.Count);
    }

    [Fact]
    public async Task Key_overlapping_with_range()
    {
        // Key "m" is also within range [l, o)
        var rowSet = new RowSet
        {
            RowKeys = { ByteString.CopyFromUtf8("m") },
            RowRanges = { RowRange.ClosedOpen("l", "o") }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);

        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().Contain("l");
        keys.Should().Contain("m");
        keys.Should().Contain("n");
        // m should not appear twice
        keys.Count(k => k == "m").Should().Be(1);
    }
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for ReadRows with various RowSet configurations — ranges, specific keys,
/// combinations, boundary conditions, and pagination patterns.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsRangeStressTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "range-stress";
    private const string CF = "cf";

    public ReadRowsRangeStressTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        // Seed 50 rows: rng-000..rng-049
        for (int i = 0; i < 50; i++)
            await Client.MutateRowAsync(TN, $"rng-{i:D3}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null, long? limit = null)
    {
        var list = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: rows, filter: filter, rowsLimit: limit))
            list.Add(row);
        return list;
    }

    #region Single ranges

    [Fact]
    public async Task ClosedOpen_includes_start_excludes_end()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("rng-010", "rng-020")));
        rows.Should().HaveCount(10);
        rows.First().Key.ToStringUtf8().Should().Be("rng-010");
        rows.Last().Key.ToStringUtf8().Should().Be("rng-019");
    }

    [Fact]
    public async Task Closed_includes_both()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.Closed("rng-010", "rng-015")));
        rows.Should().HaveCount(6);
        rows.First().Key.ToStringUtf8().Should().Be("rng-010");
        rows.Last().Key.ToStringUtf8().Should().Be("rng-015");
    }

    [Fact]
    public async Task Open_excludes_both()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.Open("rng-010", "rng-015")));
        rows.Should().HaveCount(4); // 011, 012, 013, 014
    }

    [Fact]
    public async Task OpenClosed_excludes_start_includes_end()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.OpenClosed("rng-010", "rng-015")));
        rows.Should().HaveCount(5); // 011..015
        rows.First().Key.ToStringUtf8().Should().Be("rng-011");
        rows.Last().Key.ToStringUtf8().Should().Be("rng-015");
    }

    [Fact]
    public async Task Unbounded_start_to_key()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen(null, "rng-005")));
        rows.Should().HaveCount(5); // rng-000..004
    }

    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task Key_to_unbounded_end()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.Closed("rng-045", null)));
        rows.Should().HaveCount(5); // rng-045..049
    }

    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task Fully_unbounded_returns_all()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.Closed((BigtableByteString?)null, null)));
        rows.Should().HaveCount(50);
    }

    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task Empty_range_returns_nothing()
    {
        // Start > end in lexicographic order
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("rng-020", "rng-010")));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Range_past_all_data()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("zzz-000", "zzz-100")));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Range_before_all_data()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("aaa-000", "aaa-100")));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Single_row_range()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.Closed("rng-025", "rng-025")));
        rows.Should().ContainSingle();
    }

    #endregion

    #region Multiple ranges

    [Fact]
    public async Task Two_non_overlapping_ranges()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(
            RowRange.ClosedOpen("rng-000", "rng-005"),
            RowRange.ClosedOpen("rng-045", "rng-050")));
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task Three_non_overlapping_ranges()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(
            RowRange.ClosedOpen("rng-000", "rng-003"),
            RowRange.ClosedOpen("rng-020", "rng-023"),
            RowRange.ClosedOpen("rng-040", "rng-043")));
        rows.Should().HaveCount(9);
    }

    [Fact]
    public async Task Overlapping_ranges_no_duplicates()
    {
        // Ref: Overlapping ranges should not produce duplicate rows
        var rows = await ReadAll(RowSet.FromRowRanges(
            RowRange.ClosedOpen("rng-010", "rng-020"),
            RowRange.ClosedOpen("rng-015", "rng-025")));
        // Union: rng-010..024 = 15 rows
        rows.Should().HaveCount(15);
    }

    [Fact]
    public async Task Adjacent_ranges()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(
            RowRange.ClosedOpen("rng-000", "rng-010"),
            RowRange.ClosedOpen("rng-010", "rng-020")));
        rows.Should().HaveCount(20);
    }

    [Fact]
    public async Task Contained_range()
    {
        // Inner range entirely within outer
        var rows = await ReadAll(RowSet.FromRowRanges(
            RowRange.ClosedOpen("rng-000", "rng-030"),
            RowRange.ClosedOpen("rng-010", "rng-020")));
        rows.Should().HaveCount(30); // No duplicates
    }

    #endregion

    #region Specific keys

    [Fact]
    public async Task Single_key()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("rng-025"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Multiple_keys()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("rng-000", "rng-025", "rng-049"));
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Nonexistent_key()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("rng-999"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Mix_existent_and_nonexistent_keys()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("rng-000", "rng-999", "rng-049"));
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Duplicate_keys_no_duplicate_rows()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("rng-010", "rng-010", "rng-010"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Keys_returned_in_lexicographic_order()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("rng-049", "rng-000", "rng-025"));
        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Many_specific_keys()
    {
        var keyList = Enumerable.Range(0, 50).Select(i => new BigtableByteString($"rng-{i:D3}")).ToArray();
        var rows = await ReadAll(RowSet.FromRowKeys(keyList));
        rows.Should().HaveCount(50);
    }

    #endregion

    #region Keys + ranges combined

    [Fact]
    public async Task Keys_and_ranges_union()
    {
        var rowSet = new RowSet();
        rowSet.RowKeys.Add(ByteString.CopyFromUtf8("rng-000"));
        rowSet.RowKeys.Add(ByteString.CopyFromUtf8("rng-049"));
        rowSet.RowRanges.Add(new RowRange
        {
            StartKeyClosed = ByteString.CopyFromUtf8("rng-020"),
            EndKeyOpen = ByteString.CopyFromUtf8("rng-025")
        });
        var rows = await ReadAll(rowSet);
        rows.Should().HaveCount(7); // 2 specific + 5 from range
    }

    [Fact]
    public async Task Key_overlapping_with_range_no_duplicate()
    {
        var rowSet = new RowSet();
        rowSet.RowKeys.Add(ByteString.CopyFromUtf8("rng-022"));
        rowSet.RowRanges.Add(new RowRange
        {
            StartKeyClosed = ByteString.CopyFromUtf8("rng-020"),
            EndKeyOpen = ByteString.CopyFromUtf8("rng-025")
        });
        var rows = await ReadAll(rowSet);
        rows.Should().HaveCount(5); // rng-022 is in range, no dup
    }

    #endregion

    #region Limit with ranges

    [Fact]
    public async Task Limit_with_range()
    {
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("rng-000", "rng-050")),
            limit: 10);
        rows.Should().HaveCount(10);
        rows.First().Key.ToStringUtf8().Should().Be("rng-000");
    }

    [Fact]
    public async Task Limit_with_multiple_ranges()
    {
        var rows = await ReadAll(
            RowSet.FromRowRanges(
                RowRange.ClosedOpen("rng-000", "rng-010"),
                RowRange.ClosedOpen("rng-040", "rng-050")),
            limit: 5);
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task Limit_with_specific_keys()
    {
        var rows = await ReadAll(
            RowSet.FromRowKeys("rng-000", "rng-010", "rng-020", "rng-030", "rng-040"),
            limit: 3);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Limit_exceeds_total_returns_all()
    {
        var rows = await ReadAll(limit: 1000);
        rows.Should().HaveCount(50);
    }

    [Fact]
    public async Task Limit_0_returns_all()
    {
        // Ref: rows_limit of 0 means no limit
        var rows = await ReadAll(limit: 0);
        rows.Should().HaveCount(50);
    }

    [Fact]
    public async Task Limit_1_returns_first()
    {
        var rows = await ReadAll(limit: 1);
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("rng-000");
    }

    #endregion

    #region Filter with ranges

    [Fact]
    public async Task Range_with_value_filter()
    {
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("rng-000", "rng-010")),
            RowFilters.ValueExact("v5"));
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("rng-005");
    }

    [Fact]
    public async Task Range_with_column_limit()
    {
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("rng-000", "rng-005")),
            RowFilters.CellsPerColumnLimit(1));
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task Range_with_strip_value()
    {
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("rng-000", "rng-003")),
            RowFilters.StripValueTransformer());
        rows.Should().HaveCount(3);
        rows.SelectMany(r => r.Families).SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Should().AllSatisfy(c => c.Value.Length.Should().Be(0));
    }

    [Fact]
    public async Task Filter_matching_nothing_with_range()
    {
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("rng-000", "rng-010")),
            RowFilters.ValueExact("NONEXISTENT"));
        rows.Should().BeEmpty();
    }

    #endregion

    #region Null/empty RowSet

    [Fact]
    public async Task Null_rowset_returns_all()
    {
        var rows = await ReadAll(rows: null);
        rows.Should().HaveCount(50);
    }

    [Fact]
    public async Task All_rows_in_lexicographic_order()
    {
        var rows = await ReadAll();
        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().BeInAscendingOrder();
    }

    #endregion

    #region Reversed reads

    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task Reversed_returns_descending_order()
    {
        // Ref: ReadRowsRequest.reversed
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Reversed = true,
            RowsLimit = 5,
        };
        var rows = new List<Row>();
        var stream = _fixture.ServiceApiClient.ReadRows(request);
        var chunks = new List<ReadRowsResponse.Types.CellChunk>();
        var responseStream = stream.GetResponseStream();
        await foreach (var resp in responseStream)
            chunks.AddRange(resp.Chunks);

        // Verify we got chunks in descending key order
        var seenKeys = new List<string>();
        foreach (var chunk in chunks)
        {
            if (chunk.RowKey != null && chunk.RowKey.Length > 0)
            {
                var key = chunk.RowKey.ToStringUtf8();
                if (seenKeys.Count == 0 || seenKeys.Last() != key)
                    seenKeys.Add(key);
            }
        }
        seenKeys.Should().BeInDescendingOrder();
    }

    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task Reversed_with_range()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Reversed = true,
            Rows = new RowSet(),
            RowsLimit = 3,
        };
        request.Rows.RowRanges.Add(new RowRange
        {
            StartKeyClosed = ByteString.CopyFromUtf8("rng-010"),
            EndKeyOpen = ByteString.CopyFromUtf8("rng-020"),
        });
        var rows = new List<string>();
        var stream = _fixture.ServiceApiClient.ReadRows(request);
        var responseStream = stream.GetResponseStream();
        await foreach (var resp in responseStream)
            foreach (var chunk in resp.Chunks)
                if (chunk.RowKey != null && chunk.RowKey.Length > 0)
                {
                    var key = chunk.RowKey.ToStringUtf8();
                    if (rows.Count == 0 || rows.Last() != key)
                        rows.Add(key);
                }
        rows.Should().BeInDescendingOrder();
    }

    #endregion
}

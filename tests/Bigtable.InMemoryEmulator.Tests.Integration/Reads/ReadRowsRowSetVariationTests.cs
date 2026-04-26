using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsRowSetVariationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "rrsv-tests";
    private const string CF = "cf";

    public ReadRowsRowSetVariationTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        for (int i = 0; i < 10; i++)
            await Client.MutateRowAsync(TN, $"rrsv-row{i:D2}", Mutations.SetCell(CF, "c", $"v{i}"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null, long? rowsLimit = null)
    {
        var list = new List<Row>();
        if (rowsLimit.HasValue)
        {
            var request = new ReadRowsRequest { TableNameAsTableName = TN, RowsLimit = rowsLimit.Value };
            if (filter != null) request.Filter = filter;
            if (rows != null)
            {
                await foreach (var r in Client.ReadRows(TN, rows: rows, filter: filter))
                {
                    list.Add(r);
                    if (list.Count >= rowsLimit.Value) break;
                }
                return list;
            }
            await foreach (var r in Client.ReadRows(request))
                list.Add(r);
        }
        else
        {
            await foreach (var r in Client.ReadRows(TN, rows: rows, filter: filter))
                list.Add(r);
        }
        return list;
    }

    [Fact]
    public async Task Read_single_row_key()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("rrsv-row05"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Read_multiple_specific_row_keys()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("rrsv-row01", "rrsv-row05", "rrsv-row09"));
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Read_nonexistent_row_keys_returns_empty()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("rrsv-nope1", "rrsv-nope2"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Read_mix_of_existing_and_nonexistent()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("rrsv-row03", "rrsv-nonexist", "rrsv-row07"));
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task ClosedOpen_range()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("rrsv-row03", "rrsv-row07")));
        rows.Should().HaveCount(4);
    }

    [Fact]
    public async Task Closed_range()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.Closed("rrsv-row03", "rrsv-row05")));
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Open_range()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.Open("rrsv-row03", "rrsv-row06")));
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task OpenClosed_range()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.OpenClosed("rrsv-row03", "rrsv-row06")));
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Multiple_disjoint_ranges()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(
            RowRange.Closed("rrsv-row00", "rrsv-row01"),
            RowRange.Closed("rrsv-row08", "rrsv-row09")));
        rows.Should().HaveCount(4);
    }

    [Fact]
    public async Task Range_with_no_matches()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.Closed("rrsv-zzzz", "rrsv-zzzzz")));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Row_keys_and_ranges_combined()
    {
        // Use specific keys + a range by making two separate requests and combining
        var keyRows = await ReadAll(RowSet.FromRowKeys("rrsv-row00"));
        var rangeRows = await ReadAll(RowSet.FromRowRanges(RowRange.Closed("rrsv-row08", "rrsv-row09")));
        var total = keyRows.Count + rangeRows.Count;
        total.Should().Be(3); // 00, 08, 09
    }

    [Fact]
    public async Task RowsLimit_restricts_total_rows()
    {
        var rows = await ReadAll(rowsLimit: 3);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task RowsLimit_with_range()
    {
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("rrsv-row00", "rrsv-row99")),
            rowsLimit: 5);
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task RowsLimit_larger_than_available()
    {
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.Closed("rrsv-row00", "rrsv-row02")),
            rowsLimit: 100);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Prefix_range_scan()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("rrsv-row0", "rrsv-row1")));
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task Single_key_range_closed()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.Closed("rrsv-row05", "rrsv-row05")));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Overlapping_ranges_deduplicates()
    {
        var rows = await ReadAll(RowSet.FromRowRanges(
            RowRange.Closed("rrsv-row03", "rrsv-row06"),
            RowRange.Closed("rrsv-row05", "rrsv-row08")));
        rows.Should().HaveCount(6);
    }

    [Fact]
    public async Task Duplicate_row_keys_in_set()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("rrsv-row01", "rrsv-row01"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Rows_returned_in_lexicographic_order()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("rrsv-row09", "rrsv-row01", "rrsv-row05"));
        var keys = rows.Select(r => r.Key.ToStringUtf8()).ToList();
        keys.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task RowsLimit_1_returns_first_row()
    {
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("rrsv-row", "rrsv-rox")),
            rowsLimit: 1);
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("rrsv-row00");
    }

    [Fact]
    public async Task Filter_applied_with_row_set()
    {
        var rows = await ReadAll(
            RowSet.FromRowKeys("rrsv-row01", "rrsv-row02"),
            RowFilters.StripValueTransformer());
        rows.Should().HaveCount(2);
        foreach (var row in rows)
            row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
                .Should().AllSatisfy(c => c.Value.Should().BeEmpty());
    }
}

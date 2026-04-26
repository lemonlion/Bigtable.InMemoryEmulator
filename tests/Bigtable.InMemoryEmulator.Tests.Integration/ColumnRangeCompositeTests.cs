using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for ColumnRange filter with diverse boundary types, combinations
/// with other filters, and edge cases.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#columnrange
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ColumnRangeCompositeTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "crc-tests";
    private const string CF = "cf";

    public ColumnRangeCompositeTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private async Task<List<string>> ReadQualifiers(string key, RowFilter filter)
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = filter,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8(key) } }
        };
        var quals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
                quals.Add(c.Qualifier.ToStringUtf8());
        return quals;
    }

    [Fact]
    public async Task Closed_range_includes_endpoints()
    {
        await Client.MutateRowAsync(TN, "crc-closed",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "d", "v", new BigtableVersion(1000)));

        var quals = await ReadQualifiers("crc-closed",
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "b", "c")));
        quals.Should().Contain("b");
        quals.Should().Contain("c");
        quals.Should().NotContain("a");
        quals.Should().NotContain("d");
    }

    [Fact]
    public async Task Open_range_excludes_endpoints()
    {
        await Client.MutateRowAsync(TN, "crc-open",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "d", "v", new BigtableVersion(1000)));

        var quals = await ReadQualifiers("crc-open",
            RowFilters.ColumnRange(ColumnRange.Open(CF, "a", "d")));
        quals.Should().Contain("b");
        quals.Should().Contain("c");
        quals.Should().NotContain("a");
        quals.Should().NotContain("d");
    }

    [Fact]
    public async Task ClosedOpen_boundary()
    {
        await Client.MutateRowAsync(TN, "crc-co",
            Mutations.SetCell(CF, "x", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "y", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "z", "v", new BigtableVersion(1000)));

        var quals = await ReadQualifiers("crc-co",
            RowFilters.ColumnRange(ColumnRange.ClosedOpen(CF, "x", "z")));
        quals.Should().Contain("x");
        quals.Should().Contain("y");
        quals.Should().NotContain("z");
    }

    [Fact]
    public async Task OpenClosed_boundary()
    {
        await Client.MutateRowAsync(TN, "crc-oc",
            Mutations.SetCell(CF, "x", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "y", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "z", "v", new BigtableVersion(1000)));

        var quals = await ReadQualifiers("crc-oc",
            RowFilters.ColumnRange(ColumnRange.OpenClosed(CF, "x", "z")));
        quals.Should().NotContain("x");
        quals.Should().Contain("y");
        quals.Should().Contain("z");
    }

    [Fact]
    public async Task Single_qualifier_closed_range()
    {
        await Client.MutateRowAsync(TN, "crc-single",
            Mutations.SetCell(CF, "m", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "n", "v", new BigtableVersion(1000)));

        var quals = await ReadQualifiers("crc-single",
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "m", "m")));
        quals.Should().HaveCount(1);
        quals[0].Should().Be("m");
    }

    [Fact]
    public async Task Chained_with_value_exact()
    {
        await Client.MutateRowAsync(TN, "crc-chain-val",
            Mutations.SetCell(CF, "a", "yes", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "no", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "yes", new BigtableVersion(1000)));

        var filter = RowFilters.Chain(
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "c")),
            RowFilters.ValueExact("yes"));
        var quals = await ReadQualifiers("crc-chain-val", filter);
        quals.Should().Contain("a");
        quals.Should().Contain("c");
        quals.Should().NotContain("b");
    }

    [Fact]
    public async Task Interleave_two_column_ranges()
    {
        await Client.MutateRowAsync(TN, "crc-ilv",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "m", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "n", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "z", "v", new BigtableVersion(1000)));

        var filter = RowFilters.Interleave(
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "b")),
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "m", "n")));
        var quals = await ReadQualifiers("crc-ilv", filter);
        quals.Should().Contain("a");
        quals.Should().Contain("b");
        quals.Should().Contain("m");
        quals.Should().Contain("n");
        quals.Should().NotContain("z");
    }

    [Fact]
    public async Task ColumnRange_with_cells_per_row_limit()
    {
        await Client.MutateRowAsync(TN, "crc-cprl",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var filter = RowFilters.Chain(
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "c")),
            RowFilters.CellsPerRowLimit(2));
        var quals = await ReadQualifiers("crc-cprl", filter);
        quals.Should().HaveCount(2);
    }

    [Fact]
    public async Task ColumnRange_with_cells_per_column_limit()
    {
        await Client.MutateRowAsync(TN, "crc-cpcl",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "crc-cpcl",
            Mutations.SetCell(CF, "a", "v2", new BigtableVersion(2000)));

        var filter = RowFilters.Chain(
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "a")),
            RowFilters.CellsPerColumnLimit(1));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = filter,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("crc-cpcl") } }
        };
        var cellCount = 0;
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
                cellCount += c.Cells.Count;
        cellCount.Should().Be(1);
    }

    [Fact]
    public async Task ColumnRange_with_strip_value()
    {
        await Client.MutateRowAsync(TN, "crc-strip",
            Mutations.SetCell(CF, "a", "data", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "data", new BigtableVersion(1000)));

        var filter = RowFilters.Chain(
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "b")),
            RowFilters.StripValueTransformer());

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = filter,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("crc-strip") } }
        };
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                cell.Value.ToStringUtf8().Should().BeEmpty();
    }

    [Fact]
    public async Task ColumnRange_with_label()
    {
        await Client.MutateRowAsync(TN, "crc-label",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)));

        var filter = RowFilters.Chain(
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "z")),
            new RowFilter { ApplyLabelTransformer = "col-range" });

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = filter,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("crc-label") } }
        };
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                cell.Labels.Should().Contain("col-range");
    }

    [Fact]
    public async Task ColumnRange_no_matching_columns()
    {
        await Client.MutateRowAsync(TN, "crc-nomatch",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)));

        var quals = await ReadQualifiers("crc-nomatch",
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "x", "z")));
        quals.Should().BeEmpty();
    }

    [Fact]
    public async Task ColumnRange_full_alphabet_range()
    {
        // Create columns a through e
        for (char ch = 'a'; ch <= 'e'; ch++)
            await Client.MutateRowAsync(TN, "crc-alpha",
                Mutations.SetCell(CF, ch.ToString(), "v", new BigtableVersion(1000)));

        var quals = await ReadQualifiers("crc-alpha",
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "e")));
        quals.Should().HaveCount(5);
    }

    [Fact]
    public async Task ColumnRange_on_specific_family_only()
    {
        await Client.MutateRowAsync(TN, "crc-fam",
            Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "col", "v2", new BigtableVersion(1000)));

        // ColumnRange.Closed includes family in its definition
        var quals = await ReadQualifiers("crc-fam",
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "z")));
        quals.Should().HaveCount(1); // Only from CF, not cf2
    }

    [Fact]
    public async Task ColumnRange_in_condition_predicate()
    {
        await Client.MutateRowAsync(TN, "crc-cond",
            Mutations.SetCell(CF, "a", "val", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "val", new BigtableVersion(1000)));

        var filter = RowFilters.Condition(
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "a")),
            RowFilters.PassAllFilter(),
            RowFilters.BlockAllFilter());

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = filter,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("crc-cond") } }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);
        rows.Should().HaveCount(1); // predicate matched "a", so passall
    }

    [Fact]
    public async Task ColumnRange_with_timestamp_filter()
    {
        var ts1 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var ts2 = new DateTime(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        await Client.MutateRowAsync(TN, "crc-ts",
            Mutations.SetCell(CF, "early", "v", new BigtableVersion(ts1)),
            Mutations.SetCell(CF, "late", "v", new BigtableVersion(ts2)));

        var filter = RowFilters.Chain(
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "z")),
            RowFilters.TimestampRange(null, ts2));
        var quals = await ReadQualifiers("crc-ts", filter);
        quals.Should().Contain("early");
        quals.Should().NotContain("late");
    }

    [Fact]
    public async Task ColumnRange_chained_twice_narrows()
    {
        await Client.MutateRowAsync(TN, "crc-narrow",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var filter = RowFilters.Chain(
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "c")),
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "b", "c")));
        var quals = await ReadQualifiers("crc-narrow", filter);
        quals.Should().Contain("b");
        quals.Should().Contain("c");
        quals.Should().NotContain("a");
    }
}

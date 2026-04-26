using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for ValueRange filter combined with other filter types.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#valuerange
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ValueRangeFilterComboTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "vrfc-tests";
    private const string CF = "cf";

    public ValueRangeFilterComboTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private async Task<List<string>> ReadValues(string key, RowFilter filter)
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = filter,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8(key) } }
        };
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                vals.Add(cell.Value.ToStringUtf8());
        return vals;
    }

    [Fact]
    public async Task Closed_range_includes_both_endpoints()
    {
        await Client.MutateRowAsync(TN, "vrfc-closed",
            Mutations.SetCell(CF, "c", "apple", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "d", "banana", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "e", "cherry", new BigtableVersion(1000)));

        var vals = await ReadValues("vrfc-closed", RowFilters.ValueRange(ValueRange.Closed("apple", "banana")));
        vals.Should().Contain("apple");
        vals.Should().Contain("banana");
        vals.Should().NotContain("cherry");
    }

    [Fact]
    public async Task Open_range_excludes_both_endpoints()
    {
        await Client.MutateRowAsync(TN, "vrfc-open",
            Mutations.SetCell(CF, "a", "a", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "b", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "c", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "d", "d", new BigtableVersion(1000)));

        var vals = await ReadValues("vrfc-open", RowFilters.ValueRange(ValueRange.Open("a", "d")));
        vals.Should().NotContain("a");
        vals.Should().Contain("b");
        vals.Should().Contain("c");
        vals.Should().NotContain("d");
    }

    [Fact]
    public async Task ClosedOpen_includes_start_excludes_end()
    {
        await Client.MutateRowAsync(TN, "vrfc-co",
            Mutations.SetCell(CF, "a", "x", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "y", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "z", new BigtableVersion(1000)));

        var vals = await ReadValues("vrfc-co", RowFilters.ValueRange(ValueRange.ClosedOpen("x", "z")));
        vals.Should().Contain("x");
        vals.Should().Contain("y");
        vals.Should().NotContain("z");
    }

    [Fact]
    public async Task OpenClosed_excludes_start_includes_end()
    {
        await Client.MutateRowAsync(TN, "vrfc-oc",
            Mutations.SetCell(CF, "a", "x", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "y", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "z", new BigtableVersion(1000)));

        var vals = await ReadValues("vrfc-oc", RowFilters.ValueRange(ValueRange.OpenClosed("x", "z")));
        vals.Should().NotContain("x");
        vals.Should().Contain("y");
        vals.Should().Contain("z");
    }

    [Fact]
    public async Task ValueRange_chained_with_family_filter()
    {
        await Client.MutateRowAsync(TN, "vrfc-chain-fam",
            Mutations.SetCell(CF, "c", "hello", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "hello", new BigtableVersion(1000)));

        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.ValueRange(ValueRange.Closed("a", "z")));
        var vals = await ReadValues("vrfc-chain-fam", filter);
        vals.Should().HaveCount(1);
        vals[0].Should().Be("hello");
    }

    [Fact]
    public async Task ValueRange_chained_with_column_qualifier()
    {
        await Client.MutateRowAsync(TN, "vrfc-chain-col",
            Mutations.SetCell(CF, "target", "match", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "other", "match", new BigtableVersion(1000)));

        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("target"),
            RowFilters.ValueRange(ValueRange.Closed("a", "z")));
        var vals = await ReadValues("vrfc-chain-col", filter);
        vals.Should().HaveCount(1);
    }

    [Fact]
    public async Task ValueRange_in_interleave_union()
    {
        await Client.MutateRowAsync(TN, "vrfc-ilv",
            Mutations.SetCell(CF, "a", "abc", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "mno", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "xyz", new BigtableVersion(1000)));

        var filter = RowFilters.Interleave(
            RowFilters.ValueRange(ValueRange.Closed("aaa", "bbb")),
            RowFilters.ValueRange(ValueRange.Closed("www", "zzz")));
        var vals = await ReadValues("vrfc-ilv", filter);
        vals.Should().Contain("abc");
        vals.Should().Contain("xyz");
        vals.Should().NotContain("mno");
    }

    [Fact]
    public async Task ValueRange_with_cells_per_column_limit()
    {
        await Client.MutateRowAsync(TN, "vrfc-cpcl",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "vrfc-cpcl",
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)));

        var filter = RowFilters.Chain(
            RowFilters.CellsPerColumnLimit(1),
            RowFilters.ValueRange(ValueRange.Closed("a", "z")));
        var vals = await ReadValues("vrfc-cpcl", filter);
        vals.Should().HaveCount(1);
        vals[0].Should().Be("new");
    }

    [Fact]
    public async Task ValueRange_with_strip_value_filter()
    {
        await Client.MutateRowAsync(TN, "vrfc-strip",
            Mutations.SetCell(CF, "c", "match", new BigtableVersion(1000)));

        var filter = RowFilters.Chain(
            RowFilters.ValueRange(ValueRange.Closed("a", "z")),
            RowFilters.StripValueTransformer());
        var vals = await ReadValues("vrfc-strip", filter);
        vals.Should().HaveCount(1);
        vals[0].Should().BeEmpty();
    }

    [Fact]
    public async Task ValueRange_with_label_transformer()
    {
        await Client.MutateRowAsync(TN, "vrfc-label",
            Mutations.SetCell(CF, "c", "match", new BigtableVersion(1000)));

        var filter = RowFilters.Chain(
            RowFilters.ValueRange(ValueRange.Closed("a", "z")),
            new RowFilter { ApplyLabelTransformer = "matched" });

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = filter,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("vrfc-label") } }
        };
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                cell.Labels.Should().Contain("matched");
    }

    [Fact]
    public async Task ValueRange_with_timestamp_range()
    {
        var ts1 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var ts2 = new DateTime(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        await Client.MutateRowAsync(TN, "vrfc-ts",
            Mutations.SetCell(CF, "c", "early", new BigtableVersion(ts1)));
        await Client.MutateRowAsync(TN, "vrfc-ts",
            Mutations.SetCell(CF, "d", "late", new BigtableVersion(ts2)));

        var filter = RowFilters.Chain(
            RowFilters.TimestampRange(null, ts2),
            RowFilters.ValueRange(ValueRange.Closed("a", "z")));
        var vals = await ReadValues("vrfc-ts", filter);
        vals.Should().Contain("early");
        vals.Should().NotContain("late");
    }

    [Fact]
    public async Task ValueRange_no_matching_values()
    {
        await Client.MutateRowAsync(TN, "vrfc-nomatch",
            Mutations.SetCell(CF, "c", "abc", new BigtableVersion(1000)));

        var vals = await ReadValues("vrfc-nomatch",
            RowFilters.ValueRange(ValueRange.Closed("xyz", "zzz")));
        vals.Should().BeEmpty();
    }

    [Fact]
    public async Task ValueRange_single_value_match()
    {
        await Client.MutateRowAsync(TN, "vrfc-single",
            Mutations.SetCell(CF, "c", "exact", new BigtableVersion(1000)));

        var vals = await ReadValues("vrfc-single",
            RowFilters.ValueRange(ValueRange.Closed("exact", "exact")));
        vals.Should().HaveCount(1);
        vals[0].Should().Be("exact");
    }

    [Fact]
    public async Task ValueRange_numeric_string_ordering()
    {
        await Client.MutateRowAsync(TN, "vrfc-numord",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "10", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "d", "9", new BigtableVersion(1000)));

        // Lexicographic: "1" < "10" < "2" < "9"
        var vals = await ReadValues("vrfc-numord",
            RowFilters.ValueRange(ValueRange.Closed("1", "2")));
        vals.Should().Contain("1");
        vals.Should().Contain("10");
        vals.Should().Contain("2");
        vals.Should().NotContain("9");
    }

    [Fact]
    public async Task ValueRange_in_condition_filter_predicate()
    {
        await Client.MutateRowAsync(TN, "vrfc-cond",
            Mutations.SetCell(CF, "c", "match", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "d", "other", new BigtableVersion(1000)));

        var filter = RowFilters.Condition(
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("c"),
                RowFilters.ValueRange(ValueRange.Closed("a", "z"))),
            RowFilters.PassAllFilter(),
            RowFilters.BlockAllFilter());

        var vals = await ReadValues("vrfc-cond", filter);
        vals.Should().HaveCount(2); // predicate matched, so true branch (pass all) applies
    }

    [Fact]
    public async Task ValueRange_with_multiple_versions()
    {
        await Client.MutateRowAsync(TN, "vrfc-multiver",
            Mutations.SetCell(CF, "c", "alpha", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "vrfc-multiver",
            Mutations.SetCell(CF, "c", "zulu", new BigtableVersion(2000)));

        var vals = await ReadValues("vrfc-multiver",
            RowFilters.ValueRange(ValueRange.Closed("a", "m")));
        vals.Should().HaveCount(1);
        vals[0].Should().Be("alpha");
    }

    [Fact]
    public async Task ValueRange_across_families()
    {
        await Client.MutateRowAsync(TN, "vrfc-xfam",
            Mutations.SetCell(CF, "c", "match", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "match", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "d", "no", new BigtableVersion(1000)));

        var vals = await ReadValues("vrfc-xfam",
            RowFilters.ValueRange(ValueRange.Closed("match", "match")));
        vals.Should().HaveCount(2);
    }

    [Fact]
    public async Task ValueRange_empty_string_boundary()
    {
        await Client.MutateRowAsync(TN, "vrfc-empty",
            Mutations.SetCell(CF, "c", "", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "d", "a", new BigtableVersion(1000)));

        var vals = await ReadValues("vrfc-empty",
            RowFilters.ValueRange(ValueRange.Closed("", "a")));
        vals.Should().Contain("");
        vals.Should().Contain("a");
    }

    [Fact]
    public async Task ValueRange_with_cells_per_row_limit()
    {
        await Client.MutateRowAsync(TN, "vrfc-cprl",
            Mutations.SetCell(CF, "a", "aaa", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "bbb", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "ccc", new BigtableVersion(1000)));

        var filter = RowFilters.Chain(
            RowFilters.ValueRange(ValueRange.Closed("a", "z")),
            RowFilters.CellsPerRowLimit(2));
        var vals = await ReadValues("vrfc-cprl", filter);
        vals.Should().HaveCount(2);
    }

    [Fact]
    public async Task ValueRange_with_row_key_regex()
    {
        await Client.MutateRowAsync(TN, "vrfc-rk-a",
            Mutations.SetCell(CF, "c", "match", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "vrfc-rk-b",
            Mutations.SetCell(CF, "c", "match", new BigtableVersion(1000)));

        var filter = RowFilters.Chain(
            RowFilters.RowKeyRegex("vrfc-rk-a"),
            RowFilters.ValueRange(ValueRange.Closed("a", "z")));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = filter,
            Rows = new RowSet
            {
                RowKeys =
                {
                    ByteString.CopyFromUtf8("vrfc-rk-a"),
                    ByteString.CopyFromUtf8("vrfc-rk-b")
                }
            }
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());
        keys.Should().HaveCount(1);
        keys[0].Should().Be("vrfc-rk-a");
    }

    [Fact]
    public async Task ValueRange_chained_narrowing()
    {
        await Client.MutateRowAsync(TN, "vrfc-narrow",
            Mutations.SetCell(CF, "a", "abc", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "def", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "ghi", new BigtableVersion(1000)));

        // First range: a-g, Second range: d-z → intersection is d-g
        var filter = RowFilters.Chain(
            RowFilters.ValueRange(ValueRange.Closed("a", "g")),
            RowFilters.ValueRange(ValueRange.Closed("d", "z")));
        var vals = await ReadValues("vrfc-narrow", filter);
        vals.Should().HaveCount(1);
        vals[0].Should().Be("def");
    }

    [Fact]
    public async Task ValueRange_interleave_with_family_filter()
    {
        await Client.MutateRowAsync(TN, "vrfc-ilv-fam",
            Mutations.SetCell(CF, "c", "hello", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "world", new BigtableVersion(1000)));

        var filter = RowFilters.Interleave(
            RowFilters.Chain(
                RowFilters.FamilyNameExact(CF),
                RowFilters.ValueRange(ValueRange.Closed("a", "m"))),
            RowFilters.Chain(
                RowFilters.FamilyNameExact("cf2"),
                RowFilters.ValueRange(ValueRange.Closed("u", "z"))));
        var vals = await ReadValues("vrfc-ilv-fam", filter);
        vals.Should().Contain("hello");
        vals.Should().Contain("world");
    }

    [Fact]
    public async Task ValueRange_with_block_all_in_condition()
    {
        await Client.MutateRowAsync(TN, "vrfc-block",
            Mutations.SetCell(CF, "c", "zzz", new BigtableVersion(1000)));

        var filter = RowFilters.Condition(
            RowFilters.ValueRange(ValueRange.Closed("aaa", "bbb")),
            RowFilters.PassAllFilter(),
            RowFilters.BlockAllFilter());

        // predicate doesn't match "zzz" → false branch (block all)
        var vals = await ReadValues("vrfc-block", filter);
        vals.Should().BeEmpty();
    }
}

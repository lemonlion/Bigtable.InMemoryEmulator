using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for value filter patterns: exact, regex, range.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ValueFilterPatternTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "val-filt";
    private const string CF = "cf";

    public ValueFilterPatternTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        var c = Client;
        // Seed rows with varied values
        var values = new Dictionary<string, string>
        {
            ["vf-empty"] = "",
            ["vf-hello"] = "hello",
            ["vf-world"] = "world",
            ["vf-hello-world"] = "hello world",
            ["vf-num-0"] = "0",
            ["vf-num-42"] = "42",
            ["vf-num-100"] = "100",
            ["vf-num-999"] = "999",
            ["vf-upper"] = "HELLO",
            ["vf-mixed"] = "HeLLo",
            ["vf-special"] = "hello!@#$%",
            ["vf-newline"] = "line1\nline2",
            ["vf-tab"] = "col1\tcol2",
            ["vf-unicode"] = "héllo wörld",
            ["vf-json"] = "{\"key\":\"value\"}",
            ["vf-long"] = new string('x', 1000),
            ["vf-spaces"] = "  spaced  ",
            ["vf-prefix-a"] = "alpha-001",
            ["vf-prefix-b"] = "alpha-002",
            ["vf-prefix-c"] = "beta-001",
        };
        foreach (var (key, val) in values)
            await c.MutateRowAsync(TN, key,
                Mutations.SetCell(CF, "c", val, new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null)
    {
        var list = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: rows, filter: filter))
            list.Add(row);
        return list;
    }

    #region ValueExact

    [Fact]
    public async Task ValueExact_match()
    {
        var rows = await ReadAll(filter: RowFilters.ValueExact("hello"));
        rows.Should().ContainSingle().Which.Key.ToStringUtf8().Should().Be("vf-hello");
    }

    [Fact]
    public async Task ValueExact_no_match()
    {
        var rows = await ReadAll(filter: RowFilters.ValueExact("nonexistent"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ValueExact_empty_string()
    {
        var rows = await ReadAll(filter: RowFilters.ValueExact(""));
        rows.Should().ContainSingle().Which.Key.ToStringUtf8().Should().Be("vf-empty");
    }

    [Fact]
    public async Task ValueExact_case_sensitive()
    {
        var rows = await ReadAll(filter: RowFilters.ValueExact("HELLO"));
        rows.Should().ContainSingle().Which.Key.ToStringUtf8().Should().Be("vf-upper");
    }

    [Fact]
    public async Task ValueExact_with_spaces()
    {
        var rows = await ReadAll(filter: RowFilters.ValueExact("  spaced  "));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task ValueExact_numeric_string()
    {
        var rows = await ReadAll(filter: RowFilters.ValueExact("42"));
        rows.Should().ContainSingle().Which.Key.ToStringUtf8().Should().Be("vf-num-42");
    }

    #endregion

    #region ValueRegex

    [Fact]
    public async Task ValueRegex_simple()
    {
        // Bigtable value regex is full-match (implicitly anchored), so "hello" only matches exactly "hello"
        var rows = await ReadAll(filter: RowFilters.ValueRegex("hello"));
        rows.Should().ContainSingle().Which.Key.ToStringUtf8().Should().Be("vf-hello");
    }

    [Fact]
    public async Task ValueRegex_anchored()
    {
        var rows = await ReadAll(filter: RowFilters.ValueRegex("^hello$"));
        rows.Should().ContainSingle().Which.Key.ToStringUtf8().Should().Be("vf-hello");
    }

    [Fact]
    public async Task ValueRegex_case_insensitive_not_supported()
    {
        // RE2 is case-sensitive by default
        var rows = await ReadAll(filter: RowFilters.ValueRegex("^hello$"));
        rows.Should().ContainSingle(); // Only lowercase "hello"
    }

    [Fact]
    public async Task ValueRegex_digit_pattern()
    {
        var rows = await ReadAll(filter: RowFilters.ValueRegex("^[0-9]+$"));
        rows.Should().HaveCount(4); // 0, 42, 100, 999
    }

    [Fact]
    public async Task ValueRegex_prefix()
    {
        var rows = await ReadAll(filter: RowFilters.ValueRegex("^alpha-.*"));
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task ValueRegex_alternation()
    {
        var rows = await ReadAll(filter: RowFilters.ValueRegex("^hello$|^world$"));
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task ValueRegex_dot_star()
    {
        // RE2 '.' does not match '\n' by default, so the newline row is excluded
        var rows = await ReadAll(filter: RowFilters.ValueRegex(".*"));
        rows.Should().HaveCount(19);
    }

    [Fact]
    public async Task ValueRegex_no_match()
    {
        var rows = await ReadAll(filter: RowFilters.ValueRegex("^zzz.*"));
        rows.Should().BeEmpty();
    }

    #endregion

    #region ValueRange

    [Fact]
    public async Task ValueRange_closed()
    {
        var rows = await ReadAll(filter: RowFilters.ValueRange(ValueRange.Closed("hello", "hello")));
        rows.Should().ContainSingle().Which.Key.ToStringUtf8().Should().Be("vf-hello");
    }

    [Fact]
    public async Task ValueRange_closed_multi()
    {
        var rows = await ReadAll(filter: RowFilters.ValueRange(ValueRange.Closed("hello", "world")));
        // All values between "hello" (inclusive) and "world" (inclusive)
        rows.Count.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task ValueRange_open()
    {
        var rows = await ReadAll(filter: RowFilters.ValueRange(ValueRange.Open("hello", "world")));
        // Between "hello" (exclusive) and "world" (exclusive)
        rows.All(r => r.Families[0].Columns[0].Cells[0].Value.ToStringUtf8() != "hello").Should().BeTrue();
        rows.All(r => r.Families[0].Columns[0].Cells[0].Value.ToStringUtf8() != "world").Should().BeTrue();
    }

    [Fact]
    public async Task ValueRange_no_match()
    {
        var rows = await ReadAll(filter: RowFilters.ValueRange(ValueRange.Closed("zzz", "zzzz")));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ValueRange_numeric_string_order()
    {
        // String comparison: "0" < "100" < "42" < "999" (lexicographic, not numeric)
        var rows = await ReadAll(filter: RowFilters.Chain(
            RowFilters.ValueRange(ValueRange.Closed("0", "999")),
            RowFilters.RowKeyRegex("vf-num-.*")));
        rows.Should().HaveCount(4);
    }

    #endregion

    #region Value filters with specific rows

    [Fact]
    public async Task ValueExact_on_specific_row()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("vf-hello"), RowFilters.ValueExact("hello"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task ValueExact_on_wrong_row()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("vf-world"), RowFilters.ValueExact("hello"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ValueRegex_on_range()
    {
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("vf-prefix-", "vf-prefix~")),
            RowFilters.ValueRegex("alpha-.*"));
        rows.Should().HaveCount(2); // prefix-a and prefix-b
    }

    #endregion

    #region Value filters combined with other filters

    [Fact]
    public async Task ValueExact_with_column_filter()
    {
        // Add a second column to a row
        await Client.MutateRowAsync(TN, "vf-multi",
            Mutations.SetCell(CF, "a", "target", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "other", new BigtableVersion(1000)));
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("a"),
            RowFilters.ValueExact("target"));
        var rows = await ReadAll(RowSet.FromRowKeys("vf-multi"), filter);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Chain_value_filter_with_limit()
    {
        var filter = RowFilters.Chain(
            RowFilters.ValueRegex("^alpha-.*"),
            RowFilters.CellsPerColumnLimit(1));
        var rows = await ReadAll(filter: filter);
        rows.Should().HaveCount(2);
    }

    #endregion

    #region Binary values

    [Fact]
    public async Task ValueExact_binary()
    {
        var bytes = new byte[] { 0x01, 0x02, 0x03 };
        await Client.MutateRowAsync(TN, "vf-bin",
            Mutations.SetCell(CF, "c", ByteString.CopyFrom(bytes), new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("vf-bin"),
            RowFilters.ValueExact(ByteString.CopyFrom(bytes)));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task ValueExact_binary_no_match()
    {
        var bytes = new byte[] { 0x01, 0x02, 0x03 };
        await Client.MutateRowAsync(TN, "vf-bin2",
            Mutations.SetCell(CF, "c", ByteString.CopyFrom(bytes), new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("vf-bin2"),
            RowFilters.ValueExact(ByteString.CopyFrom(new byte[] { 0x01, 0x02, 0x04 })));
        rows.Should().BeEmpty();
    }

    #endregion
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for row key regex filter patterns.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class RowKeyRegexFilterTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "rkr-filt";
    private const string CF = "cf";

    public RowKeyRegexFilterTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        var keys = new[]
        {
            "user#001", "user#002", "user#010", "user#100",
            "order#001", "order#002", "order#100",
            "product#A", "product#B", "product#C",
            "log#2024-01-01", "log#2024-01-02", "log#2024-12-31",
            "a", "ab", "abc", "abcd",
            "UPPER", "lower", "MiXeD",
            "special!@#", "with space", "with\ttab",
            "123", "456", "789",
            "alpha", "beta", "gamma", "delta",
        };
        foreach (var key in keys)
            await Client.MutateRowAsync(TN, key,
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
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

    #region Exact match

    [Fact]
    public async Task RowKeyRegex_exact_match()
    {
        // Bigtable row key regex is full-match (anchored)
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("user#001"));
        rows.Should().ContainSingle().Which.Key.ToStringUtf8().Should().Be("user#001");
    }

    [Fact]
    public async Task RowKeyRegex_no_match()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("nonexistent"));
        rows.Should().BeEmpty();
    }

    #endregion

    #region Prefix matching

    [Fact]
    public async Task RowKeyRegex_prefix_user()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("user#.*"));
        rows.Should().HaveCount(4);
        rows.Should().AllSatisfy(r => r.Key.ToStringUtf8().Should().StartWith("user#"));
    }

    [Fact]
    public async Task RowKeyRegex_prefix_order()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("order#.*"));
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task RowKeyRegex_prefix_product()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("product#.*"));
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task RowKeyRegex_prefix_log()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("log#.*"));
        rows.Should().HaveCount(3);
    }

    #endregion

    #region Character classes

    [Fact]
    public async Task RowKeyRegex_digits_only()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("[0-9]+"));
        rows.Should().HaveCount(3); // "123", "456", "789"
    }

    [Fact]
    public async Task RowKeyRegex_lowercase_alpha()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("[a-z]+"));
        // Matches: "a", "ab", "abc", "abcd", "lower", "alpha", "beta", "gamma", "delta"
        rows.Should().HaveCount(9);
    }

    [Fact]
    public async Task RowKeyRegex_uppercase_alpha()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("[A-Z]+"));
        rows.Should().ContainSingle().Which.Key.ToStringUtf8().Should().Be("UPPER");
    }

    #endregion

    #region Alternation

    [Fact]
    public async Task RowKeyRegex_alternation()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("alpha|beta|gamma"));
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task RowKeyRegex_alternation_prefixes()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("user#.*|order#.*"));
        rows.Should().HaveCount(7); // 4 users + 3 orders
    }

    #endregion

    #region Quantifiers

    [Fact]
    public async Task RowKeyRegex_question_mark()
    {
        // "a" or "ab" — single character 'a' followed by optional 'b'
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("ab?"));
        rows.Should().HaveCount(2); // "a" and "ab"
    }

    [Fact]
    public async Task RowKeyRegex_plus()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("a[a-z]+"));
        // "ab", "abc", "abcd", "alpha"
        rows.Should().HaveCount(4);
    }

    [Fact]
    public async Task RowKeyRegex_exact_length()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("[a-z]{4}"));
        // "abcd", "beta"
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task RowKeyRegex_length_range()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("[a-z]{3,5}"));
        // "abc", "abcd", "alpha", "beta", "gamma", "delta"
        rows.Should().HaveCount(7); // "abc"(3), "abcd"(4), "alpha"(5), "beta"(4), "gamma"(5), "delta"(5), "lower"(5)
    }

    #endregion

    #region Special characters

    [Fact]
    public async Task RowKeyRegex_hash_literal()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex(".*#.*"));
        // Matches: user#4, order#3, product#3, log#3, special!@# = 14
        rows.Should().HaveCount(14);
    }

    [Fact]
    public async Task RowKeyRegex_dot_literal()
    {
        // '.' matches any character including '#', spaces, etc.
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("a."));
        // "ab" exactly (2 chars, starts with 'a')
        rows.Should().ContainSingle().Which.Key.ToStringUtf8().Should().Be("ab");
    }

    [Fact]
    public async Task RowKeyRegex_caret_dollar_anchoring()
    {
        // Since regex is full-match, ^/$ are implicit but can be explicit
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("^a$"));
        rows.Should().ContainSingle().Which.Key.ToStringUtf8().Should().Be("a");
    }

    #endregion

    #region Combined with RowSet

    [Fact]
    public async Task RowKeyRegex_with_specific_keys()
    {
        var rowSet = RowSet.FromRowKeys("user#001", "order#001", "product#A", "alpha");
        var rows = await ReadAll(rows: rowSet, filter: RowFilters.RowKeyRegex("user#.*|order#.*"));
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task RowKeyRegex_with_row_range()
    {
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("a", "b"));
        var rows = await ReadAll(rows: rowSet, filter: RowFilters.RowKeyRegex("[a-z]+"));
        // In range [a, b): "a", "ab", "abc", "abcd", "alpha" — all are lowercase alpha
        rows.Should().HaveCount(5);
    }

    #endregion

    #region Edge cases

    [Fact]
    public async Task RowKeyRegex_dot_star_matches_all_except_newline()
    {
        var all = await ReadAll();
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex(".*"));
        // All keys in our seed data don't contain newlines, so all should match
        rows.Should().HaveCount(all.Count);
    }

    [Fact]
    public async Task RowKeyRegex_empty_pattern_matches_empty_key_only()
    {
        // Empty regex "" matches only empty string row key (which we don't have)
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex(""));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task RowKeyRegex_date_pattern()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("log#2024-01-.*"));
        rows.Should().HaveCount(2); // "log#2024-01-01", "log#2024-01-02"
    }

    [Fact]
    public async Task RowKeyRegex_with_space_key()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("with space"));
        rows.Should().ContainSingle();
    }

    #endregion
}

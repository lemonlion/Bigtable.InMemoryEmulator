using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for RE2 regex edge cases in RowKey, ColumnQualifier, and Value filters.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
///   "Uses RE2 syntax; full-match (implicitly anchored with ^ and $)"
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class RegexEdgeCaseTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public RegexEdgeCaseTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync("regex-edge", new[] { CF });
        // Seed diverse rows
        var data = new Dictionary<string, string>
        {
            ["alpha"] = "hello",
            ["alpha-beta"] = "world",
            ["alpha_gamma"] = "test",
            ["ALPHA"] = "UPPER",
            ["123"] = "numeric",
            ["abc123"] = "mixed",
            ["a.b.c"] = "dots",
            ["a+b"] = "plus",
            ["a*b"] = "star",
            ["a(b)c"] = "parens",
            ["a[0]"] = "brackets",
            ["a{1}b"] = "braces",
            ["a|b"] = "pipe",
            ["a^b"] = "caret",
            ["a$b"] = "dollar",
            ["tab\there"] = "tab",
            ["space here"] = "space",
            ["emoji\xF0\x9F\x98\x80"] = "emoji-utf8",
            ["row-00"] = "padded",
            ["row-01"] = "padded2",
        };
        foreach (var (key, val) in data)
            await Client.MutateRowAsync(TN, key,
                Mutations.SetCell(CF, "c", val, new BigtableVersion(1000)));
    }
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("regex-edge");

    private async Task<List<string>> ReadKeys(RowFilter filter)
    {
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter))
            keys.Add(row.Key.ToStringUtf8());
        return keys;
    }

    #region Literal character matching

    [Fact]
    public async Task Regex_exact_match()
    {
        var keys = await ReadKeys(RowFilters.RowKeyRegex("alpha"));
        keys.Should().ContainSingle().Which.Should().Be("alpha");
    }

    [Fact]
    public async Task Regex_dot_matches_single_char()
    {
        // "a.b" matches "a+b", "a*b", "a|b", "a^b", "a$b"
        var keys = await ReadKeys(RowFilters.RowKeyRegex("a.b"));
        keys.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Regex_escaped_dot_matches_literal()
    {
        var keys = await ReadKeys(RowFilters.RowKeyRegex(@"a\.b\.c"));
        keys.Should().ContainSingle().Which.Should().Be("a.b.c");
    }

    [Fact]
    public async Task Regex_escaped_plus_matches_literal()
    {
        var keys = await ReadKeys(RowFilters.RowKeyRegex(@"a\+b"));
        keys.Should().ContainSingle().Which.Should().Be("a+b");
    }

    [Fact]
    public async Task Regex_escaped_star_matches_literal()
    {
        var keys = await ReadKeys(RowFilters.RowKeyRegex(@"a\*b"));
        keys.Should().ContainSingle().Which.Should().Be("a*b");
    }

    [Fact]
    public async Task Regex_escaped_parens_matches_literal()
    {
        var keys = await ReadKeys(RowFilters.RowKeyRegex(@"a\(b\)c"));
        keys.Should().ContainSingle().Which.Should().Be("a(b)c");
    }

    [Fact]
    public async Task Regex_escaped_brackets_matches_literal()
    {
        var keys = await ReadKeys(RowFilters.RowKeyRegex(@"a\[0\]"));
        keys.Should().ContainSingle().Which.Should().Be("a[0]");
    }

    [Fact]
    public async Task Regex_escaped_pipe_matches_literal()
    {
        var keys = await ReadKeys(RowFilters.RowKeyRegex(@"a\|b"));
        keys.Should().ContainSingle().Which.Should().Be("a|b");
    }

    #endregion

    #region Character classes

    [Fact]
    public async Task Digit_class_matches_numeric_row()
    {
        var keys = await ReadKeys(RowFilters.RowKeyRegex("[0-9]+"));
        keys.Should().ContainSingle().Which.Should().Be("123");
    }

    [Fact]
    public async Task Mixed_alphanumeric_class()
    {
        var keys = await ReadKeys(RowFilters.RowKeyRegex("[a-z]+[0-9]+"));
        keys.Should().Contain("abc123");
        keys.Should().NotContain("alpha"); // no digits
    }

    [Fact]
    public async Task Negated_class()
    {
        // Only alpha chars, no digits/special
        var keys = await ReadKeys(RowFilters.RowKeyRegex("[a-zA-Z]+"));
        keys.Should().Contain("alpha");
        keys.Should().Contain("ALPHA");
        keys.Should().NotContain("123");
    }

    #endregion

    #region Quantifiers

    [Fact]
    public async Task Plus_quantifier_one_or_more()
    {
        var keys = await ReadKeys(RowFilters.RowKeyRegex("alpha.+"));
        // "alpha-beta" and "alpha_gamma" match (alpha + at least 1 more char)
        keys.Should().Contain("alpha-beta");
        keys.Should().Contain("alpha_gamma");
        keys.Should().NotContain("alpha"); // no chars after "alpha"
    }

    [Fact]
    public async Task Star_quantifier_zero_or_more()
    {
        var keys = await ReadKeys(RowFilters.RowKeyRegex("alpha.*"));
        // "alpha", "alpha-beta", "alpha_gamma" all match
        keys.Should().Contain("alpha");
        keys.Should().Contain("alpha-beta");
        keys.Should().Contain("alpha_gamma");
    }

    [Fact]
    public async Task Question_mark_optional()
    {
        var keys = await ReadKeys(RowFilters.RowKeyRegex("row-0[01]?"));
        // Matches "row-0", "row-00", "row-01"
        keys.Should().Contain("row-00");
        keys.Should().Contain("row-01");
    }

    [Fact]
    public async Task Curly_brace_exact_count()
    {
        var keys = await ReadKeys(RowFilters.RowKeyRegex("[0-9]{3}"));
        keys.Should().ContainSingle().Which.Should().Be("123");
    }

    #endregion

    #region Alternation

    [Fact]
    public async Task Alternation_matches_either()
    {
        var keys = await ReadKeys(RowFilters.RowKeyRegex("alpha|ALPHA"));
        keys.Should().HaveCount(2);
        keys.Should().Contain("alpha");
        keys.Should().Contain("ALPHA");
    }

    [Fact]
    public async Task Alternation_three_options()
    {
        var keys = await ReadKeys(RowFilters.RowKeyRegex("alpha|123|row-00"));
        keys.Should().HaveCount(3);
    }

    #endregion

    #region Value regex

    [Fact]
    public async Task Value_regex_exact_match()
    {
        var filter = RowFilters.ValueRegex("hello");
        var keys = await ReadKeys(filter);
        keys.Should().ContainSingle().Which.Should().Be("alpha");
    }

    [Fact]
    public async Task Value_regex_alternation()
    {
        var filter = RowFilters.ValueRegex("hello|world|test");
        var keys = await ReadKeys(filter);
        keys.Should().HaveCount(3);
    }

    [Fact]
    public async Task Value_regex_case_sensitive()
    {
        var filter = RowFilters.ValueRegex("upper");
        var keys = await ReadKeys(filter);
        keys.Should().BeEmpty(); // Value is "UPPER" not "upper"
    }

    [Fact]
    public async Task Value_regex_wildcard()
    {
        var filter = RowFilters.ValueRegex("pad.*");
        var keys = await ReadKeys(filter);
        keys.Should().HaveCount(2); // row-00 and row-01 have "padded" and "padded2"
    }

    #endregion

    #region ColumnQualifier regex

    [Fact]
    public async Task Column_regex_exact()
    {
        var filter = RowFilters.ColumnQualifierRegex("c");
        var count = 0;
        await foreach (var _ in Client.ReadRows(TN, rows: null, filter))
            count++;
        count.Should().BeGreaterThanOrEqualTo(1); // All rows have column "c"
    }

    #endregion
}

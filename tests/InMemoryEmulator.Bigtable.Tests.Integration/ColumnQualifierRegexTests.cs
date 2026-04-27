using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for column qualifier regex filter patterns.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ColumnQualifierRegexTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "cqr-filt";
    private const string CF = "cf";

    public ColumnQualifierRegexTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        // Seed one row with many columns
        var mutations = new List<Mutation>();
        var columns = new[]
        {
            "name", "email", "phone", "address",
            "score_1", "score_2", "score_3",
            "tag_a", "tag_b", "tag_c",
            "meta_created", "meta_updated", "meta_version",
            "x", "xy", "xyz", "xyzw",
            "UPPER", "lower", "MiXeD",
            "col with space", "col.with.dots", "col-with-dashes",
            "123", "456num", "num789",
        };
        foreach (var col in columns)
            mutations.Add(Mutations.SetCell(CF, col, $"val-{col}", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "r1", mutations.ToArray());
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<Row>> ReadAll(RowFilter? filter = null)
    {
        var list = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, filter: filter))
            list.Add(row);
        return list;
    }

    private int GetCellCount(List<Row> rows)
    {
        return rows.SelectMany(r => r.Families)
            .SelectMany(f => f.Columns)
            .SelectMany(c => c.Cells)
            .Count();
    }

    private List<string> GetQualifiers(List<Row> rows)
    {
        return rows.SelectMany(r => r.Families)
            .SelectMany(f => f.Columns)
            .Select(c => c.Qualifier.ToStringUtf8())
            .ToList();
    }

    #region Exact match

    [Fact]
    public async Task ColumnQualifierRegex_exact_match()
    {
        var rows = await ReadAll(filter: RowFilters.ColumnQualifierRegex("name"));
        var quals = GetQualifiers(rows);
        quals.Should().ContainSingle().Which.Should().Be("name");
    }

    [Fact]
    public async Task ColumnQualifierRegex_no_match()
    {
        var rows = await ReadAll(filter: RowFilters.ColumnQualifierRegex("nonexistent"));
        rows.Should().BeEmpty();
    }

    #endregion

    #region Prefix

    [Fact]
    public async Task ColumnQualifierRegex_prefix_score()
    {
        var rows = await ReadAll(filter: RowFilters.ColumnQualifierRegex("score_.*"));
        var quals = GetQualifiers(rows);
        quals.Should().HaveCount(3);
        quals.Should().AllSatisfy(q => q.Should().StartWith("score_"));
    }

    [Fact]
    public async Task ColumnQualifierRegex_prefix_tag()
    {
        var rows = await ReadAll(filter: RowFilters.ColumnQualifierRegex("tag_.*"));
        var quals = GetQualifiers(rows);
        quals.Should().HaveCount(3);
    }

    [Fact]
    public async Task ColumnQualifierRegex_prefix_meta()
    {
        var rows = await ReadAll(filter: RowFilters.ColumnQualifierRegex("meta_.*"));
        var quals = GetQualifiers(rows);
        quals.Should().HaveCount(3);
    }

    #endregion

    #region Character classes

    [Fact]
    public async Task ColumnQualifierRegex_digits_only()
    {
        var rows = await ReadAll(filter: RowFilters.ColumnQualifierRegex("[0-9]+"));
        var quals = GetQualifiers(rows);
        quals.Should().ContainSingle().Which.Should().Be("123");
    }

    [Fact]
    public async Task ColumnQualifierRegex_lowercase()
    {
        var rows = await ReadAll(filter: RowFilters.ColumnQualifierRegex("[a-z]+"));
        var quals = GetQualifiers(rows);
        quals.Should().Contain("name");
        quals.Should().Contain("email");
        quals.Should().Contain("lower");
        quals.Should().NotContain("UPPER");
    }

    #endregion

    #region Alternation

    [Fact]
    public async Task ColumnQualifierRegex_alternation()
    {
        var rows = await ReadAll(filter: RowFilters.ColumnQualifierRegex("name|email|phone"));
        var quals = GetQualifiers(rows);
        quals.Should().HaveCount(3);
    }

    [Fact]
    public async Task ColumnQualifierRegex_alternation_prefix()
    {
        var rows = await ReadAll(filter: RowFilters.ColumnQualifierRegex("score_.*|tag_.*"));
        var quals = GetQualifiers(rows);
        quals.Should().HaveCount(6); // 3 scores + 3 tags
    }

    #endregion

    #region Quantifiers

    [Fact]
    public async Task ColumnQualifierRegex_single_char()
    {
        var rows = await ReadAll(filter: RowFilters.ColumnQualifierRegex("[a-z]"));
        var quals = GetQualifiers(rows);
        quals.Should().ContainSingle().Which.Should().Be("x");
    }

    [Fact]
    public async Task ColumnQualifierRegex_two_chars()
    {
        var rows = await ReadAll(filter: RowFilters.ColumnQualifierRegex("[a-z]{2}"));
        var quals = GetQualifiers(rows);
        quals.Should().ContainSingle().Which.Should().Be("xy");
    }

    [Fact]
    public async Task ColumnQualifierRegex_three_to_five()
    {
        var rows = await ReadAll(filter: RowFilters.ColumnQualifierRegex("[a-z]{3,5}"));
        var quals = GetQualifiers(rows);
        quals.Should().Contain("xyz");
        quals.Should().Contain("xyzw");
        quals.Should().Contain("name");
        quals.Should().Contain("email");
        quals.Should().Contain("phone");
        quals.Should().Contain("lower");
    }

    #endregion

    #region Combined with value filter

    [Fact]
    public async Task ColumnQualifierRegex_chain_with_value_filter()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierRegex("score_.*"),
            RowFilters.ValueRegex("val-score_1"));
        var rows = await ReadAll(filter: filter);
        var quals = GetQualifiers(rows);
        quals.Should().ContainSingle().Which.Should().Be("score_1");
    }

    [Fact]
    public async Task ColumnQualifierRegex_interleave_with_other()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierRegex("name"),
            RowFilters.ColumnQualifierRegex("email"));
        var rows = await ReadAll(filter: filter);
        var quals = GetQualifiers(rows);
        quals.Should().HaveCount(2);
        quals.Should().Contain("name");
        quals.Should().Contain("email");
    }

    #endregion

    #region Edge cases

    [Fact]
    public async Task ColumnQualifierRegex_with_dots_literal()
    {
        // In RE2, unescaped dot matches any char. Use \. for literal dot.
        var rows = await ReadAll(filter: RowFilters.ColumnQualifierRegex(@"col\.with\.dots"));
        var quals = GetQualifiers(rows);
        quals.Should().ContainSingle().Which.Should().Be("col.with.dots");
    }

    [Fact]
    public async Task ColumnQualifierRegex_unescaped_dot_matches_more()
    {
        // "col.with.dots" without escaping matches more broadly
        var rows = await ReadAll(filter: RowFilters.ColumnQualifierRegex("col.with.dots"));
        var quals = GetQualifiers(rows);
        quals.Should().Contain("col.with.dots");
        // May also match "col-with-dots" style if present, but dot also matches space and dash
    }

    [Fact]
    public async Task ColumnQualifierRegex_with_space()
    {
        var rows = await ReadAll(filter: RowFilters.ColumnQualifierRegex("col with space"));
        var quals = GetQualifiers(rows);
        quals.Should().ContainSingle().Which.Should().Be("col with space");
    }

    [Fact]
    public async Task ColumnQualifierRegex_dot_star_all()
    {
        var rows = await ReadAll(filter: RowFilters.ColumnQualifierRegex(".*"));
        var quals = GetQualifiers(rows);
        quals.Should().HaveCount(26); // All columns seeded
    }

    #endregion
}

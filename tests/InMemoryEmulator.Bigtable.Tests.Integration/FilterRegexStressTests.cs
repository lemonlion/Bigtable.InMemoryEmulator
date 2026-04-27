using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Stress tests for regex filters — complex patterns, edge cases, anchoring behavior.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// "Uses RE2 syntax. The entire value must match."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class FilterRegexStressTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "regex-stress";
    private const string CF = "cf";
    private const string CF2 = "cf2";
    private const string CF3 = "cf3";

    public FilterRegexStressTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, CF2, CF3 });
        var c = Client;
        // Seed diverse data
        // row keys: alpha-001..alpha-005, beta-001..beta-005, gamma-001..gamma-003
        foreach (var prefix in new[] { "alpha", "beta", "gamma" })
        {
            int count = prefix == "gamma" ? 3 : 5;
            for (int i = 1; i <= count; i++)
            {
                var key = $"{prefix}-{i:D3}";
                await c.MutateRowAsync(TN, key,
                    Mutations.SetCell(CF, "name", $"Name-{prefix}-{i}", new BigtableVersion(1000)),
                    Mutations.SetCell(CF, "status", i % 2 == 0 ? "active" : "inactive", new BigtableVersion(1000)),
                    Mutations.SetCell(CF, "count", $"{i * 10}", new BigtableVersion(1000)),
                    Mutations.SetCell(CF2, "data", $"payload-{prefix}", new BigtableVersion(1000)));
            }
        }
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

    #region RowKeyRegex patterns

    [Fact]
    public async Task RowKeyRegex_exact_key()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("alpha-001"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task RowKeyRegex_prefix_with_wildcard()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("alpha-.*"));
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task RowKeyRegex_suffix_pattern()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex(".*-001"));
        rows.Should().HaveCount(3); // alpha-001, beta-001, gamma-001
    }

    [Fact]
    public async Task RowKeyRegex_alternation()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("alpha-001|beta-001"));
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task RowKeyRegex_character_class()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("[ab].*-003"));
        rows.Should().HaveCount(2); // alpha-003, beta-003
    }

    [Fact]
    public async Task RowKeyRegex_negated_character_class()
    {
        // Match keys not starting with 'a' or 'b'
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("[^ab].*"));
        rows.Should().HaveCount(3); // gamma-001..003
    }

    [Fact]
    public async Task RowKeyRegex_quantifier_plus()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("alpha-0+1"));
        rows.Should().ContainSingle(); // alpha-001
    }

    [Fact]
    public async Task RowKeyRegex_dot_matches_any()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("alpha-.0."));
        rows.Should().HaveCount(5); // alpha-001 through alpha-005
    }

    [Fact]
    public async Task RowKeyRegex_escaped_hyphen()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("alpha\\-001"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task RowKeyRegex_star_matches_all()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex(".*"));
        rows.Should().HaveCount(13);
    }

    [Fact]
    public async Task RowKeyRegex_no_match()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("delta-.*"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task RowKeyRegex_digit_range()
    {
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("alpha-00[1-3]"));
        rows.Should().HaveCount(3);
    }

    #endregion

    #region ColumnQualifierRegex patterns

    [Fact]
    public async Task ColumnQualifierRegex_exact_column()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("alpha-001"), RowFilters.ColumnQualifierRegex("name"));
        rows.Should().ContainSingle();
        rows[0].Families.SelectMany(f => f.Columns).Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("name");
    }

    [Fact]
    public async Task ColumnQualifierRegex_prefix_match()
    {
        // "na.*" matches "name", not "status" or "count"
        var rows = await ReadAll(RowSet.FromRowKeys("alpha-001"), RowFilters.ColumnQualifierRegex("na.*"));
        rows.Should().ContainSingle();
        var allQuals = rows[0].Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8());
        allQuals.Should().Contain("name");
        allQuals.Should().NotContain("status");
    }

    [Fact]
    public async Task ColumnQualifierRegex_alternation()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("alpha-001"),
            RowFilters.ColumnQualifierRegex("name|count"));
        var quals = rows[0].Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).Distinct().ToList();
        quals.Should().Contain("name");
        quals.Should().Contain("count");
        quals.Should().NotContain("status");
    }

    [Fact]
    public async Task ColumnQualifierRegex_char_class()
    {
        // [ns].* matches "name", "status" but not "count", "data"
        var rows = await ReadAll(RowSet.FromRowKeys("alpha-001"),
            RowFilters.ColumnQualifierRegex("[ns].*"));
        var quals = rows[0].Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).Distinct().ToList();
        quals.Should().Contain("name");
        quals.Should().Contain("status");
        quals.Should().NotContain("count");
    }

    [Fact]
    public async Task ColumnQualifierRegex_no_match()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("alpha-001"),
            RowFilters.ColumnQualifierRegex("zzz.*"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ColumnQualifierRegex_dot_star_matches_all()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("alpha-001"), RowFilters.ColumnQualifierRegex(".*"));
        var allQuals = rows[0].Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).Distinct().ToList();
        allQuals.Should().HaveCountGreaterThanOrEqualTo(3); // name, status, count (+data from cf2)
    }

    #endregion

    #region ValueRegex patterns

    [Fact]
    public async Task ValueRegex_exact_value()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("alpha-001"), RowFilters.ValueRegex("active"));
        // alpha-001 has status=inactive, so this should match nothing
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ValueRegex_pattern_with_digits()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("alpha-003"), RowFilters.ValueRegex("[0-9]+"));
        rows.Should().ContainSingle();
        // "count" = "30" matches, "Name-alpha-3" doesn't fully match "[0-9]+"
        var vals = rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Select(c => c.Value.ToStringUtf8()).ToList();
        vals.Should().AllSatisfy(v => v.Should().MatchRegex("^[0-9]+$"));
    }

    [Fact]
    public async Task ValueRegex_prefix_pattern()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("alpha-001"), RowFilters.ValueRegex("Name-.*"));
        rows.Should().ContainSingle();
        var vals = rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Select(c => c.Value.ToStringUtf8()).ToList();
        vals.Should().AllSatisfy(v => v.Should().StartWith("Name-"));
    }

    [Fact]
    public async Task ValueRegex_contains_pattern()
    {
        // ".*alpha.*" matches cells with "alpha" in the value
        var rows = await ReadAll(RowSet.FromRowKeys("alpha-001"), RowFilters.ValueRegex(".*alpha.*"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task ValueRegex_alternation()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("alpha-002"),
            RowFilters.ValueRegex("active|inactive"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task ValueRegex_case_sensitive()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("alpha-002"), RowFilters.ValueRegex("Active"));
        rows.Should().BeEmpty(); // "Active" != "active"
    }

    [Fact]
    public async Task ValueRegex_no_match()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("alpha-001"), RowFilters.ValueRegex("NOMATCH"));
        rows.Should().BeEmpty();
    }

    #endregion

    #region FamilyNameRegex patterns

    [Fact]
    public async Task FamilyNameRegex_exact_family()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("alpha-001"), RowFilters.FamilyNameRegex("cf"));
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF);
    }

    [Fact]
    public async Task FamilyNameRegex_alternation()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("alpha-001"), RowFilters.FamilyNameRegex("cf|cf2"));
        rows.Should().ContainSingle();
        rows[0].Families.Select(f => f.Name).Should().Contain(new[] { CF, CF2 });
    }

    [Fact]
    public async Task FamilyNameRegex_pattern_with_digit()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("alpha-001"), RowFilters.FamilyNameRegex("cf[0-9]"));
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF2);
    }

    [Fact]
    public async Task FamilyNameRegex_star_matches_all_families()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("alpha-001"), RowFilters.FamilyNameRegex(".*"));
        rows.Should().ContainSingle();
        rows[0].Families.Should().HaveCount(2); // cf, cf2 (cf3 empty)
    }

    [Fact]
    public async Task FamilyNameRegex_no_match()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("alpha-001"), RowFilters.FamilyNameRegex("zzz"));
        rows.Should().BeEmpty();
    }

    #endregion

    #region Combined regex chains

    [Fact]
    public async Task Chain_rowkey_and_value_regex()
    {
        var filter = RowFilters.Chain(
            RowFilters.RowKeyRegex("alpha-.*"),
            RowFilters.ValueRegex("active"));
        var rows = await ReadAll(filter: filter);
        // alpha-002, alpha-004 have status=active
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Chain_family_regex_and_column_regex()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameRegex("cf"),
            RowFilters.ColumnQualifierRegex("name"));
        var rows = await ReadAll(RowSet.FromRowKeys("alpha-001"), filter);
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF);
        rows[0].Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("name");
    }

    [Fact]
    public async Task Chain_three_regex_filters()
    {
        var filter = RowFilters.Chain(
            RowFilters.RowKeyRegex("beta-.*"),
            RowFilters.FamilyNameRegex("cf"),
            RowFilters.ColumnQualifierRegex("status"));
        var rows = await ReadAll(filter: filter);
        rows.Should().HaveCount(5); // all beta rows
        foreach (var row in rows)
        {
            row.Families.Should().ContainSingle().Which.Name.Should().Be(CF);
            row.Families[0].Columns.Should().ContainSingle()
                .Which.Qualifier.ToStringUtf8().Should().Be("status");
        }
    }

    [Fact]
    public async Task Interleave_different_value_regex()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ValueRegex("active"),
            RowFilters.ValueRegex("inactive"));
        var rows = await ReadAll(RowSet.FromRowKeys("alpha-001"), filter);
        rows.Should().ContainSingle();
        // Both "active" and "inactive" cells returned
    }

    [Fact]
    public async Task Chain_rowkey_regex_then_version_limit()
    {
        var filter = RowFilters.Chain(
            RowFilters.RowKeyRegex("gamma-.*"),
            RowFilters.CellsPerColumnLimit(1));
        var rows = await ReadAll(filter: filter);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Condition_with_value_regex_predicate()
    {
        // Predicate: does row contain "active"?
        // True: strip values. False: pass all.
        var filter = RowFilters.Condition(
            RowFilters.ValueRegex("active"),
            RowFilters.StripValueTransformer(),
            RowFilters.PassAllFilter());
        var rows = await ReadAll(RowSet.FromRowKeys("alpha-002"), filter);
        // alpha-002 has status=active, so true branch applies
        rows.Should().ContainSingle();
        rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Should().AllSatisfy(c => c.Value.Length.Should().Be(0));
    }

    [Fact]
    public async Task Condition_with_value_regex_false_branch()
    {
        var filter = RowFilters.Condition(
            RowFilters.ValueRegex("active"),
            RowFilters.StripValueTransformer(),
            RowFilters.FamilyNameExact(CF));
        var rows = await ReadAll(RowSet.FromRowKeys("alpha-001"), filter);
        // alpha-001 has status=inactive, no "active" value → false branch
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF);
        // Values should still be present (false branch doesn't strip)
        rows[0].Families[0].Columns.SelectMany(c => c.Cells)
            .Should().Contain(c => c.Value.Length > 0);
    }

    #endregion

    #region Regex with special characters in data

    [Fact]
    public async Task ValueRegex_matching_hyphenated_value()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("alpha-001"),
            RowFilters.ValueRegex("Name-alpha-1"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task ValueRegex_escaped_dot()
    {
        // "\." should match literal dot, but our values don't have dots
        var rows = await ReadAll(RowSet.FromRowKeys("alpha-001"),
            RowFilters.ValueRegex("Name\\.alpha.+"));
        rows.Should().BeEmpty(); // No dots in values
    }

    [Fact]
    public async Task RowKeyRegex_escaped_dot()
    {
        // "alpha\\.001" should not match "alpha-001" (dot is escaped)
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("alpha\\.001"));
        rows.Should().BeEmpty();
    }

    #endregion
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Comprehensive filter regex integration tests — RE2 patterns on row keys,
/// column qualifiers, family names, and values.
///
/// Ref: https://cloud.google.com/bigtable/docs/using-filters#regex
///   "Bigtable uses RE2 syntax for regular expressions."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class FilterRegexIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "regex-tests";
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public FilterRegexIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
        await SeedData();
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task SeedData()
    {
        // Row keys for regex matching
        var entries = new (string Key, string Family, string Qualifier, string Value)[]
        {
            ("rx-abc", CF, "col1", "hello"),
            ("rx-abd", CF, "col2", "world"),
            ("rx-bcd", CF, "col1", "HELLO"),
            ("rx-xyz", CF, "col10", "12345"),
            ("rx-xyz", CF2, "col1", "fam2val"),
            ("rx-123", CF, "num", "42"),
            ("rx-456", CF, "num", "99"),
            ("rx-test-1", CF, "data", "foo-bar"),
            ("rx-test-2", CF, "data", "foo-baz"),
            ("rx-test-3", CF, "data", "qux"),
        };
        foreach (var (key, fam, qual, val) in entries)
        {
            await Client.MutateRowAsync(TN, key,
                Mutations.SetCell(fam, qual, val, new BigtableVersion(1000)));
        }
    }

    #region RowKeyRegex

    [Fact]
    public async Task RowKeyRegex_prefix_match()
    {
        var filter = RowFilters.RowKeyRegex("rx-ab.*");
        var rows = await ReadWithFilter(filter);
        rows.Should().HaveCount(2);
        rows.Select(r => r.Key.ToStringUtf8()).Should().OnlyContain(k => k.StartsWith("rx-ab"));
    }

    [Fact]
    public async Task RowKeyRegex_suffix_match()
    {
        var filter = RowFilters.RowKeyRegex(".*cd");
        var rows = await ReadWithFilter(filter);
        rows.Should().HaveCount(1);
        rows[0].Key.ToStringUtf8().Should().Be("rx-bcd");
    }

    [Fact]
    public async Task RowKeyRegex_character_class()
    {
        // Match keys containing digits
        var filter = RowFilters.RowKeyRegex("rx-[0-9]+");
        var rows = await ReadWithFilter(filter);
        rows.Should().HaveCount(2); // rx-123, rx-456
    }

    [Fact]
    public async Task RowKeyRegex_alternation()
    {
        var filter = RowFilters.RowKeyRegex("rx-abc|rx-xyz");
        var rows = await ReadWithFilter(filter);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task RowKeyRegex_dot_star_matches_all()
    {
        // .* should match all row keys
        var filter = RowFilters.RowKeyRegex(".*");
        var rows = await ReadWithFilter(filter);
        rows.Should().HaveCountGreaterThanOrEqualTo(9);
    }

    [Fact]
    public async Task RowKeyRegex_no_match()
    {
        var filter = RowFilters.RowKeyRegex("nonexistent_pattern_xyz");
        var rows = await ReadWithFilter(filter);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task RowKeyRegex_exact_match()
    {
        var filter = RowFilters.RowKeyRegex("rx-abc");
        var rows = await ReadWithFilter(filter);
        rows.Should().ContainSingle();
    }

    #endregion

    #region ColumnQualifierRegex

    [Fact]
    public async Task ColumnQualifierRegex_exact()
    {
        var rk = new BigtableByteString("rx-abc");
        var filter = RowFilters.ColumnQualifierRegex("col1");
        var rows = await ReadWithFilter(filter, RowSet.FromRowKeys(rk));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("col1");
    }

    [Fact]
    public async Task ColumnQualifierRegex_prefix_pattern()
    {
        var rk = new BigtableByteString("rx-xyz");
        var filter = RowFilters.ColumnQualifierRegex("col.*");
        var rows = await ReadWithFilter(filter, RowSet.FromRowKeys(rk));
        rows.Should().ContainSingle();
        // rx-xyz has col10 in cf and col1 in cf2
        var allCols = rows[0].Families.SelectMany(f => f.Columns)
            .Select(c => c.Qualifier.ToStringUtf8()).ToList();
        allCols.Should().OnlyContain(c => c.StartsWith("col"));
    }

    [Fact]
    public async Task ColumnQualifierRegex_digit_pattern()
    {
        var filter = RowFilters.ColumnQualifierRegex("col[0-9]+");
        var rows = await ReadWithFilter(filter, RowSet.FromRowKeys("rx-abc"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task ColumnQualifierRegex_no_match_returns_empty()
    {
        var filter = RowFilters.ColumnQualifierRegex("zzz_nonexistent");
        var rows = await ReadWithFilter(filter, RowSet.FromRowKeys("rx-abc"));
        rows.Should().BeEmpty();
    }

    #endregion

    #region ValueRegex

    [Fact]
    public async Task ValueRegex_case_sensitive()
    {
        // RE2 is case-sensitive by default
        var filter = RowFilters.ValueRegex("hello");
        var rows = await ReadWithFilter(filter);
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("rx-abc");
    }

    [Fact]
    public async Task ValueRegex_uppercase()
    {
        var filter = RowFilters.ValueRegex("HELLO");
        var rows = await ReadWithFilter(filter);
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("rx-bcd");
    }

    [Fact]
    public async Task ValueRegex_prefix_pattern()
    {
        var filter = RowFilters.ValueRegex("foo-.*");
        var rows = await ReadWithFilter(filter);
        rows.Should().HaveCount(2); // foo-bar, foo-baz
    }

    [Fact]
    public async Task ValueRegex_digit_pattern()
    {
        var filter = RowFilters.ValueRegex("[0-9]+");
        var rows = await ReadWithFilter(filter);
        rows.Should().HaveCountGreaterThanOrEqualTo(3); // 12345, 42, 99
    }

    [Fact]
    public async Task ValueRegex_dot_star_matches_all()
    {
        var filter = RowFilters.ValueRegex(".*");
        var rows = await ReadWithFilter(filter);
        rows.Count.Should().BeGreaterThanOrEqualTo(9);
    }

    [Fact]
    public async Task ValueRegex_exact_value()
    {
        var filter = RowFilters.ValueRegex("qux");
        var rows = await ReadWithFilter(filter);
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("rx-test-3");
    }

    #endregion

    #region FamilyNameRegex

    [Fact]
    public async Task FamilyNameRegex_selects_one_family()
    {
        var rk = new BigtableByteString("rx-xyz");
        var filter = RowFilters.FamilyNameRegex("^cf$");
        var rows = await ReadWithFilter(filter, RowSet.FromRowKeys(rk));
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be("cf");
    }

    [Fact]
    public async Task FamilyNameRegex_alternation()
    {
        var rk = new BigtableByteString("rx-xyz");
        var filter = RowFilters.FamilyNameRegex("cf|cf2");
        var rows = await ReadWithFilter(filter, RowSet.FromRowKeys(rk));
        rows.Should().ContainSingle();
        rows[0].Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task FamilyNameRegex_pattern()
    {
        var rk = new BigtableByteString("rx-xyz");
        var filter = RowFilters.FamilyNameRegex("cf.*");
        var rows = await ReadWithFilter(filter, RowSet.FromRowKeys(rk));
        rows.Should().ContainSingle();
        rows[0].Families.Should().HaveCount(2);
    }

    #endregion

    #region Combined regex filters

    [Fact]
    public async Task Chain_rowkey_and_value_regex()
    {
        var filter = RowFilters.Chain(
            RowFilters.RowKeyRegex("rx-test-.*"),
            RowFilters.ValueRegex("foo-.*"));
        var rows = await ReadWithFilter(filter);
        rows.Should().HaveCount(2); // rx-test-1 and rx-test-2
    }

    [Fact]
    public async Task Chain_family_and_qualifier_regex()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameRegex("cf"),
            RowFilters.ColumnQualifierRegex("num"));
        var rows = await ReadWithFilter(filter);
        rows.Should().HaveCount(2); // rx-123, rx-456
    }

    [Fact]
    public async Task Interleave_multiple_value_patterns()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ValueRegex("hello"),
            RowFilters.ValueRegex("world"));
        var rows = await ReadWithFilter(filter);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Chain_three_regex_filters()
    {
        var filter = RowFilters.Chain(
            RowFilters.RowKeyRegex("rx-test-.*"),
            RowFilters.ColumnQualifierRegex("data"),
            RowFilters.ValueRegex("foo-.*"));
        var rows = await ReadWithFilter(filter);
        rows.Should().HaveCount(2);
    }

    #endregion

    #region Helpers

    private async Task<List<Row>> ReadWithFilter(RowFilter filter, RowSet? rowSet = null)
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet, filter: filter))
        {
            rows.Add(row);
        }
        return rows;
    }

    #endregion
}

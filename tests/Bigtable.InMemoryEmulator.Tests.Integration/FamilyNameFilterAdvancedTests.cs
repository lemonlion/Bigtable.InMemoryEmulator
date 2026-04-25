using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for family name regex filter and family-scoped operations.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
///   "family_name_regex_filter: Matches only cells from columns whose families satisfy the given RE2 regex."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class FamilyNameFilterAdvancedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "fam-filter";

    public FamilyNameFilterAdvancedTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { "cf1", "cf2", "data", "meta", "log" });
        var tn = TN;
        await _fixture.Client.MutateRowAsync(tn, "ff-r1",
            Mutations.SetCell("cf1", "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell("data", "c", "3", new BigtableVersion(1000)),
            Mutations.SetCell("meta", "d", "4", new BigtableVersion(1000)),
            Mutations.SetCell("log", "e", "5", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<(string Fam, string Col, string Val)>> ReadCells(string rowKey, RowFilter filter)
    {
        var cells = new List<(string, string, string)>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys(rowKey), filter: filter))
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    foreach (var cell in col.Cells)
                        cells.Add((fam.Name, col.Qualifier.ToStringUtf8(), cell.Value.ToStringUtf8()));
        return cells;
    }

    #region Exact family match

    [Fact]
    public async Task FamilyRegex_exact_match()
    {
        var cells = await ReadCells("ff-r1", RowFilters.FamilyNameRegex("cf1"));
        cells.Should().ContainSingle().Which.Fam.Should().Be("cf1");
    }

    [Fact]
    public async Task FamilyRegex_nonexistent_family()
    {
        var cells = await ReadCells("ff-r1", RowFilters.FamilyNameRegex("nonexistent"));
        cells.Should().BeEmpty();
    }

    #endregion

    #region Regex patterns

    [Fact]
    public async Task FamilyRegex_prefix_pattern()
    {
        var cells = await ReadCells("ff-r1", RowFilters.FamilyNameRegex("cf.*"));
        cells.Should().HaveCount(2);
        cells.Select(c => c.Fam).Distinct().Should().BeEquivalentTo(new[] { "cf1", "cf2" });
    }

    [Fact]
    public async Task FamilyRegex_alternation()
    {
        var cells = await ReadCells("ff-r1", RowFilters.FamilyNameRegex("data|meta"));
        cells.Should().HaveCount(2);
        cells.Select(c => c.Fam).Distinct().Should().BeEquivalentTo(new[] { "data", "meta" });
    }

    [Fact]
    public async Task FamilyRegex_dot_star_matches_all()
    {
        var cells = await ReadCells("ff-r1", RowFilters.FamilyNameRegex(".*"));
        cells.Should().HaveCount(5);
    }

    [Fact]
    public async Task FamilyRegex_character_class()
    {
        var cells = await ReadCells("ff-r1", RowFilters.FamilyNameRegex("cf[12]"));
        cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task FamilyRegex_single_char_wildcard()
    {
        var cells = await ReadCells("ff-r1", RowFilters.FamilyNameRegex("cf."));
        cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task FamilyRegex_full_match_required()
    {
        // RE2 full match: "cf" should NOT match "cf1"
        var cells = await ReadCells("ff-r1", RowFilters.FamilyNameRegex("cf"));
        cells.Should().BeEmpty();
    }

    [Fact]
    public async Task FamilyRegex_three_letter_families()
    {
        // [a-z]{3} matches exactly 3 lowercase letters — only "log" qualifies
        // cf1/cf2 contain a digit, data/meta are 4 chars
        var cells = await ReadCells("ff-r1", RowFilters.FamilyNameRegex("[a-z]{3}"));
        cells.Should().ContainSingle();
        cells[0].Fam.Should().Be("log");
    }

    #endregion

    #region Family filter combined with other filters

    [Fact]
    public async Task Family_then_qualifier()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameRegex("cf.*"),
            RowFilters.ColumnQualifierExact("a"));
        var cells = await ReadCells("ff-r1", filter);
        cells.Should().ContainSingle();
        cells[0].Fam.Should().Be("cf1");
        cells[0].Col.Should().Be("a");
    }

    [Fact]
    public async Task Family_then_value()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameRegex("data|meta"),
            RowFilters.ValueRegex("3"));
        var cells = await ReadCells("ff-r1", filter);
        cells.Should().ContainSingle().Which.Fam.Should().Be("data");
    }

    [Fact]
    public async Task Family_then_cells_limit()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameRegex(".*"),
            RowFilters.CellsPerRowLimit(2));
        var cells = await ReadCells("ff-r1", filter);
        cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task Family_then_strip_value()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameRegex("data"),
            RowFilters.StripValueTransformer());
        var cells = await ReadCells("ff-r1", filter);
        cells.Should().ContainSingle();
        cells[0].Val.Should().BeEmpty();
    }

    #endregion

    #region Family filter with interleave

    [Fact]
    public async Task Interleave_family_filters()
    {
        var filter = RowFilters.Interleave(
            RowFilters.FamilyNameRegex("cf1"),
            RowFilters.FamilyNameRegex("log"));
        var cells = await ReadCells("ff-r1", filter);
        cells.Should().HaveCount(2);
        cells.Select(c => c.Fam).Should().BeEquivalentTo(new[] { "cf1", "log" });
    }

    #endregion

    #region Family filter in condition

    [Fact]
    public async Task Condition_predicate_on_family()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.FamilyNameRegex("data"), RowFilters.ValueRegex("3")),
            RowFilters.FamilyNameRegex("meta"),
            RowFilters.FamilyNameRegex("log"));
        var cells = await ReadCells("ff-r1", filter);
        // data has value "3" → true → get meta family
        cells.Should().ContainSingle().Which.Fam.Should().Be("meta");
    }

    #endregion
}

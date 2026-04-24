using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for family name regex filter.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class FamilyNameRegexTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "fnr-filt";

    public FamilyNameRegexTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { "profile", "metrics", "metadata", "logs", "tags" });
        // Write data to each family for several rows
        for (int i = 0; i < 5; i++)
        {
            await Client.MutateRowAsync(TN, $"r{i}",
                Mutations.SetCell("profile", "name", $"user{i}", new BigtableVersion(1000)),
                Mutations.SetCell("metrics", "count", $"{i}", new BigtableVersion(1000)),
                Mutations.SetCell("metadata", "created", "2024-01-01", new BigtableVersion(1000)),
                Mutations.SetCell("logs", "entry", $"log{i}", new BigtableVersion(1000)),
                Mutations.SetCell("tags", "label", $"tag{i}", new BigtableVersion(1000)));
        }
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

    private List<string> GetFamilies(List<Row> rows)
    {
        return rows.SelectMany(r => r.Families).Select(f => f.Name).Distinct().ToList();
    }

    #region Exact family match

    [Fact]
    public async Task FamilyNameRegex_exact_match()
    {
        var rows = await ReadAll(filter: RowFilters.FamilyNameRegex("profile"));
        rows.Should().HaveCount(5);
        GetFamilies(rows).Should().ContainSingle().Which.Should().Be("profile");
    }

    [Fact]
    public async Task FamilyNameRegex_exact_metrics()
    {
        var rows = await ReadAll(filter: RowFilters.FamilyNameRegex("metrics"));
        rows.Should().HaveCount(5);
        GetFamilies(rows).Should().ContainSingle().Which.Should().Be("metrics");
    }

    [Fact]
    public async Task FamilyNameRegex_no_match()
    {
        var rows = await ReadAll(filter: RowFilters.FamilyNameRegex("nonexistent"));
        rows.Should().BeEmpty();
    }

    #endregion

    #region Prefix pattern

    [Fact]
    public async Task FamilyNameRegex_prefix_meta()
    {
        var rows = await ReadAll(filter: RowFilters.FamilyNameRegex("meta.*"));
        GetFamilies(rows).Should().ContainSingle().Which.Should().Be("metadata");
    }

    [Fact]
    public async Task FamilyNameRegex_prefix_log()
    {
        var rows = await ReadAll(filter: RowFilters.FamilyNameRegex("log.*"));
        GetFamilies(rows).Should().ContainSingle().Which.Should().Be("logs");
    }

    #endregion

    #region Alternation

    [Fact]
    public async Task FamilyNameRegex_alternation_two()
    {
        var rows = await ReadAll(filter: RowFilters.FamilyNameRegex("profile|metrics"));
        GetFamilies(rows).Should().HaveCount(2);
        GetFamilies(rows).Should().Contain("profile");
        GetFamilies(rows).Should().Contain("metrics");
    }

    [Fact]
    public async Task FamilyNameRegex_alternation_three()
    {
        var rows = await ReadAll(filter: RowFilters.FamilyNameRegex("profile|metrics|tags"));
        GetFamilies(rows).Should().HaveCount(3);
    }

    #endregion

    #region Character classes

    [Fact]
    public async Task FamilyNameRegex_char_class_mM()
    {
        // Matches families starting with 'm': "metrics", "metadata"
        var rows = await ReadAll(filter: RowFilters.FamilyNameRegex("m.*"));
        GetFamilies(rows).Should().HaveCount(2);
    }

    [Fact]
    public async Task FamilyNameRegex_length_pattern()
    {
        // Families of exactly 4 characters: "logs", "tags"
        var rows = await ReadAll(filter: RowFilters.FamilyNameRegex("[a-z]{4}"));
        GetFamilies(rows).Should().HaveCount(2);
    }

    #endregion

    #region Combined with other filters

    [Fact]
    public async Task FamilyNameRegex_chain_with_value()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameRegex("profile"),
            RowFilters.ValueExact("user0"));
        var rows = await ReadAll(filter: filter);
        rows.Should().ContainSingle().Which.Key.ToStringUtf8().Should().Be("r0");
    }

    [Fact]
    public async Task FamilyNameRegex_chain_with_column_qualifier()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameRegex("metrics|logs"),
            RowFilters.ColumnQualifierRegex("count"));
        var rows = await ReadAll(filter: filter);
        rows.Should().HaveCount(5);
        GetFamilies(rows).Should().ContainSingle().Which.Should().Be("metrics");
    }

    [Fact]
    public async Task FamilyNameRegex_interleave_families()
    {
        var filter = RowFilters.Interleave(
            RowFilters.FamilyNameRegex("profile"),
            RowFilters.FamilyNameRegex("tags"));
        var rows = await ReadAll(filter: filter);
        rows.Should().HaveCount(5);
        GetFamilies(rows).Should().HaveCount(2);
    }

    [Fact]
    public async Task FamilyNameRegex_with_row_key_filter()
    {
        var filter = RowFilters.Chain(
            RowFilters.RowKeyRegex("r[0-2]"),
            RowFilters.FamilyNameRegex("profile"));
        var rows = await ReadAll(filter: filter);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task FamilyNameRegex_with_cells_per_row_limit()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameRegex(".*"),
            RowFilters.CellsPerRowLimit(2));
        var rows = await ReadAll(filter: filter);
        foreach (var row in rows)
        {
            var cellCount = row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();
            cellCount.Should().BeLessThanOrEqualTo(2);
        }
    }

    #endregion

    #region Dot-star and edge cases

    [Fact]
    public async Task FamilyNameRegex_dot_star_matches_all()
    {
        var rows = await ReadAll(filter: RowFilters.FamilyNameRegex(".*"));
        rows.Should().HaveCount(5);
        GetFamilies(rows).Should().HaveCount(5);
    }

    [Fact]
    public async Task FamilyNameRegex_specific_row()
    {
        var rows = await ReadAll(
            rows: RowSet.FromRowKeys("r0"),
            filter: RowFilters.FamilyNameRegex("profile"));
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be("profile");
    }

    #endregion
}

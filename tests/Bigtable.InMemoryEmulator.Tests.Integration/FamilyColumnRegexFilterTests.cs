using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for Family and Column qualifier filtering with regex patterns.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class FamilyColumnRegexFilterTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "fcrf-tests";
    private TableName TN => _fixture.GetTableName(Table);

    public FamilyColumnRegexFilterTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { "data", "meta", "logs", "stats" });

        await Client.MutateRowAsync(TN, "fcrf-row1",
            Mutations.SetCell("data", "name", "Alice", new BigtableVersion(1000)),
            Mutations.SetCell("data", "email", "alice@test.com", new BigtableVersion(1000)),
            Mutations.SetCell("meta", "created", "2024-01-01", new BigtableVersion(1000)),
            Mutations.SetCell("meta", "updated", "2024-06-01", new BigtableVersion(1000)),
            Mutations.SetCell("logs", "entry1", "logged in", new BigtableVersion(1000)),
            Mutations.SetCell("logs", "entry2", "updated profile", new BigtableVersion(1000)),
            Mutations.SetCell("stats", "views", "100", new BigtableVersion(1000)),
            Mutations.SetCell("stats", "clicks", "50", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task FamilyNameExact_returns_only_that_family()
    {
        var request = MakeRequest(RowFilters.FamilyNameExact("data"));
        var families = await CollectFamilies(request);
        families.Should().ContainSingle("data");
    }

    [Fact]
    public async Task FamilyNameRegex_matches_multiple_families()
    {
        // "data" and "stats" both contain 'a'
        var request = MakeRequest(RowFilters.FamilyNameRegex("(data|stats)"));
        var families = await CollectFamilies(request);
        families.Should().HaveCount(2);
        families.Should().Contain("data");
        families.Should().Contain("stats");
    }

    [Fact]
    public async Task FamilyNameRegex_dot_star()
    {
        var request = MakeRequest(RowFilters.FamilyNameRegex(".*"));
        var families = await CollectFamilies(request);
        families.Should().HaveCount(4);
    }

    [Fact]
    public async Task FamilyNameRegex_prefix()
    {
        var request = MakeRequest(RowFilters.FamilyNameRegex("log.*"));
        var families = await CollectFamilies(request);
        families.Should().ContainSingle("logs");
    }

    [Fact]
    public async Task ColumnQualifierExact_returns_single_column()
    {
        var request = MakeRequest(RowFilters.ColumnQualifierExact("name"));
        var cols = await CollectColumns(request);
        cols.Should().ContainSingle("name");
    }

    [Fact]
    public async Task ColumnQualifierRegex_matches_multiple_columns()
    {
        var request = MakeRequest(RowFilters.ColumnQualifierRegex("entry.*"));
        var cols = await CollectColumns(request);
        cols.Should().HaveCount(2);
        cols.Should().Contain("entry1");
        cols.Should().Contain("entry2");
    }

    [Fact]
    public async Task Chain_family_and_column_filter()
    {
        var request = MakeRequest(RowFilters.Chain(
            RowFilters.FamilyNameExact("data"),
            RowFilters.ColumnQualifierExact("name")));
        var vals = await CollectValues(request);
        vals.Should().ContainSingle("Alice");
    }

    [Fact]
    public async Task Chain_family_regex_and_column_regex()
    {
        var request = MakeRequest(RowFilters.Chain(
            RowFilters.FamilyNameRegex("^meta$"),
            RowFilters.ColumnQualifierRegex("^created$")));
        var vals = await CollectValues(request);
        vals.Should().ContainSingle("2024-01-01");
    }

    [Fact]
    public async Task Interleave_two_exact_families()
    {
        var request = MakeRequest(RowFilters.Interleave(
            RowFilters.FamilyNameExact("data"),
            RowFilters.FamilyNameExact("stats")));
        var families = await CollectFamilies(request);
        families.Should().HaveCount(2);
    }

    [Fact]
    public async Task ColumnRange_within_family()
    {
        var request = MakeRequest(RowFilters.ColumnRange(ColumnRange.Closed("data", "email", "name")));
        var cols = await CollectColumns(request);
        cols.Should().HaveCount(2);
        cols.Should().Contain("email");
        cols.Should().Contain("name");
    }

    [Fact]
    public async Task ColumnRange_excludes_out_of_range()
    {
        var request = MakeRequest(RowFilters.ColumnRange(ColumnRange.Closed("logs", "entry1", "entry1")));
        var cols = await CollectColumns(request);
        cols.Should().ContainSingle("entry1");
    }

    [Fact]
    public async Task Family_filter_with_strip_value()
    {
        var request = MakeRequest(RowFilters.Chain(
            RowFilters.FamilyNameExact("stats"),
            RowFilters.StripValueTransformer()));
        var cells = new List<(string family, string col, int valLen)>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                cells.Add((f.Name, c.Qualifier.ToStringUtf8(), cell.Value.Length));

        cells.Should().HaveCount(2);
        cells.Should().AllSatisfy(c => c.valLen.Should().Be(0));
    }

    [Fact]
    public async Task Family_filter_with_label()
    {
        var request = MakeRequest(RowFilters.Chain(
            RowFilters.FamilyNameExact("meta"),
            new RowFilter { ApplyLabelTransformer = "meta-data" }));
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                cell.Labels.Should().Contain("meta-data");
    }

    [Fact]
    public async Task Column_filter_nonexistent_returns_empty_row()
    {
        var request = MakeRequest(RowFilters.ColumnQualifierExact("no-such-column"));
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(0);
    }

    [Fact]
    public async Task Family_filter_nonexistent_returns_empty()
    {
        var request = MakeRequest(RowFilters.FamilyNameExact("no-such-family"));
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(0);
    }

    [Fact]
    public async Task Condition_on_family_presence()
    {
        var request = MakeRequest(RowFilters.Condition(
            RowFilters.FamilyNameExact("stats"),
            new RowFilter { ApplyLabelTransformer = "has-stats" },
            RowFilters.BlockAllFilter()));
        var labels = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                labels.AddRange(cell.Labels);
        labels.Should().Contain("has-stats");
    }

    [Fact]
    public async Task Multiple_ColumnRange_via_interleave()
    {
        var request = MakeRequest(RowFilters.Interleave(
            RowFilters.ColumnRange(ColumnRange.Closed("data", "name", "name")),
            RowFilters.ColumnRange(ColumnRange.Closed("stats", "views", "views"))));
        var vals = await CollectValues(request);
        vals.Should().HaveCount(2);
        vals.Should().Contain("Alice");
        vals.Should().Contain("100");
    }

    [Fact]
    public async Task ColumnQualifierRegex_dot_star()
    {
        var request = MakeRequest(RowFilters.Chain(
            RowFilters.FamilyNameExact("data"),
            RowFilters.ColumnQualifierRegex(".*")));
        var cols = await CollectColumns(request);
        cols.Should().HaveCount(2);
    }

    [Fact]
    public async Task Family_and_value_filter_chain()
    {
        var request = MakeRequest(RowFilters.Chain(
            RowFilters.FamilyNameExact("stats"),
            RowFilters.ValueExact("100")));
        var cols = await CollectColumns(request);
        cols.Should().ContainSingle("views");
    }

    private ReadRowsRequest MakeRequest(RowFilter filter) =>
        new()
        {
            TableNameAsTableName = TN,
            Filter = filter,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fcrf-row1") } }
        };

    private async Task<List<string>> CollectFamilies(ReadRowsRequest request)
    {
        var families = new HashSet<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
                families.Add(f.Name);
        return families.ToList();
    }

    private async Task<List<string>> CollectColumns(ReadRowsRequest request)
    {
        var cols = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
                cols.Add(c.Qualifier.ToStringUtf8());
        return cols;
    }

    private async Task<List<string>> CollectValues(ReadRowsRequest request)
    {
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                vals.Add(cell.Value.ToStringUtf8());
        return vals;
    }
}

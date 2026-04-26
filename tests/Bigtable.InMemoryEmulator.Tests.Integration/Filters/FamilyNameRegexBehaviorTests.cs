using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class FamilyNameRegexBehaviorTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "fn-regex-beh";

    public FamilyNameRegexBehaviorTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { "data", "meta", "logs", "idx" });
        var tn = _fixture.GetTableName(Table);
        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("data", "c", "1"),
            Mutations.SetCell("meta", "c", "2"),
            Mutations.SetCell("logs", "c", "3"),
            Mutations.SetCell("idx", "c", "4"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task FamilyRegex_exact_match()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.FamilyNameRegex("data"));
        row.Should().NotBeNull();
        row!.Families.Should().ContainSingle().Which.Name.Should().Be("data");
    }

    [Fact]
    public async Task FamilyRegex_prefix()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.FamilyNameRegex("d.*"));
        row.Should().NotBeNull();
        row!.Families.Should().ContainSingle().Which.Name.Should().Be("data");
    }

    [Fact]
    public async Task FamilyRegex_alternation()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.FamilyNameRegex("data|meta"));
        row.Should().NotBeNull();
        row!.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task FamilyRegex_dot_star_matches_all()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.FamilyNameRegex(".*"));
        row.Should().NotBeNull();
        row!.Families.Should().HaveCount(4);
    }

    [Fact]
    public async Task FamilyRegex_no_match()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.FamilyNameRegex("nope"));
        row.Should().BeNull();
    }

    [Fact]
    public async Task FamilyRegex_character_class()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.FamilyNameRegex("[dl].*"));
        row.Should().NotBeNull();
        row!.Families.Should().HaveCount(2);
        row.Families.Select(f => f.Name).Should().Contain("data").And.Contain("logs");
    }

    [Fact]
    public async Task FamilyRegex_combined_with_column_filter()
    {
        var chain = RowFilters.Chain(
            RowFilters.FamilyNameRegex("data|meta"),
            RowFilters.ColumnQualifierExact("c"));
        var row = await Client.ReadRowAsync(TN, "r1", chain);
        row.Should().NotBeNull();
        row!.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task FamilyExact_match()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.FamilyNameExact("logs"));
        row.Should().NotBeNull();
        row!.Families.Should().ContainSingle().Which.Name.Should().Be("logs");
    }

    [Fact]
    public async Task FamilyRegex_three_letter_pattern()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.FamilyNameRegex("...[as]"));
        row.Should().NotBeNull();
        row!.Families.Select(f => f.Name).Should().Contain("data").And.Contain("logs");
    }

    [Fact]
    public async Task FamilyRegex_with_cells_per_row()
    {
        var chain = RowFilters.Chain(
            RowFilters.FamilyNameRegex(".*"),
            RowFilters.CellsPerRowLimit(2));
        var row = await Client.ReadRowAsync(TN, "r1", chain);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(2);
    }

    [Fact]
    public async Task FamilyRegex_case_sensitive()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.FamilyNameRegex("DATA"));
        row.Should().BeNull(); // Case sensitive
    }

    [Fact]
    public async Task FamilyRegex_single_char()
    {
        // "idx" is 3 chars, match 3 char families
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.FamilyNameRegex("...")); 
        row.Should().NotBeNull();
        row!.Families.Should().ContainSingle().Which.Name.Should().Be("idx");
    }

    [Fact]
    public async Task FamilyRegex_with_interleave_and_value_filter()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameRegex("data"),
            RowFilters.ValueRegex("1"));
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("1");
    }
}

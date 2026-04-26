using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ColumnQualifierRegexBehaviorTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "cqreg-beh";
    private const string CF = "cf";

    public ColumnQualifierRegexBehaviorTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        await Client.MutateRowAsync(TN, "r1",
            Mutations.SetCell(CF, "name", "Alice"),
            Mutations.SetCell(CF, "email", "alice@test.com"),
            Mutations.SetCell(CF, "age", "30"),
            Mutations.SetCell(CF, "addr-city", "London"),
            Mutations.SetCell(CF, "addr-zip", "SW1"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Qualifier_exact_match()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.ColumnQualifierExact("name"));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().ContainSingle();
    }

    [Fact]
    public async Task Qualifier_regex_prefix()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.ColumnQualifierRegex("addr-.*"));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(2);
    }

    [Fact]
    public async Task Qualifier_regex_suffix()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.ColumnQualifierRegex(".*e"));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(2);
        row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8())
            .Should().Contain("name").And.Contain("age"); // both end in 'e'
    }

    [Fact]
    public async Task Qualifier_regex_alternation()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.ColumnQualifierRegex("name|age"));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(2);
    }

    [Fact]
    public async Task Qualifier_regex_no_match()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.ColumnQualifierRegex("phone"));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Qualifier_regex_dot_star()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.ColumnQualifierRegex(".*"));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(5);
    }

    [Fact]
    public async Task Qualifier_regex_char_class()
    {
        // Match columns starting with a-e
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.ColumnQualifierRegex("[a-e].*"));
        row.Should().NotBeNull();
        var cols = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("email").And.Contain("age").And.Contain("addr-city").And.Contain("addr-zip");
    }

    [Fact]
    public async Task Qualifier_combined_with_value_filter()
    {
        var chain = RowFilters.Chain(
            RowFilters.ColumnQualifierRegex("addr-.*"),
            RowFilters.ValueRegex("London"));
        var row = await Client.ReadRowAsync(TN, "r1", chain);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().ContainSingle();
    }

    [Fact]
    public async Task Qualifier_range_filtering()
    {
        var range = ColumnRange.Closed(CF, "addr-city", "age");
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.ColumnRange(range));
        row.Should().NotBeNull();
        var cols = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("addr-city").And.Contain("addr-zip").And.Contain("age");
    }

    [Fact]
    public async Task Qualifier_regex_single_char()
    {
        // Only "age" is 3 chars
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.ColumnQualifierRegex("..."));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("age");
    }

    [Fact]
    public async Task Qualifier_regex_with_cells_per_row()
    {
        var chain = RowFilters.Chain(
            RowFilters.ColumnQualifierRegex(".*"),
            RowFilters.CellsPerRowLimit(3));
        var row = await Client.ReadRowAsync(TN, "r1", chain);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(3);
    }

    [Fact]
    public async Task Qualifier_regex_across_multiple_rows()
    {
        await Client.MutateRowAsync(TN, "r2",
            Mutations.SetCell(CF, "name", "Bob"),
            Mutations.SetCell(CF, "email", "bob@test.com"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ColumnQualifierRegex("name")))
            rows.Add(r);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Qualifier_case_sensitive()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.ColumnQualifierRegex("NAME"));
        row.Should().BeNull();
    }
}

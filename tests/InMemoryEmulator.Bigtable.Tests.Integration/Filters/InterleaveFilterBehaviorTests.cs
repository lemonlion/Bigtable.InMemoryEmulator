using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class InterleaveFilterBehaviorTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "il-beh";
    private const string CF = "cf";

    public InterleaveFilterBehaviorTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        await Client.MutateRowAsync(TN, "r1",
            Mutations.SetCell(CF, "name", "Alice"),
            Mutations.SetCell(CF, "age", "30"),
            Mutations.SetCell(CF, "city", "London"),
            Mutations.SetCell(CF, "email", "alice@test.com"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Interleave_two_column_filters()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.ColumnQualifierExact("age"));
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(2);
    }

    [Fact]
    public async Task Interleave_three_filters()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.ColumnQualifierExact("age"),
            RowFilters.ColumnQualifierExact("city"));
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(3);
    }

    [Fact]
    public async Task Interleave_with_pass_all()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.PassAllFilter());
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
        // PassAll returns all 4 columns, interleave unions them
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(4);
    }

    [Fact]
    public async Task Interleave_with_block_all()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.BlockAllFilter());
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().ContainSingle();
    }

    [Fact]
    public async Task Interleave_overlapping_filters()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierRegex("name|age"),
            RowFilters.ColumnQualifierRegex("age|city"));
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
        // Union of: name,age + age,city = name,age,city (dedup)
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(3);
    }

    [Fact]
    public async Task Interleave_all_block_returns_nothing()
    {
        var filter = RowFilters.Interleave(
            RowFilters.BlockAllFilter(),
            RowFilters.BlockAllFilter());
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().BeNull();
    }

    [Fact]
    public async Task Interleave_with_value_filters()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ValueExact("Alice"),
            RowFilters.ValueExact("30"));
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(2);
    }

    [Fact]
    public async Task Interleave_with_regex_filters()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierRegex("na.*"),
            RowFilters.ColumnQualifierRegex("ci.*"));
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(2);
    }

    [Fact]
    public async Task Interleave_on_missing_row()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.ColumnQualifierExact("age"));
        var row = await Client.ReadRowAsync(TN, "missing", filter);
        row.Should().BeNull();
    }

    [Fact]
    public async Task Interleave_across_multiple_rows()
    {
        await Client.MutateRowAsync(TN, "r2",
            Mutations.SetCell(CF, "name", "Bob"),
            Mutations.SetCell(CF, "city", "Paris"));
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.ColumnQualifierExact("city"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: filter)) rows.Add(r);
        rows.Should().HaveCount(2);
        foreach (var row in rows)
            row.Families.SelectMany(f => f.Columns).Should().HaveCount(2);
    }

    [Fact]
    public async Task Interleave_nested_in_chain()
    {
        var filter = RowFilters.Chain(
            RowFilters.Interleave(
                RowFilters.ColumnQualifierExact("name"),
                RowFilters.ColumnQualifierExact("age")),
            RowFilters.CellsPerRowLimit(1));
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().ContainSingle();
    }

    [Fact]
    [Trait(TestTraits.Target, TestTraits.InMemoryOnly)] // Go emulator rejects Interleave with <2 filters
    public async Task Interleave_single_filter()
    {
        var filter = RowFilters.Interleave(RowFilters.ColumnQualifierExact("name"));
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().ContainSingle();
    }

    [Fact]
    public async Task Chain_nested_in_interleave()
    {
        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("name"), RowFilters.ValueExact("Alice")),
            RowFilters.Chain(RowFilters.ColumnQualifierExact("age"), RowFilters.ValueExact("30")));
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(2);
    }
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowSingleRowTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "rrsr-tests";
    private const string CF = "cf";

    public ReadRowSingleRowTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        await Client.MutateRowAsync(TN, "row1",
            Mutations.SetCell(CF, "name", "Alice"),
            Mutations.SetCell(CF, "age", "30"),
            Mutations.SetCell(CF, "city", "London"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task ReadRow_returns_all_columns()
    {
        var row = await Client.ReadRowAsync(TN, "row1");
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(3);
    }

    [Fact]
    public async Task ReadRow_nonexistent_returns_null()
    {
        var row = await Client.ReadRowAsync(TN, "nonexistent");
        row.Should().BeNull();
    }

    [Fact]
    public async Task ReadRow_with_qualifier_filter()
    {
        var row = await Client.ReadRowAsync(TN, "row1", RowFilters.ColumnQualifierExact("name"));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().ContainSingle();
    }

    [Fact]
    public async Task ReadRow_with_value_filter()
    {
        var row = await Client.ReadRowAsync(TN, "row1", RowFilters.ValueExact("Alice"));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("Alice");
    }

    [Fact]
    public async Task ReadRow_with_block_all_returns_null()
    {
        var row = await Client.ReadRowAsync(TN, "row1", RowFilters.BlockAllFilter());
        row.Should().BeNull();
    }

    [Fact]
    public async Task ReadRow_with_pass_all_returns_everything()
    {
        var row = await Client.ReadRowAsync(TN, "row1", RowFilters.PassAllFilter());
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(3);
    }

    [Fact]
    public async Task ReadRow_with_cells_per_row_limit()
    {
        var row = await Client.ReadRowAsync(TN, "row1", RowFilters.CellsPerRowLimit(1));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().ContainSingle();
    }

    [Fact]
    public async Task ReadRow_with_strip_value()
    {
        var row = await Client.ReadRowAsync(TN, "row1", RowFilters.StripValueTransformer());
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .All(c => c.Value.IsEmpty).Should().BeTrue();
    }

    [Fact]
    public async Task ReadRow_key_is_correct()
    {
        var row = await Client.ReadRowAsync(TN, "row1");
        row!.Key.ToStringUtf8().Should().Be("row1");
    }

    [Fact]
    public async Task ReadRow_family_name_is_correct()
    {
        var row = await Client.ReadRowAsync(TN, "row1");
        row!.Families.Should().ContainSingle().Which.Name.Should().Be(CF);
    }

    [Fact]
    public async Task ReadRow_column_qualifiers_are_sorted()
    {
        var row = await Client.ReadRowAsync(TN, "row1");
        var qualifiers = row!.Families.SelectMany(f => f.Columns)
            .Select(c => c.Qualifier.ToStringUtf8()).ToList();
        qualifiers.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task ReadRow_after_update()
    {
        await Client.MutateRowAsync(TN, "row1", Mutations.SetCell(CF, "name", "Bob"));
        var row = await Client.ReadRowAsync(TN, "row1", RowFilters.Chain(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.CellsPerColumnLimit(1)));
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("Bob");
    }

    [Fact]
    public async Task ReadRow_column_range_filter()
    {
        var row = await Client.ReadRowAsync(TN, "row1",
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "age", "city")));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(2);
    }

    [Fact]
    public async Task ReadRow_value_regex()
    {
        var row = await Client.ReadRowAsync(TN, "row1", RowFilters.ValueRegex(".*li.*"));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("Alice");
    }

    [Fact]
    public async Task ReadRow_with_qualifier_regex()
    {
        // Match "age" or "city" using regex
        var row = await Client.ReadRowAsync(TN, "row1", RowFilters.ColumnQualifierRegex("age|city"));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(2);
    }
}

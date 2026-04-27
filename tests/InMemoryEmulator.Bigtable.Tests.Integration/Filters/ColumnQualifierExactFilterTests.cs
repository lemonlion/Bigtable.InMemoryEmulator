using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ColumnQualifierExactFilterTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "col-exact";
    private const string CF = "cf";

    public ColumnQualifierExactFilterTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        await Client.MutateRowAsync(TN, "r1",
            Mutations.SetCell(CF, "name", "alice"),
            Mutations.SetCell(CF, "age", "30"),
            Mutations.SetCell(CF, "email", "a@b.com"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Filter_exact_column()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.ColumnQualifierExact("name"));
        row.Should().NotBeNull();
        var cols = row!.Families.SelectMany(f => f.Columns).ToList();
        cols.Should().ContainSingle();
        cols[0].Qualifier.ToStringUtf8().Should().Be("name");
    }

    [Fact]
    public async Task Filter_nonexistent_column()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.ColumnQualifierExact("phone"));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Filter_preserves_value()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.ColumnQualifierExact("age"));
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("30");
    }

    [Fact]
    public async Task Case_sensitive()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.ColumnQualifierExact("Name"));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Chain_with_value_filter()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("email"),
            RowFilters.ValueRegex(".*@.*"));
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Interleave_two_columns()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.ColumnQualifierExact("age"));
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        var cols = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().HaveCount(2);
        cols.Should().Contain("name").And.Contain("age");
    }

    [Fact]
    public async Task All_rows_with_column_filter()
    {
        await Client.MutateRowAsync(TN, "r2", Mutations.SetCell(CF, "name", "bob"));
        await Client.MutateRowAsync(TN, "r3", Mutations.SetCell(CF, "age", "25"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ColumnQualifierExact("name")))
            rows.Add(r);
        rows.Should().HaveCount(2); // r1 and r2
    }

    [Fact]
    public async Task Column_exact_with_strip()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.StripValueTransformer());
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row!.Families[0].Columns[0].Cells[0].Value.Should().BeEmpty();
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("name");
    }

    [Fact]
    public async Task Empty_qualifier_no_match()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.ColumnQualifierExact(""));
        row.Should().BeNull();
    }
}

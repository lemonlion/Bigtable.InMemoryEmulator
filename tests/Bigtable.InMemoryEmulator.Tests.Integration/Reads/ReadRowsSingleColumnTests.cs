using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsSingleColumnTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "rr-1col";
    private const string CF = "cf";

    public ReadRowsSingleColumnTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        for (int i = 0; i < 10; i++)
            await Client.MutateRowAsync(TN, $"r{i:D2}",
                Mutations.SetCell(CF, "name", $"user-{i}"),
                Mutations.SetCell(CF, "score", $"{i * 10}"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Read_single_column_from_all_rows()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ColumnQualifierExact("name")))
            rows.Add(r);
        rows.Should().HaveCount(10);
        foreach (var row in rows)
            row.Families.SelectMany(f => f.Columns).Should().ContainSingle();
    }

    [Fact]
    public async Task Read_column_with_range()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN,
            RowSet.FromRowRanges(RowRange.Closed("r03", "r07")),
            RowFilters.ColumnQualifierExact("score")))
            rows.Add(r);
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task Column_filter_with_value_filter()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("score"),
            RowFilters.ValueExact("50"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: filter)) rows.Add(r);
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("r05");
    }

    [Fact]
    public async Task Column_filter_with_limit()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN,
            filter: RowFilters.ColumnQualifierExact("name"), rowsLimit: 3))
            rows.Add(r);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Column_absent_in_some_rows()
    {
        await Client.MutateRowAsync(TN, "r10", Mutations.SetCell(CF, "name", "special"));
        // r10 has only "name", no "score"
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ColumnQualifierExact("score")))
            rows.Add(r);
        rows.Should().HaveCount(10); // r00-r09
    }

    [Fact]
    public async Task Two_columns_via_interleave()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.ColumnQualifierExact("score"));
        var row = await Client.ReadRowAsync(TN, "r05", filter);
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(2);
    }

    [Fact]
    public async Task Strip_value_preserves_metadata()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.StripValueTransformer());
        var row = await Client.ReadRowAsync(TN, "r00", filter);
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("name");
        row.Families[0].Columns[0].Cells[0].Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Column_regex_matches_both()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ColumnQualifierRegex(".*")))
            rows.Add(r);
        rows.Should().HaveCount(10);
        foreach (var row in rows)
            row.Families.SelectMany(f => f.Columns).Should().HaveCount(2);
    }

    [Fact]
    public async Task Column_regex_partial()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ColumnQualifierRegex("na..")))
            rows.Add(r);
        rows.Should().HaveCount(10); // "name" matches "na.."
    }

    [Fact]
    public async Task Nonexistent_column_empty_results()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ColumnQualifierExact("missing")))
            rows.Add(r);
        rows.Should().BeEmpty();
    }
}

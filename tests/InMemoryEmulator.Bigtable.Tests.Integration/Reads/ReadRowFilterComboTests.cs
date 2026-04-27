using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowFilterComboTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "rr-fc";
    private const string CF = "cf";

    public ReadRowFilterComboTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        for (int i = 0; i < 20; i++)
        {
            await Client.MutateRowAsync(TN, $"user-{i:D3}",
                Mutations.SetCell(CF, "name", $"name-{i}"),
                Mutations.SetCell(CF, "age", $"{20 + i}"),
                Mutations.SetCell(CF, "active", i % 2 == 0 ? "true" : "false"));
        }
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Chain_qualifier_value()
    {
        var chain = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("active"),
            RowFilters.ValueExact("true"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: chain)) rows.Add(r);
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task Interleave_two_qualifiers()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.ColumnQualifierExact("age"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: filter)) rows.Add(r);
        rows.Should().HaveCount(20);
        foreach (var row in rows)
            row.Families.SelectMany(f => f.Columns).Should().HaveCount(2);
    }

    [Fact]
    public async Task Row_key_regex_with_limit()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex("user-00.*"), rowsLimit: 5))
            rows.Add(r);
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task Chain_family_qualifier_value()
    {
        var chain = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.ValueRegex("name-1.*"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: chain)) rows.Add(r);
        rows.Should().HaveCount(11); // name-1, name-10..name-19
    }

    [Fact]
    public async Task Condition_with_column_check()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("active"), RowFilters.ValueExact("true")),
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.BlockAllFilter());
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: filter)) rows.Add(r);
        rows.Should().HaveCount(10); // Only active=true rows pass; they show "name" column
    }

    [Fact]
    public async Task Chain_with_strip_value()
    {
        var chain = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.StripValueTransformer());
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: chain)) rows.Add(r);
        rows.Should().HaveCount(20);
        foreach (var row in rows)
            row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
                .All(c => c.Value.IsEmpty).Should().BeTrue();
    }

    [Fact]
    public async Task Interleave_with_cells_per_row()
    {
        var filter = RowFilters.Chain(
            RowFilters.Interleave(
                RowFilters.ColumnQualifierExact("name"),
                RowFilters.ColumnQualifierExact("age"),
                RowFilters.ColumnQualifierExact("active")),
            RowFilters.CellsPerRowLimit(2));
        var row = await Client.ReadRowAsync(TN, "user-000", filter);
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(2);
    }

    [Fact]
    public async Task Row_range_with_value_filter()
    {
        var rowSet = new RowSet { RowRanges = { RowRange.ClosedOpen("user-000", "user-010") } };
        var chain = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("active"),
            RowFilters.ValueExact("true"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet, filter: chain)) rows.Add(r);
        rows.Should().HaveCount(5); // 0,2,4,6,8
    }

    [Fact]
    public async Task Multiple_ranges_with_filter()
    {
        var rowSet = new RowSet
        {
            RowRanges = {
                RowRange.ClosedOpen("user-000", "user-003"),
                RowRange.ClosedOpen("user-010", "user-013"),
            }
        };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet, filter: RowFilters.ColumnQualifierExact("name")))
            rows.Add(r);
        rows.Should().HaveCount(6);
    }

    [Fact]
    public async Task Pass_all_with_column_filter()
    {
        var chain = RowFilters.Chain(
            RowFilters.PassAllFilter(),
            RowFilters.ColumnQualifierExact("age"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: chain)) rows.Add(r);
        rows.Should().HaveCount(20);
        foreach (var row in rows)
            row.Families.SelectMany(f => f.Columns).Should().ContainSingle();
    }

    [Fact]
    public async Task Cells_per_row_offset_with_qualifier()
    {
        var chain = RowFilters.Chain(
            RowFilters.PassAllFilter(),
            RowFilters.CellsPerRowOffset(1),
            RowFilters.CellsPerRowLimit(1));
        var row = await Client.ReadRowAsync(TN, "user-000", chain);
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().ContainSingle();
    }

    [Fact]
    public async Task Specific_keys_with_column_filter()
    {
        var rowSet = RowSet.FromRowKeys("user-005", "user-015");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet, filter: RowFilters.ColumnQualifierExact("active")))
            rows.Add(r);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Block_all_with_range()
    {
        var rowSet = new RowSet { RowRanges = { RowRange.ClosedOpen("user-000", "user-010") } };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet, filter: RowFilters.BlockAllFilter()))
            rows.Add(r);
        rows.Should().BeEmpty();
    }
}

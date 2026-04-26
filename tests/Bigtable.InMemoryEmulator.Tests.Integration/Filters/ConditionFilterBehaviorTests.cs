using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ConditionFilterBehaviorTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "cond-beh";
    private const string CF = "cf";

    public ConditionFilterBehaviorTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        await Client.MutateRowAsync(TN, "r1",
            Mutations.SetCell(CF, "status", "active"),
            Mutations.SetCell(CF, "name", "Alice"));
        await Client.MutateRowAsync(TN, "r2",
            Mutations.SetCell(CF, "status", "inactive"),
            Mutations.SetCell(CF, "name", "Bob"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task True_branch_when_predicate_matches()
    {
        var filter = RowFilters.Condition(
            RowFilters.ValueExact("active"),
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.PassAllFilter());
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
        // True branch: only name column
        row!.Families.SelectMany(f => f.Columns).Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("name");
    }

    [Fact]
    public async Task False_branch_when_predicate_no_match()
    {
        var filter = RowFilters.Condition(
            RowFilters.ValueExact("active"),
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.PassAllFilter());
        var row = await Client.ReadRowAsync(TN, "r2", filter);
        row.Should().NotBeNull();
        // False branch: all columns
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(2);
    }

    [Fact]
    public async Task Pass_all_predicate()
    {
        var filter = RowFilters.Condition(
            RowFilters.PassAllFilter(),
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.ColumnQualifierExact("status"));
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("name");
    }

    [Fact]
    public async Task Block_all_predicate_always_false()
    {
        var filter = RowFilters.Condition(
            RowFilters.BlockAllFilter(),
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.ColumnQualifierExact("status"));
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("status");
    }

    [Fact]
    public async Task True_branch_block_all()
    {
        var filter = RowFilters.Condition(
            RowFilters.PassAllFilter(),
            RowFilters.BlockAllFilter(),
            RowFilters.PassAllFilter());
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().BeNull();
    }

    [Fact]
    public async Task False_branch_block_all()
    {
        var filter = RowFilters.Condition(
            RowFilters.BlockAllFilter(),
            RowFilters.PassAllFilter(),
            RowFilters.BlockAllFilter());
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().BeNull();
    }

    [Fact]
    public async Task Column_predicate()
    {
        var filter = RowFilters.Condition(
            RowFilters.ColumnQualifierExact("status"),
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.BlockAllFilter());
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        // Predicate matches (status exists) -> true branch -> name only
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().ContainSingle();
    }

    [Fact]
    public async Task Condition_on_missing_row()
    {
        var filter = RowFilters.Condition(
            RowFilters.PassAllFilter(),
            RowFilters.PassAllFilter(),
            RowFilters.PassAllFilter());
        var row = await Client.ReadRowAsync(TN, "missing", filter);
        row.Should().BeNull();
    }

    [Fact]
    public async Task Condition_across_rows()
    {
        var filter = RowFilters.Condition(
            RowFilters.ValueExact("active"),
            RowFilters.CellsPerRowLimit(1),
            RowFilters.PassAllFilter());
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: filter)) rows.Add(r);
        rows.Should().HaveCount(2);
        // r1 has active -> true branch (1 cell), r2 doesn't -> false branch (all cells)
        var r1 = rows.First(r => r.Key.ToStringUtf8() == "r1");
        var r2 = rows.First(r => r.Key.ToStringUtf8() == "r2");
        r1.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().ContainSingle();
        r2.Families.SelectMany(f => f.Columns).Should().HaveCount(2);
    }

    [Fact]
    public async Task Nested_condition()
    {
        var inner = RowFilters.Condition(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.ValueRegex("Alice"),
            RowFilters.BlockAllFilter());
        var outer = RowFilters.Condition(
            RowFilters.PassAllFilter(),
            inner,
            RowFilters.PassAllFilter());
        var row = await Client.ReadRowAsync(TN, "r1", outer);
        // outer: predicate matches -> true branch (inner)
        // inner: predicate matches (name exists) -> true branch: ValueRegex("Alice")
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Condition_with_family_predicate()
    {
        var filter = RowFilters.Condition(
            RowFilters.FamilyNameExact(CF),
            RowFilters.CellsPerRowLimit(1),
            RowFilters.PassAllFilter());
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().ContainSingle();
    }

    [Fact]
    public async Task Condition_with_regex_predicate()
    {
        var filter = RowFilters.Condition(
            RowFilters.ValueRegex("act.*"),
            RowFilters.ColumnQualifierExact("status"),
            RowFilters.ColumnQualifierExact("name"));
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("status");
    }
}

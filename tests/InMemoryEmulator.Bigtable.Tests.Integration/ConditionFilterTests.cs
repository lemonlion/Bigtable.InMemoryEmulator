using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for condition filter (predicate true/false branches).
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ConditionFilterTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "cond-filt";
    private const string CF = "cf";

    public ConditionFilterTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
        // Status rows: active, pending, completed
        await Client.MutateRowAsync(TN, "cf-active",
            Mutations.SetCell(CF, "status", "active", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "name", "Alice", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "cf-pending",
            Mutations.SetCell(CF, "status", "pending", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "name", "Bob", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "cf-completed",
            Mutations.SetCell(CF, "status", "completed", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "name", "Charlie", new BigtableVersion(1000)));
        // Multi-family row
        await Client.MutateRowAsync(TN, "cf-multi",
            Mutations.SetCell(CF, "type", "admin", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "level", "5", new BigtableVersion(1000)));
        // Numeric rows
        for (int i = 0; i < 10; i++)
            await Client.MutateRowAsync(TN, $"cf-num-{i}",
                Mutations.SetCell(CF, "score", $"{i * 10}", new BigtableVersion(1000)));
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

    #region Basic condition

    [Fact]
    public async Task Condition_true_branch_applied()
    {
        // Predicate: status == "active"
        // True: return name only
        // False: return all
        var filter = RowFilters.Condition(
            predicateFilter: RowFilters.Chain(
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.ValueExact("active"),
                RowFilters.CellsPerColumnLimit(1)),
            trueFilter: RowFilters.ColumnQualifierExact("name"),
            falseFilter: RowFilters.PassAllFilter());

        var rows = await ReadAll(rows: RowSet.FromRowKeys("cf-active"), filter: filter);
        rows.Should().ContainSingle();
        var cols = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().ContainSingle().Which.Should().Be("name");
    }

    [Fact]
    public async Task Condition_false_branch_applied()
    {
        var filter = RowFilters.Condition(
            predicateFilter: RowFilters.Chain(
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.ValueExact("active"),
                RowFilters.CellsPerColumnLimit(1)),
            trueFilter: RowFilters.ColumnQualifierExact("name"),
            falseFilter: RowFilters.PassAllFilter());

        var rows = await ReadAll(rows: RowSet.FromRowKeys("cf-pending"), filter: filter);
        rows.Should().ContainSingle();
        var cols = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().HaveCount(2); // status + name
    }

    #endregion

    #region Block/Pass branches

    [Fact]
    public async Task Condition_true_pass_false_block()
    {
        // Only return rows where status == "active"
        var filter = RowFilters.Condition(
            predicateFilter: RowFilters.Chain(
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.ValueExact("active"),
                RowFilters.CellsPerColumnLimit(1)),
            trueFilter: RowFilters.PassAllFilter(),
            falseFilter: RowFilters.BlockAllFilter());

        var rows = await ReadAll(filter: filter);
        rows.Should().ContainSingle().Which.Key.ToStringUtf8().Should().Be("cf-active");
    }

    [Fact]
    public async Task Condition_true_block_false_pass()
    {
        // Return everything EXCEPT rows where status == "active"
        var filter = RowFilters.Condition(
            predicateFilter: RowFilters.Chain(
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.ValueExact("active"),
                RowFilters.CellsPerColumnLimit(1)),
            trueFilter: RowFilters.BlockAllFilter(),
            falseFilter: RowFilters.PassAllFilter());

        var rows = await ReadAll(filter: filter);
        rows.Should().NotContain(r => r.Key.ToStringUtf8() == "cf-active");
        rows.Count.Should().BeGreaterThan(0);
    }

    #endregion

    #region Predicate variations

    [Fact]
    public async Task Condition_predicate_regex()
    {
        var filter = RowFilters.Condition(
            predicateFilter: RowFilters.Chain(
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.ValueRegex("active|pending"),
                RowFilters.CellsPerColumnLimit(1)),
            trueFilter: RowFilters.StripValueTransformer(),
            falseFilter: RowFilters.PassAllFilter());

        var activeRow = await ReadAll(rows: RowSet.FromRowKeys("cf-active"), filter: filter);
        activeRow[0].Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);

        var completedRow = await ReadAll(rows: RowSet.FromRowKeys("cf-completed"), filter: filter);
        completedRow[0].Families[0].Columns[0].Cells[0].Value.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Condition_predicate_family_filter()
    {
        // Predicate: does cf2 family have data?
        var filter = RowFilters.Condition(
            predicateFilter: RowFilters.FamilyNameRegex("cf2"),
            trueFilter: RowFilters.PassAllFilter(),
            falseFilter: RowFilters.BlockAllFilter());

        var rows = await ReadAll(filter: filter);
        // Only cf-multi has data in cf2
        rows.Should().ContainSingle().Which.Key.ToStringUtf8().Should().Be("cf-multi");
    }

    [Fact]
    public async Task Condition_predicate_column_exists()
    {
        // Predicate: does "score" column exist?
        var filter = RowFilters.Condition(
            predicateFilter: RowFilters.ColumnQualifierExact("score"),
            trueFilter: RowFilters.PassAllFilter(),
            falseFilter: RowFilters.BlockAllFilter());

        var rows = await ReadAll(filter: filter);
        rows.Should().HaveCount(10); // cf-num-0 through cf-num-9
    }

    #endregion

    #region Nested condition

    [Fact]
    public async Task Nested_condition_in_true_branch()
    {
        // Outer: status column exists?
        // True inner: is status == "active" ? strip values : pass all
        // False: block all
        var filter = RowFilters.Condition(
            predicateFilter: RowFilters.ColumnQualifierExact("status"),
            trueFilter: RowFilters.Condition(
                predicateFilter: RowFilters.Chain(
                    RowFilters.ColumnQualifierExact("status"),
                    RowFilters.ValueExact("active"),
                    RowFilters.CellsPerColumnLimit(1)),
                trueFilter: RowFilters.StripValueTransformer(),
                falseFilter: RowFilters.PassAllFilter()),
            falseFilter: RowFilters.BlockAllFilter());

        var activeRow = await ReadAll(rows: RowSet.FromRowKeys("cf-active"), filter: filter);
        // Active row: stripped values
        activeRow[0].Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);

        var pendingRow = await ReadAll(rows: RowSet.FromRowKeys("cf-pending"), filter: filter);
        // Pending row: values preserved
        pendingRow[0].Families[0].Columns
            .First(c => c.Qualifier.ToStringUtf8() == "status")
            .Cells[0].Value.ToStringUtf8().Should().Be("pending");
    }

    #endregion

    #region Condition with chain/interleave

    [Fact]
    public async Task Condition_in_chain()
    {
        var filter = RowFilters.Chain(
            RowFilters.RowKeyRegex("cf-num-.*"),
            RowFilters.Condition(
                predicateFilter: RowFilters.Chain(
                    RowFilters.ColumnQualifierExact("score"),
                    RowFilters.ValueRegex("[5-9]0"),
                    RowFilters.CellsPerColumnLimit(1)),
                trueFilter: RowFilters.PassAllFilter(),
                falseFilter: RowFilters.BlockAllFilter()));

        var rows = await ReadAll(filter: filter);
        rows.Should().HaveCount(5); // scores 50, 60, 70, 80, 90
    }

    [Fact]
    public async Task Condition_in_interleave()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.Condition(
                predicateFilter: RowFilters.Chain(
                    RowFilters.ColumnQualifierExact("status"),
                    RowFilters.ValueExact("active"),
                    RowFilters.CellsPerColumnLimit(1)),
                trueFilter: RowFilters.ColumnQualifierExact("status"),
                falseFilter: RowFilters.BlockAllFilter()));

        var activeRow = await ReadAll(rows: RowSet.FromRowKeys("cf-active"), filter: filter);
        var cols = activeRow[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().HaveCount(2); // name + status (condition matched)
    }

    #endregion

    #region Edge cases

    [Fact]
    public async Task Condition_on_nonexistent_row()
    {
        var filter = RowFilters.Condition(
            predicateFilter: RowFilters.PassAllFilter(),
            trueFilter: RowFilters.PassAllFilter(),
            falseFilter: RowFilters.PassAllFilter());
        var rows = await ReadAll(rows: RowSet.FromRowKeys("nonexistent"), filter: filter);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Condition_both_branches_block()
    {
        var filter = RowFilters.Condition(
            predicateFilter: RowFilters.PassAllFilter(),
            trueFilter: RowFilters.BlockAllFilter(),
            falseFilter: RowFilters.BlockAllFilter());
        var rows = await ReadAll(filter: filter);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Condition_both_branches_pass()
    {
        var filter = RowFilters.Condition(
            predicateFilter: RowFilters.PassAllFilter(),
            trueFilter: RowFilters.PassAllFilter(),
            falseFilter: RowFilters.PassAllFilter());
        var rows = await ReadAll(filter: filter);
        rows.Count.Should().BeGreaterThan(0);
    }

    #endregion
}

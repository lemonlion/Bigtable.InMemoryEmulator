using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for ConditionFilter edge cases beyond basic true/false.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
///   "Condition: A RowFilter which evaluates one of two possible RowFilters."
///   "If predicate_filter outputs any cells, then true_filter is evaluated on the given row."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ConditionFilterEdgeCaseTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public ConditionFilterEdgeCaseTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync("cond-edge", new[] { CF, "cf2" });
        var tn = _fixture.GetTableName("cond-edge");

        await _fixture.Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell(CF, "status", "active", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "type", "user", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "extra", "data", new BigtableVersion(1000)));

        await _fixture.Client.MutateRowAsync(tn, "r2",
            Mutations.SetCell(CF, "status", "inactive", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "type", "admin", new BigtableVersion(1000)));
    }
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("cond-edge");

    [Fact]
    public async Task Condition_true_branch_pass_all()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.ValueRegex("active")),
            RowFilters.PassAllFilter(),
            RowFilters.BlockAllFilter());

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter))
            rows.Add(row);

        // r1 matches (active) → pass all; r2 doesn't match → block all
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("r1");
    }

    [Fact]
    public async Task Condition_false_branch_selected()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.ValueRegex("deleted")),
            RowFilters.BlockAllFilter(),
            RowFilters.FamilyNameExact(CF));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter))
            rows.Add(row);

        // Neither row has status "deleted" → false → CF only
        rows.Should().HaveCount(2);
        foreach (var row in rows)
            row.Families.Should().ContainSingle().Which.Name.Should().Be(CF);
    }

    [Fact]
    public async Task Condition_predicate_with_family_filter()
    {
        var filter = RowFilters.Condition(
            RowFilters.FamilyNameExact("cf2"), // Only r1 has cf2 data
            RowFilters.ColumnQualifierExact("status"),
            RowFilters.PassAllFilter());

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter))
            rows.Add(row);

        // r1 has cf2 → true → only status column
        // r2 has no cf2 → false → pass all
        var r1 = rows.FirstOrDefault(r => r.Key.ToStringUtf8() == "r1");
        var r2 = rows.FirstOrDefault(r => r.Key.ToStringUtf8() == "r2");
        r1!.Families.SelectMany(f => f.Columns).Should().ContainSingle();
        r2!.Families.SelectMany(f => f.Columns).Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public async Task Nested_condition_filters()
    {
        var inner = RowFilters.Condition(
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("type"),
                RowFilters.ValueRegex("user")),
            RowFilters.FamilyNameExact(CF),
            RowFilters.FamilyNameExact("cf2"));

        var outer = RowFilters.Condition(
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.ValueRegex("active")),
            inner,
            RowFilters.BlockAllFilter());

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, outer))
            rows.Add(row);

        // r1: status=active → outer true → inner: type=user → true → CF only
        // r2: status=inactive → outer false → block all
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("r1");
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF);
    }

    [Fact]
    public async Task Condition_with_strip_value_in_true_branch()
    {
        var filter = RowFilters.Condition(
            RowFilters.PassAllFilter(),
            RowFilters.StripValueTransformer(),
            RowFilters.PassAllFilter());

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter))
            rows.Add(row);

        // All rows match → true → strip values
        foreach (var row in rows)
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    foreach (var cell in col.Cells)
                        cell.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Condition_with_cells_per_column_in_branches()
    {
        // True branch: latest only, False branch: all versions
        await Client.MutateRowAsync(TN, "r1",
            Mutations.SetCell(CF, "status", "active-v2", new BigtableVersion(2000)));

        var filter = RowFilters.Condition(
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueRegex("active.*")),
            RowFilters.Chain(
                RowFilters.FamilyNameExact(CF),
                RowFilters.CellsPerColumnLimit(1)),
            RowFilters.FamilyNameExact(CF));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter))
            rows.Add(row);

        rows.Should().ContainSingle();
    }
}

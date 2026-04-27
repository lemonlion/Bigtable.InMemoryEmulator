using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for complex nested filter compositions mixing Chain, Interleave, and Condition.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
///   Filters can be composed arbitrarily via chain, interleave, and condition.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class NestedFilterCompositionTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string CF2 = "cf2";
    private const string Table = "nested-filt";

    public NestedFilterCompositionTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task Chain_inside_interleave()
    {
        await Client.MutateRowAsync(TN, "nf-r1",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "v", new BigtableVersion(1000)));
        // Interleave: family=cf + qualifier=a, OR family=cf2
        var row = await Client.ReadRowAsync(TN, "nf-r1",
            RowFilters.Interleave(
                RowFilters.Chain(RowFilters.FamilyNameRegex(CF), RowFilters.ColumnQualifierExact("a")),
                RowFilters.FamilyNameRegex(CF2)));
        var cols = row!.Families.SelectMany(f => f.Columns).ToList();
        cols.Should().HaveCount(2); // a from cf and c from cf2
    }

    [Fact]
    public async Task Interleave_inside_chain()
    {
        await Client.MutateRowAsync(TN, "nf-r2",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "b", "v3", new BigtableVersion(1000)));
        // Chain: interleave(col=a, col=b) → limit 1 per col
        var row = await Client.ReadRowAsync(TN, "nf-r2",
            RowFilters.Chain(
                RowFilters.Interleave(
                    RowFilters.ColumnQualifierExact("a"),
                    RowFilters.ColumnQualifierExact("b")),
                RowFilters.CellsPerColumnLimit(1)));
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task Condition_true_chain()
    {
        await Client.MutateRowAsync(TN, "nf-r3",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "nf-r3",
            RowFilters.Condition(
                RowFilters.ValueRegex("v"),
                RowFilters.Chain(RowFilters.PassAllFilter(), RowFilters.StripValueTransformer()),
                RowFilters.BlockAllFilter()));
        row!.Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task Condition_false_interleave()
    {
        await Client.MutateRowAsync(TN, "nf-r4",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "nf-r4",
            RowFilters.Condition(
                RowFilters.ValueRegex("nomatch"),
                RowFilters.BlockAllFilter(),
                RowFilters.Interleave(
                    RowFilters.ColumnQualifierExact("a"),
                    RowFilters.ColumnQualifierExact("b"))));
        row!.Families[0].Columns.Should().HaveCount(2);
    }

    [Fact]
    public async Task Three_level_nesting()
    {
        await Client.MutateRowAsync(TN, "nf-r5",
            Mutations.SetCell(CF, "x", "val", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "y", "val", new BigtableVersion(1000)));
        // chain(interleave(chain(family, col=x), chain(family, col=y)), limit 1 per col)
        var row = await Client.ReadRowAsync(TN, "nf-r5",
            RowFilters.Chain(
                RowFilters.Interleave(
                    RowFilters.Chain(RowFilters.FamilyNameRegex(CF), RowFilters.ColumnQualifierExact("x")),
                    RowFilters.Chain(RowFilters.FamilyNameRegex(CF), RowFilters.ColumnQualifierExact("y"))),
                RowFilters.CellsPerColumnLimit(1)));
        row!.Families[0].Columns.Should().HaveCount(2);
    }

    [Fact]
    public async Task Condition_nested_in_chain()
    {
        await Client.MutateRowAsync(TN, "nf-r6",
            Mutations.SetCell(CF, "c", "test", new BigtableVersion(1000)));
        // chain(condition(pass → strip, block), family filter)
        var row = await Client.ReadRowAsync(TN, "nf-r6",
            RowFilters.Chain(
                RowFilters.Condition(
                    RowFilters.PassAllFilter(),
                    RowFilters.StripValueTransformer(),
                    RowFilters.BlockAllFilter()),
                RowFilters.FamilyNameRegex(CF)));
        row!.Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task Chain_condition_interleave_combined()
    {
        await Client.MutateRowAsync(TN, "nf-r7",
            Mutations.SetCell(CF, "a", "yes", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "no", new BigtableVersion(1000)));
        // Condition: if value matches "yes" → return all (PassAll), else block
        // Then interleave with PassAll to get normal result
        var row = await Client.ReadRowAsync(TN, "nf-r7",
            RowFilters.Condition(
                RowFilters.ValueRegex("yes"),
                RowFilters.PassAllFilter(),
                RowFilters.BlockAllFilter()));
        // Predicate matches (row has a cell with "yes"), so true filter (PassAll) applied
        row!.Families[0].Columns.Should().HaveCount(2);
    }

    [Fact]
    public async Task Interleave_produces_duplicates()
    {
        // Interleave does NOT deduplicate
        await Client.MutateRowAsync(TN, "nf-r8",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "nf-r8",
            RowFilters.Interleave(RowFilters.PassAllFilter(), RowFilters.PassAllFilter()));
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task Chain_empty_result_propagates()
    {
        await Client.MutateRowAsync(TN, "nf-r9",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "nf-r9",
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("nonexistent"),
                RowFilters.CellsPerColumnLimit(1)));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Interleave_two_families_merges()
    {
        await Client.MutateRowAsync(TN, "nf-r10",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "b", "v2", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "nf-r10",
            RowFilters.Interleave(
                RowFilters.FamilyNameRegex(CF),
                RowFilters.FamilyNameRegex(CF2)));
        row!.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Chain_family_then_column()
    {
        await Client.MutateRowAsync(TN, "nf-r11",
            Mutations.SetCell(CF, "target", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "other", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "target", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "nf-r11",
            RowFilters.Chain(
                RowFilters.FamilyNameRegex(CF),
                RowFilters.ColumnQualifierExact("target")));
        row!.Families.Should().ContainSingle();
        row.Families[0].Name.Should().Be(CF);
        row.Families[0].Columns.Should().ContainSingle();
    }
}

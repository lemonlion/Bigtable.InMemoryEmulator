using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for Interleave filter semantics: merging, duplicate handling, ordering.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
///   "Interleave: Applies several RowFilters to the data in parallel and merges the results."
///   "If multiple cells are produced with the same column and timestamp,
///    they will all appear in the output row in an unspecified mutual order."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class InterleaveDeduplicationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public InterleaveDeduplicationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync("interleave-dedup", new[] { CF, "cf2" });
        var tn = _fixture.GetTableName("interleave-dedup");
        // Seed data with multiple columns and versions
        await _fixture.Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell(CF, "c1", "val1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c2", "val2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c1", "val1v2", new BigtableVersion(2000)),
            Mutations.SetCell("cf2", "d1", "other", new BigtableVersion(1000)));
    }
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("interleave-dedup");

    [Fact]
    public async Task Interleave_merges_disjoint_column_results()
    {
        // Branch 1: c1 only, Branch 2: c2 only → merged result has both
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("c1"),
            RowFilters.ColumnQualifierExact("c2"));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter))
            rows.Add(row);

        rows.Should().ContainSingle();
        var cols = rows[0].Families.SelectMany(f => f.Columns)
            .Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("c1");
        cols.Should().Contain("c2");
    }

    [Fact]
    public async Task Interleave_does_not_deduplicate_overlapping_cells()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.RowFilter.Interleave
        //   "If multiple cells are produced with the same column and timestamp,
        //    they will all appear in the output row in an unspecified mutual order."
        // Both branches return c1 → duplicates are kept
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("c1"),
            RowFilters.PassAllFilter());

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter))
            rows.Add(row);

        rows.Should().ContainSingle();
        // c1 has 2 versions; each appears from both branches = 4 total cells
        var c1Cells = rows[0].Families.Where(f => f.Name == CF)
            .SelectMany(f => f.Columns.Where(c => c.Qualifier.ToStringUtf8() == "c1"))
            .SelectMany(c => c.Cells).ToList();
        c1Cells.Should().HaveCount(4); // 2 versions × 2 branches, no dedup
    }

    [Fact]
    public async Task Interleave_preserves_version_ordering_with_duplicates()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.RowFilter.Interleave
        //   "results are pooled, sorted, and combined into a single output row"
        // Branch 1 (limit 1): c1@2000 only
        // Branch 2 (limit 2): c1@2000, c1@1000
        // Merged: c1@2000 (×2), c1@1000 = 3 cells for c1
        var filter = RowFilters.Interleave(
            RowFilters.CellsPerColumnLimit(1),
            RowFilters.CellsPerColumnLimit(2));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter))
            rows.Add(row);

        rows.Should().ContainSingle();
        var c1 = rows[0].Families.Where(f => f.Name == CF)
            .SelectMany(f => f.Columns.Where(c => c.Qualifier.ToStringUtf8() == "c1"))
            .SelectMany(c => c.Cells).ToList();
        c1.Should().HaveCount(3); // 1 from branch 1 + 2 from branch 2
    }

    [Fact]
    public async Task Interleave_merges_different_families()
    {
        var filter = RowFilters.Interleave(
            RowFilters.FamilyNameExact(CF),
            RowFilters.FamilyNameExact("cf2"));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter))
            rows.Add(row);

        rows.Should().ContainSingle();
        var families = rows[0].Families.Select(f => f.Name).ToList();
        families.Should().Contain(CF);
        families.Should().Contain("cf2");
    }

    [Fact]
    public async Task Interleave_three_branches()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("c1"),
            RowFilters.ColumnQualifierExact("c2"),
            RowFilters.ColumnQualifierExact("d1"));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter))
            rows.Add(row);

        rows.Should().ContainSingle();
        var allCols = rows[0].Families.SelectMany(f => f.Columns)
            .Select(c => c.Qualifier.ToStringUtf8()).ToList();
        allCols.Should().HaveCount(3);
    }

    [Fact]
    public async Task Interleave_with_block_all_branch()
    {
        // One branch blocks all, other passes → only pass-through data appears
        var filter = RowFilters.Interleave(
            RowFilters.BlockAllFilter(),
            RowFilters.ColumnQualifierExact("c1"));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter))
            rows.Add(row);

        rows.Should().ContainSingle();
        var allCols = rows[0].Families.SelectMany(f => f.Columns)
            .Select(c => c.Qualifier.ToStringUtf8()).ToList();
        allCols.Should().ContainSingle().Which.Should().Be("c1");
    }

    [Fact]
    public async Task Interleave_with_strip_value()
    {
        // Interleave where one branch strips values
        var filter = RowFilters.Interleave(
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("c1"),
                RowFilters.StripValueTransformer()),
            RowFilters.ColumnQualifierExact("c2"));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter))
            rows.Add(row);

        rows.Should().ContainSingle();
        // c1 cells should have empty values, c2 cells should have values
        var c1 = rows[0].Families.Where(f => f.Name == CF)
            .SelectMany(f => f.Columns.Where(c => c.Qualifier.ToStringUtf8() == "c1"))
            .SelectMany(c => c.Cells).ToList();
        var c2 = rows[0].Families.Where(f => f.Name == CF)
            .SelectMany(f => f.Columns.Where(c => c.Qualifier.ToStringUtf8() == "c2"))
            .SelectMany(c => c.Cells).ToList();

        c1.Should().NotBeEmpty();
        c2.Should().NotBeEmpty();
        c1.All(c => c.Value.IsEmpty).Should().BeTrue();
        c2.All(c => !c.Value.IsEmpty).Should().BeTrue();
    }
}

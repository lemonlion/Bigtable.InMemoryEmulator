using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CrossFilterInteractionTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "cfi-tests";
    private const string CF = "cf";

    public CrossFilterInteractionTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        for (int i = 0; i < 10; i++)
        {
            var rk = $"cfi-{i:D2}";
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, "name", $"name-{i}", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "name", $"name-{i}-v2", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "score", $"{i * 10}", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "tag", i % 2 == 0 ? "even" : "odd", new BigtableVersion(1000)));
        }
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Chain_qualifier_then_version_limit()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.CellsPerColumnLimit(1));
        var row = await Client.ReadRowAsync(TN, "cfi-00", filter);
        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().ContainSingle();
        cells[0].Value.ToStringUtf8().Should().Be("name-0-v2");
    }

    [Fact]
    public async Task Chain_value_regex_then_qualifier()
    {
        var filter = RowFilters.Chain(
            RowFilters.ValueRegex("even"),
            RowFilters.ColumnQualifierExact("tag"));
        var row = await Client.ReadRowAsync(TN, "cfi-00", filter);
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Interleave_two_qualifier_filters()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.ColumnQualifierExact("score"));
        var row = await Client.ReadRowAsync(TN, "cfi-00", filter);
        row.Should().NotBeNull();
        var cols = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("name");
        cols.Should().Contain("score");
    }

    [Fact]
    public async Task Condition_with_value_predicate()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("tag"), RowFilters.ValueExact("even")),
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.ColumnQualifierExact("score"));

        // cfi-00 has tag=even -> should return name
        var row0 = await Client.ReadRowAsync(TN, "cfi-00", filter);
        row0.Should().NotBeNull();
        row0!.Families.SelectMany(f => f.Columns).All(c => c.Qualifier.ToStringUtf8() == "name").Should().BeTrue();

        // cfi-01 has tag=odd -> should return score
        var row1 = await Client.ReadRowAsync(TN, "cfi-01", filter);
        row1.Should().NotBeNull();
        row1!.Families.SelectMany(f => f.Columns).All(c => c.Qualifier.ToStringUtf8() == "score").Should().BeTrue();
    }

    [Fact]
    public async Task Chain_cells_per_row_limit_with_qualifier()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("score"),
            RowFilters.CellsPerRowLimit(1));
        var row = await Client.ReadRowAsync(TN, "cfi-05", filter);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().ContainSingle();
    }

    [Fact]
    public async Task Chain_column_range_then_value_regex()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "name", "name")),
            RowFilters.ValueRegex(".*v2"));
        var row = await Client.ReadRowAsync(TN, "cfi-03", filter);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().EndWith("v2");
    }

    [Fact]
    public async Task Interleave_with_chain()
    {
        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("name"), RowFilters.CellsPerColumnLimit(1)),
            RowFilters.ColumnQualifierExact("tag"));
        var row = await Client.ReadRowAsync(TN, "cfi-02", filter);
        row.Should().NotBeNull();
        var cols = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).OrderBy(c => c).ToList();
        cols.Should().Contain("name");
        cols.Should().Contain("tag");
        // name should only have 1 cell due to chain limit
        row.Families.SelectMany(f => f.Columns).First(c => c.Qualifier.ToStringUtf8() == "name")
            .Cells.Should().ContainSingle();
    }

    [Fact]
    public async Task Chain_strip_value_then_qualifier()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.StripValueTransformer());
        var row = await Client.ReadRowAsync(TN, "cfi-00", filter);
        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(2); // two versions
        cells.All(c => c.Value.IsEmpty).Should().BeTrue();
    }

    [Fact]
    public async Task Multi_row_chain_filter()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("tag"),
            RowFilters.ValueExact("even"));

        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: filter))
            rows.Add(r);

        rows.Should().HaveCount(5); // 0,2,4,6,8
    }

    [Fact]
    public async Task Chain_timestamp_then_qualifier()
    {
        var filter = RowFilters.Chain(
            new RowFilter
            {
                TimestampRangeFilter = new TimestampRange
                {
                    StartTimestampMicros = 1_000_000,
                    EndTimestampMicros = 1_001_000
                }
            },
            RowFilters.ColumnQualifierExact("name"));

        var row = await Client.ReadRowAsync(TN, "cfi-00", filter);
        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().ContainSingle();
        cells[0].Value.ToStringUtf8().Should().Be("name-0");
    }

    [Fact]
    public async Task PassAll_returns_everything()
    {
        var row = await Client.ReadRowAsync(TN, "cfi-00", RowFilters.PassAllFilter());
        row.Should().NotBeNull();
        // 3 columns: name(2 versions), score(1), tag(1) = 4 cells
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(4);
    }

    [Fact]
    public async Task BlockAll_returns_null()
    {
        var row = await Client.ReadRowAsync(TN, "cfi-00", RowFilters.BlockAllFilter());
        row.Should().BeNull();
    }

    [Fact]
    public async Task Chain_three_filters()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.CellsPerColumnLimit(1),
            RowFilters.StripValueTransformer());

        var row = await Client.ReadRowAsync(TN, "cfi-05", filter);
        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().ContainSingle();
        cells[0].Value.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task Interleave_deduplicates_same_cell()
    {
        // Interleave should return the union; if both sides match the same cell, it appears twice per Bigtable spec
        var filter = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("tag"),
            RowFilters.ColumnQualifierExact("tag"));

        var row = await Client.ReadRowAsync(TN, "cfi-00", filter);
        row.Should().NotBeNull();
        // Bigtable interleave returns duplicates
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(2); // same cell duplicated
    }

    [Fact]
    public async Task Condition_without_false_branch()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("tag"), RowFilters.ValueExact("even")),
            RowFilters.ColumnQualifierExact("score"),
            RowFilters.PassAllFilter());

        // cfi-00 has tag=even -> returns score
        var row0 = await Client.ReadRowAsync(TN, "cfi-00", filter);
        row0.Should().NotBeNull();

        // cfi-01 has tag=odd -> false branch returns all
        var row1 = await Client.ReadRowAsync(TN, "cfi-01", filter);
        row1.Should().NotBeNull();
    }

    [Fact]
    public async Task RowKeyRegex_with_column_filter()
    {
        var filter = RowFilters.Chain(
            RowFilters.RowKeyRegex("cfi-0[0-2]"),
            RowFilters.ColumnQualifierExact("tag"));

        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: filter))
            rows.Add(r);

        rows.Should().HaveCount(3);
    }
}

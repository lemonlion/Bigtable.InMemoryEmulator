using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for chain filter ordering and composition semantics.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
///   "chain: A Chain applies a series of filters sequentially to the output of
///    the previous filter."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ChainFilterOrderingTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string CF2 = "cf2";
    private const string Table = "chain-ord";

    public ChainFilterOrderingTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
        var tn = TN;
        await Client.MutateRowAsync(tn, "co-r1",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "a", "3", new BigtableVersion(3000)),
            Mutations.SetCell(CF, "b", "10", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "20", new BigtableVersion(1000)));
        await Client.MutateRowAsync(tn, "co-r2",
            Mutations.SetCell(CF, "a", "x", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "y", new BigtableVersion(2000)));
        await Client.MutateRowAsync(tn, "co-r3",
            Mutations.SetCell(CF, "z", "val", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task Chain_family_then_qualifier()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameRegex(CF),
            RowFilters.ColumnQualifierExact("a"));
        var row = await Client.ReadRowAsync(TN, "co-r1", filter);
        row!.Families.Should().ContainSingle();
        row.Families[0].Columns.Should().ContainSingle();
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("a");
    }

    [Fact]
    public async Task Chain_qualifier_then_version_limit()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("a"),
            RowFilters.CellsPerColumnLimit(1));
        var row = await Client.ReadRowAsync(TN, "co-r1", filter);
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().ContainSingle();
        cells[0].Value.ToStringUtf8().Should().Be("3"); // latest
    }

    [Fact]
    public async Task Chain_version_limit_then_value_regex()
    {
        // First limit to 2 latest, then filter by regex
        var filter = RowFilters.Chain(
            RowFilters.CellsPerColumnLimit(2),
            RowFilters.ValueRegex("3"));
        var row = await Client.ReadRowAsync(TN, "co-r1", filter);
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().ContainSingle();
        cells[0].Value.ToStringUtf8().Should().Be("3");
    }

    [Fact]
    public async Task Chain_three_filters()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameRegex(CF),
            RowFilters.ColumnQualifierExact("a"),
            RowFilters.CellsPerColumnLimit(1));
        var row = await Client.ReadRowAsync(TN, "co-r1", filter);
        row!.Families.Should().ContainSingle();
        var cells = row.Families[0].Columns[0].Cells;
        cells.Should().ContainSingle();
    }

    [Fact]
    public async Task Chain_with_pass_all_is_transparent()
    {
        var filter = RowFilters.Chain(
            RowFilters.PassAllFilter(),
            RowFilters.CellsPerColumnLimit(1));
        var row = await Client.ReadRowAsync(TN, "co-r1", filter);
        var totalCells = row!.Families.SelectMany(f => f.Columns).Sum(c => c.Cells.Count);
        // 3 columns (a, b in cf, c in cf2) each limited to 1 = 3 cells
        totalCells.Should().Be(3);
    }

    [Fact]
    public async Task Chain_with_block_all_returns_nothing()
    {
        var filter = RowFilters.Chain(
            RowFilters.CellsPerColumnLimit(1),
            RowFilters.BlockAllFilter());
        var row = await Client.ReadRowAsync(TN, "co-r1", filter);
        row.Should().BeNull();
    }

    [Fact]
    public async Task Chain_qualifier_regex_then_family()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("c"),
            RowFilters.FamilyNameRegex(CF2));
        var row = await Client.ReadRowAsync(TN, "co-r1", filter);
        row!.Families.Should().ContainSingle();
        row.Families[0].Name.Should().Be(CF2);
    }

    [Fact]
    public async Task Chain_strip_value_preserves_structure()
    {
        var filter = RowFilters.Chain(
            RowFilters.CellsPerColumnLimit(1),
            RowFilters.StripValueTransformer());
        var row = await Client.ReadRowAsync(TN, "co-r1", filter);
        foreach (var fam in row!.Families)
            foreach (var col in fam.Columns)
                foreach (var cell in col.Cells)
                    cell.Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task Chain_across_multiple_rows()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("a"),
            RowFilters.CellsPerColumnLimit(1));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, filter: filter))
            rows.Add(row);
        // co-r1 has col a, co-r2 has col a, co-r3 does not
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Chain_timestamp_range_then_limit()
    {
        var filter = RowFilters.Chain(
            new RowFilter { TimestampRangeFilter = new TimestampRange { StartTimestampMicros = 1_000_000, EndTimestampMicros = 3_000_000 } },
            RowFilters.CellsPerColumnLimit(1));
        var row = await Client.ReadRowAsync(TN, "co-r1", filter);
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        // Timestamp range [1000ms, 3000ms) = versions at 1000ms and 2000ms. Then limit 1 per column.
        foreach (var fam in row.Families)
            foreach (var col in fam.Columns)
                col.Cells.Should().HaveCountLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task Chain_row_key_regex_then_qualifier()
    {
        var filter = RowFilters.Chain(
            RowFilters.RowKeyRegex("co-r[12]"),
            RowFilters.ColumnQualifierExact("a"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, filter: filter))
            rows.Add(row);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Chain_single_filter_behaves_like_no_chain()
    {
        var withChain = await Client.ReadRowAsync(TN, "co-r1",
            RowFilters.Chain(RowFilters.CellsPerColumnLimit(1)));
        var withoutChain = await Client.ReadRowAsync(TN, "co-r1",
            RowFilters.CellsPerColumnLimit(1));
        var chainCells = withChain!.Families.SelectMany(f => f.Columns)
            .SelectMany(c => c.Cells).Select(c => c.Value.ToStringUtf8()).ToList();
        var directCells = withoutChain!.Families.SelectMany(f => f.Columns)
            .SelectMany(c => c.Cells).Select(c => c.Value.ToStringUtf8()).ToList();
        chainCells.Should().BeEquivalentTo(directCells);
    }
}

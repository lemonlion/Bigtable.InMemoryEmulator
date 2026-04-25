using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for ReadRow (single row read) edge cases.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowSingleTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string CF2 = "cf2";
    private const string Table = "readrow-single";

    public ReadRowSingleTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
        var tn = TN;
        await Client.MutateRowAsync(tn, "rrs-r1",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "3", new BigtableVersion(1000)));
        await Client.MutateRowAsync(tn, "rrs-r2",
            Mutations.SetCell(CF, "x", "val", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task ReadRow_returns_row()
    {
        var row = await Client.ReadRowAsync(TN, "rrs-r1");
        row.Should().NotBeNull();
        row!.Key.ToStringUtf8().Should().Be("rrs-r1");
    }

    [Fact]
    public async Task ReadRow_nonexistent_returns_null()
    {
        var row = await Client.ReadRowAsync(TN, "rrs-nonexist");
        row.Should().BeNull();
    }

    [Fact]
    public async Task ReadRow_with_filter_matching()
    {
        var row = await Client.ReadRowAsync(TN, "rrs-r1",
            RowFilters.ColumnQualifierExact("a"));
        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns).ToList();
        cells.Should().ContainSingle();
        cells[0].Qualifier.ToStringUtf8().Should().Be("a");
    }

    [Fact]
    public async Task ReadRow_with_filter_not_matching()
    {
        var row = await Client.ReadRowAsync(TN, "rrs-r1",
            RowFilters.ColumnQualifierExact("nonexist"));
        row.Should().BeNull();
    }

    [Fact]
    public async Task ReadRow_with_block_all_filter()
    {
        var row = await Client.ReadRowAsync(TN, "rrs-r1", RowFilters.BlockAllFilter());
        row.Should().BeNull();
    }

    [Fact]
    public async Task ReadRow_with_pass_all_filter()
    {
        var row = await Client.ReadRowAsync(TN, "rrs-r1", RowFilters.PassAllFilter());
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task ReadRow_with_family_filter()
    {
        var row = await Client.ReadRowAsync(TN, "rrs-r1",
            RowFilters.FamilyNameRegex(CF));
        row.Should().NotBeNull();
        row!.Families.Should().ContainSingle();
        row.Families[0].Name.Should().Be(CF);
    }

    [Fact]
    public async Task ReadRow_with_strip_value()
    {
        var row = await Client.ReadRowAsync(TN, "rrs-r1",
            RowFilters.StripValueTransformer());
        row.Should().NotBeNull();
        foreach (var fam in row!.Families)
            foreach (var col in fam.Columns)
                foreach (var cell in col.Cells)
                    cell.Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task ReadRow_with_cells_per_row_limit()
    {
        var row = await Client.ReadRowAsync(TN, "rrs-r1",
            RowFilters.CellsPerRowLimit(1));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().ContainSingle();
    }

    [Fact]
    public async Task ReadRow_with_cells_per_row_offset()
    {
        var row = await Client.ReadRowAsync(TN, "rrs-r1",
            RowFilters.CellsPerRowOffset(1));
        row.Should().NotBeNull();
        var totalCells = row!.Families.SelectMany(f => f.Columns).Sum(c => c.Cells.Count);
        totalCells.Should().Be(2); // 3 total - 1 offset = 2
    }

    [Fact]
    public async Task ReadRow_chain_filter()
    {
        var row = await Client.ReadRowAsync(TN, "rrs-r1",
            RowFilters.Chain(
                RowFilters.FamilyNameRegex(CF),
                RowFilters.CellsPerColumnLimit(1)));
        row.Should().NotBeNull();
        row!.Families.Should().ContainSingle();
    }

    [Fact]
    public async Task ReadRow_condition_filter()
    {
        var row = await Client.ReadRowAsync(TN, "rrs-r1",
            RowFilters.Condition(
                RowFilters.ValueRegex("1"),
                trueFilter: RowFilters.PassAllFilter(),
                falseFilter: RowFilters.BlockAllFilter()));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task ReadRow_after_delete()
    {
        await Client.MutateRowAsync(TN, "rrs-del",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row1 = await Client.ReadRowAsync(TN, "rrs-del");
        row1.Should().NotBeNull();
        await Client.MutateRowAsync(TN, "rrs-del", Mutations.DeleteFromRow());
        var row2 = await Client.ReadRowAsync(TN, "rrs-del");
        row2.Should().BeNull();
    }

    [Fact]
    public async Task ReadRow_multiple_families()
    {
        var row = await Client.ReadRowAsync(TN, "rrs-r1");
        row!.Families.Should().HaveCount(2);
        row.Families.Select(f => f.Name).Should().Contain(CF).And.Contain(CF2);
    }

    [Fact]
    public async Task ReadRow_preserves_column_order()
    {
        var row = await Client.ReadRowAsync(TN, "rrs-r1",
            RowFilters.FamilyNameRegex(CF));
        var quals = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().BeInAscendingOrder();
    }
}

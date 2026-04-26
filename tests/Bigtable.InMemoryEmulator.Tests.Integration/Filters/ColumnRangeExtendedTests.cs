using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ColumnRangeExtendedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "cre-tests";
    private const string CF = "cf";

    public ColumnRangeExtendedTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        var rk = "cre-seed";
        for (char c = 'a'; c <= 'z'; c++)
            await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, c.ToString(), $"val-{c}"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task ColumnRange_closed_includes_endpoints()
    {
        var row = await Client.ReadRowAsync(TN, "cre-seed", RowFilters.ColumnRange(ColumnRange.Closed(CF, "d", "g")));
        row.Should().NotBeNull();
        var cols = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().BeEquivalentTo(new[] { "d", "e", "f", "g" });
    }

    [Fact]
    public async Task ColumnRange_closedopen_excludes_end()
    {
        var row = await Client.ReadRowAsync(TN, "cre-seed", RowFilters.ColumnRange(ColumnRange.ClosedOpen(CF, "d", "g")));
        row.Should().NotBeNull();
        var cols = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().BeEquivalentTo(new[] { "d", "e", "f" });
    }

    [Fact]
    public async Task ColumnRange_open_excludes_both()
    {
        var row = await Client.ReadRowAsync(TN, "cre-seed", RowFilters.ColumnRange(ColumnRange.Open(CF, "d", "g")));
        row.Should().NotBeNull();
        var cols = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().BeEquivalentTo(new[] { "e", "f" });
    }

    [Fact]
    public async Task ColumnRange_openclosed_excludes_start()
    {
        var row = await Client.ReadRowAsync(TN, "cre-seed", RowFilters.ColumnRange(ColumnRange.OpenClosed(CF, "d", "g")));
        row.Should().NotBeNull();
        var cols = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().BeEquivalentTo(new[] { "e", "f", "g" });
    }

    [Fact]
    public async Task ColumnRange_single_column()
    {
        var row = await Client.ReadRowAsync(TN, "cre-seed", RowFilters.ColumnRange(ColumnRange.Closed(CF, "m", "m")));
        row.Should().NotBeNull();
        var cols = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().ContainSingle().Which.Should().Be("m");
    }

    [Fact]
    public async Task ColumnRange_no_match()
    {
        var row = await Client.ReadRowAsync(TN, "cre-seed", RowFilters.ColumnRange(ColumnRange.Closed(CF, "0", "9")));
        row.Should().BeNull();
    }

    [Fact]
    public async Task ColumnRange_all_columns()
    {
        var row = await Client.ReadRowAsync(TN, "cre-seed", RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "z")));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(26);
    }

    [Fact]
    public async Task ColumnRange_chained_with_version_limit()
    {
        var rk = "cre-chain-ver";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "old", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "new", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "b", "old", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "new", new BigtableVersion(2000)));

        var filter = RowFilters.Chain(
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "a")),
            RowFilters.CellsPerColumnLimit(1));
        var row = await Client.ReadRowAsync(TN, rk, filter);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Single()
            .Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task ColumnRange_interleaved()
    {
        var filter = RowFilters.Interleave(
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "c")),
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "x", "z")));
        var row = await Client.ReadRowAsync(TN, "cre-seed", filter);
        row.Should().NotBeNull();
        var cols = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).OrderBy(c => c).ToList();
        cols.Should().BeEquivalentTo(new[] { "a", "b", "c", "x", "y", "z" });
    }

    [Fact]
    public async Task ColumnRange_on_nonexistent_row()
    {
        var row = await Client.ReadRowAsync(TN, "cre-nonexist", RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "z")));
        row.Should().BeNull();
    }

    [Fact]
    public async Task ColumnRange_with_label()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "a")),
            new RowFilter { ApplyLabelTransformer = "first-col" });
        var row = await Client.ReadRowAsync(TN, "cre-seed", filter);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Single()
            .Labels.Should().Contain("first-col");
    }

    [Fact]
    public async Task ColumnRange_columns_returned_in_sorted_order()
    {
        var row = await Client.ReadRowAsync(TN, "cre-seed", RowFilters.ColumnRange(ColumnRange.Closed(CF, "f", "k")));
        row.Should().NotBeNull();
        var cols = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task ColumnQualifierExact_matches_single_column()
    {
        var row = await Client.ReadRowAsync(TN, "cre-seed", RowFilters.ColumnQualifierExact("q"));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().ContainSingle();
    }

    [Fact]
    public async Task ColumnQualifierRegex_matches_pattern()
    {
        // Match columns a through c: "[a-c]"
        var row = await Client.ReadRowAsync(TN, "cre-seed", RowFilters.ColumnQualifierRegex("[a-c]"));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(3);
    }
}

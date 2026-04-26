using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CellsPerRowLimitTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "cprl-tests";
    private const string CF = "cf";

    public CellsPerRowLimitTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null)
    {
        var list = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rows, filter: filter))
            list.Add(r);
        return list;
    }

    [Fact]
    public async Task CellsPerRowLimit_1_returns_single_cell()
    {
        var rk = "cprl-1-single";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a", "v1"));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "b", "v2"));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "c", "v3"));

        var row = await Client.ReadRowAsync(TN, rk, RowFilters.CellsPerRowLimit(1));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().ContainSingle();
    }

    [Fact]
    public async Task CellsPerRowLimit_2_returns_two_cells()
    {
        var rk = "cprl-2-two";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a", "v1"));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "b", "v2"));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "c", "v3"));

        var row = await Client.ReadRowAsync(TN, rk, RowFilters.CellsPerRowLimit(2));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(2);
    }

    [Fact]
    public async Task CellsPerRowLimit_larger_than_total_returns_all()
    {
        var rk = "cprl-larger";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a", "v1"));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "b", "v2"));

        var row = await Client.ReadRowAsync(TN, rk, RowFilters.CellsPerRowLimit(100));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(2);
    }

    [Fact]
    public async Task CellsPerRowLimit_counts_across_columns()
    {
        var rk = "cprl-across";
        for (int i = 0; i < 5; i++)
            await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, $"col{i:D2}", $"val{i}"));

        var row = await Client.ReadRowAsync(TN, rk, RowFilters.CellsPerRowLimit(3));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(3);
    }

    [Fact]
    public async Task CellsPerRowLimit_counts_versions_as_cells()
    {
        var rk = "cprl-versions";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "v2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "v3", new BigtableVersion(3000)));

        var row = await Client.ReadRowAsync(TN, rk, RowFilters.CellsPerRowLimit(2));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(2);
    }

    [Fact]
    public async Task CellsPerColumnLimit_1_returns_latest_version()
    {
        var rk = "cprl-col-limit-1";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "new", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, rk, RowFilters.CellsPerColumnLimit(1));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Single()
            .Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task CellsPerColumnLimit_2_returns_two_latest()
    {
        var rk = "cprl-col-limit-2";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "v2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "v3", new BigtableVersion(3000)));

        var row = await Client.ReadRowAsync(TN, rk, RowFilters.CellsPerColumnLimit(2));
        row.Should().NotBeNull();
        var values = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Select(c => c.Value.ToStringUtf8()).ToList();
        values.Should().HaveCount(2);
        values.Should().Contain("v3");
        values.Should().Contain("v2");
    }

    [Fact]
    public async Task CellsPerColumnLimit_applies_per_column_independently()
    {
        var rk = "cprl-col-indep";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a", "a1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a", "a2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "b", "b1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "b", "b2", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, rk, RowFilters.CellsPerColumnLimit(1));
        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(2);
        var values = cells.Select(c => c.Value.ToStringUtf8()).ToList();
        values.Should().Contain("a2");
        values.Should().Contain("b2");
    }

    [Fact]
    public async Task CellsPerRowLimit_on_nonexistent_row_returns_null()
    {
        var row = await Client.ReadRowAsync(TN, "cprl-nonexist", RowFilters.CellsPerRowLimit(10));
        row.Should().BeNull();
    }

    [Fact]
    public async Task CellsPerColumnLimit_larger_than_versions_returns_all()
    {
        var rk = "cprl-col-all";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "only", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk, RowFilters.CellsPerColumnLimit(100));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().ContainSingle();
    }

    [Fact]
    public async Task CellsPerRowLimit_after_value_filter()
    {
        var rk = "cprl-chain-vf";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a", "yes"));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "b", "no"));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "c", "yes"));

        var filter = RowFilters.Chain(RowFilters.ValueExact("yes"), RowFilters.CellsPerRowLimit(1));
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().ContainSingle();
    }

    [Fact]
    public async Task CellsPerRowLimit_across_multiple_rows()
    {
        for (int r = 0; r < 3; r++)
        {
            var rk = $"cprl-multi-r{r}";
            await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a", "1"));
            await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "b", "2"));
            await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "c", "3"));
        }

        var filter = RowFilters.CellsPerRowLimit(2);
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("cprl-multi-r", "cprl-multi-s")),
            filter);

        rows.Should().HaveCount(3);
        foreach (var row in rows)
            row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(2);
    }

    [Fact]
    public async Task CellsPerRowLimit_and_CellsPerColumnLimit_chained()
    {
        var rk = "cprl-both";
        for (int c = 0; c < 3; c++)
            for (int v = 1; v <= 3; v++)
                await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, $"c{c}", $"v{v}", new BigtableVersion(v * 1000)));

        var filter = RowFilters.Chain(RowFilters.CellsPerColumnLimit(1), RowFilters.CellsPerRowLimit(2));
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(2);
    }

    [Fact]
    public async Task CellsPerRowLimit_1_returns_first_cell_in_sort_order()
    {
        var rk = "cprl-order-1";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "z_last", "val_z"));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a_first", "val_a"));

        var row = await Client.ReadRowAsync(TN, rk, RowFilters.CellsPerRowLimit(1));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Single()
            .Value.ToStringUtf8().Should().Be("val_a");
    }

    [Fact]
    public async Task CellsPerColumnLimit_with_many_versions()
    {
        var rk = "cprl-many-ver";
        for (int i = 1; i <= 10; i++)
            await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", $"v{i}", new BigtableVersion(i * 1000)));

        var row = await Client.ReadRowAsync(TN, rk, RowFilters.CellsPerColumnLimit(3));
        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(3);
        cells.Select(c => c.Value.ToStringUtf8()).Should().Contain("v10");
    }

    [Fact]
    public async Task CellsPerRowLimit_with_multi_version_columns()
    {
        var rk = "cprl-mv";
        for (int c = 0; c < 3; c++)
        {
            await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, $"c{c}", "old", new BigtableVersion(1000)));
            await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, $"c{c}", "new", new BigtableVersion(2000)));
        }

        var row = await Client.ReadRowAsync(TN, rk, RowFilters.CellsPerRowLimit(4));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(4);
    }
}

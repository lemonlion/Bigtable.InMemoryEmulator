using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CellsPerColumnLimitBehaviorTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "cpcl-beh";
    private const string CF = "cf";

    public CellsPerColumnLimitBehaviorTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        // Write 5 versions to col
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "r1", Mutations.SetCell(CF, "col", $"v{i}", new BigtableVersion(i * 1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Limit_1_returns_latest()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.CellsPerColumnLimit(1));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("v5");
    }

    [Fact]
    public async Task Limit_3_returns_3_latest()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.CellsPerColumnLimit(3));
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(3);
        cells[0].Value.ToStringUtf8().Should().Be("v5");
        cells[2].Value.ToStringUtf8().Should().Be("v3");
    }

    [Fact]
    public async Task Limit_larger_than_versions_returns_all()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.CellsPerColumnLimit(10));
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(5);
    }

    [Fact]
    public async Task Limit_per_column_not_per_row()
    {
        await Client.MutateRowAsync(TN, "r2",
            Mutations.SetCell(CF, "a", "a1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "a2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "b", "b1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "b2", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "r2", RowFilters.CellsPerColumnLimit(1));
        var cols = row!.Families.SelectMany(f => f.Columns).ToList();
        cols.Should().HaveCount(2);
        foreach (var col in cols)
            col.Cells.Should().ContainSingle();
    }

    [Fact]
    public async Task Limit_with_chain()
    {
        var chain = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("col"),
            RowFilters.CellsPerColumnLimit(2));
        var row = await Client.ReadRowAsync(TN, "r1", chain);
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task Limit_on_single_version_column()
    {
        await Client.MutateRowAsync(TN, "r3", Mutations.SetCell(CF, "col", "only", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "r3", RowFilters.CellsPerColumnLimit(1));
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("only");
    }

    [Fact]
    public async Task Limit_on_empty_row()
    {
        var row = await Client.ReadRowAsync(TN, "nomatch", RowFilters.CellsPerColumnLimit(1));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Cells_returned_newest_first()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.CellsPerColumnLimit(5));
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        var timestamps = cells.Select(c => c.TimestampMicros).ToList();
        timestamps.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Limit_2_across_rows()
    {
        for (int i = 1; i <= 3; i++)
            await Client.MutateRowAsync(TN, "r4", Mutations.SetCell(CF, "col", $"v{i}", new BigtableVersion(i * 1000)));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN,
            rows: RowSet.FromRowKeys("r1", "r4"),
            filter: RowFilters.CellsPerColumnLimit(2)))
            rows.Add(r);
        rows.Should().HaveCount(2);
        foreach (var row in rows)
            row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(2);
    }

    [Fact]
    public async Task Limit_combined_with_value_regex()
    {
        var chain = RowFilters.Chain(
            RowFilters.CellsPerColumnLimit(3),
            RowFilters.ValueRegex("v[45]"));
        var row = await Client.ReadRowAsync(TN, "r1", chain);
        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(2); // v5 and v4 from top 3
    }
}

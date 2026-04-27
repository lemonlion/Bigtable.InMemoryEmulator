using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for CellsPerColumnLimit filter interactions — ensures only the N most recent versions
/// are returned per column qualifier.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
///   "cells_per_column_limit_filter: Matches only the most recent N cells within each column."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CellsPerColumnLimitAdvancedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "cpcl-adv";

    public CellsPerColumnLimitAdvancedTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        var tn = TN;
        // Row with 5 versions of same column
        await Client.MutateRowAsync(tn, "cl-r1",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)),
            Mutations.SetCell(CF, "c", "v4", new BigtableVersion(4000)),
            Mutations.SetCell(CF, "c", "v5", new BigtableVersion(5000)));
        // Row with multiple columns, each with 3 versions
        await Client.MutateRowAsync(tn, "cl-r2",
            Mutations.SetCell(CF, "a", "a1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "a2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "a", "a3", new BigtableVersion(3000)),
            Mutations.SetCell(CF, "b", "b1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "b2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "b", "b3", new BigtableVersion(3000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task Limit_1_returns_latest_version()
    {
        var row = await Client.ReadRowAsync(TN, "cl-r1", RowFilters.CellsPerColumnLimit(1));
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v5");
    }

    [Fact]
    public async Task Limit_2_returns_two_latest()
    {
        var row = await Client.ReadRowAsync(TN, "cl-r1", RowFilters.CellsPerColumnLimit(2));
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2);
        cells.Select(c => c.Value.ToStringUtf8()).Should().BeEquivalentTo(new[] { "v5", "v4" });
    }

    [Fact]
    public async Task Limit_equal_to_count()
    {
        var row = await Client.ReadRowAsync(TN, "cl-r1", RowFilters.CellsPerColumnLimit(5));
        row!.Families[0].Columns[0].Cells.Should().HaveCount(5);
    }

    [Fact]
    public async Task Limit_exceeding_count()
    {
        var row = await Client.ReadRowAsync(TN, "cl-r1", RowFilters.CellsPerColumnLimit(100));
        row!.Families[0].Columns[0].Cells.Should().HaveCount(5);
    }

    [Fact]
    public async Task Limit_applied_per_column_independently()
    {
        var row = await Client.ReadRowAsync(TN, "cl-r2", RowFilters.CellsPerColumnLimit(1));
        var cols = row!.Families[0].Columns.ToList();
        cols.Should().HaveCount(2);
        foreach (var col in cols)
            col.Cells.Should().ContainSingle();
    }

    [Fact]
    public async Task Limit_2_per_column_across_columns()
    {
        var row = await Client.ReadRowAsync(TN, "cl-r2", RowFilters.CellsPerColumnLimit(2));
        foreach (var col in row!.Families[0].Columns)
            col.Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task Limit_with_chain_qualifier_filter()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("a"),
            RowFilters.CellsPerColumnLimit(1));
        var row = await Client.ReadRowAsync(TN, "cl-r2", filter);
        row!.Families.SelectMany(f => f.Columns).Should().ContainSingle();
        row.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("a3");
    }

    [Fact]
    public async Task Limit_with_value_regex()
    {
        var filter = RowFilters.Chain(
            RowFilters.CellsPerColumnLimit(3),
            RowFilters.ValueRegex("v[345]"));
        var row = await Client.ReadRowAsync(TN, "cl-r1", filter);
        var vals = row!.Families[0].Columns[0].Cells.Select(c => c.Value.ToStringUtf8()).ToList();
        vals.Should().BeEquivalentTo(new[] { "v5", "v4", "v3" });
    }

    [Fact]
    public async Task Limit_preserves_timestamp_ordering()
    {
        var row = await Client.ReadRowAsync(TN, "cl-r1", RowFilters.CellsPerColumnLimit(3));
        var timestamps = row!.Families[0].Columns[0].Cells.Select(c => c.TimestampMicros).ToList();
        timestamps.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Limit_on_empty_row_returns_nothing()
    {
        var row = await Client.ReadRowAsync(TN, "cl-nonexist", RowFilters.CellsPerColumnLimit(1));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Limit_with_strip_value()
    {
        var filter = RowFilters.Chain(
            RowFilters.CellsPerColumnLimit(1),
            RowFilters.StripValueTransformer());
        var row = await Client.ReadRowAsync(TN, "cl-r1", filter);
        row!.Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task Limit_interleave_different_limits()
    {
        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("a"), RowFilters.CellsPerColumnLimit(1)),
            RowFilters.Chain(RowFilters.ColumnQualifierExact("b"), RowFilters.CellsPerColumnLimit(2)));
        var row = await Client.ReadRowAsync(TN, "cl-r2", filter);
        var colA = row!.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "a");
        var colB = row.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "b");
        colA.Cells.Should().ContainSingle();
        colB.Cells.Should().HaveCount(2);
    }
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for ColumnRange filter boundary types.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#columnrange
///   "Specifies a contiguous range of columns within a single column family."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ColumnRangeFilterTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "col-range";

    public ColumnRangeFilterTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        var tn = TN;
        // Row with many columns
        await Client.MutateRowAsync(tn, "cr-r1",
            Mutations.SetCell(CF, "col-a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col-b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col-c", "3", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col-d", "4", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col-e", "5", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<string>> ReadQualifiers(string rowKey, RowFilter filter)
    {
        var quals = new List<string>();
        var row = await Client.ReadRowAsync(TN, rowKey, filter);
        if (row != null)
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    quals.Add(col.Qualifier.ToStringUtf8());
        return quals;
    }

    [Fact]
    public async Task Closed_range()
    {
        var quals = await ReadQualifiers("cr-r1",
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "col-b", "col-d")));
        quals.Should().BeEquivalentTo(new[] { "col-b", "col-c", "col-d" });
    }

    [Fact]
    public async Task ClosedOpen_range()
    {
        var quals = await ReadQualifiers("cr-r1",
            RowFilters.ColumnRange(ColumnRange.ClosedOpen(CF, "col-b", "col-d")));
        quals.Should().BeEquivalentTo(new[] { "col-b", "col-c" });
    }

    [Fact]
    public async Task OpenClosed_range()
    {
        var quals = await ReadQualifiers("cr-r1",
            RowFilters.ColumnRange(ColumnRange.OpenClosed(CF, "col-b", "col-d")));
        quals.Should().BeEquivalentTo(new[] { "col-c", "col-d" });
    }

    [Fact]
    public async Task Open_range()
    {
        var quals = await ReadQualifiers("cr-r1",
            RowFilters.ColumnRange(ColumnRange.Open(CF, "col-b", "col-d")));
        quals.Should().ContainSingle().Which.Should().Be("col-c");
    }

    [Fact]
    public async Task Single_column_closed()
    {
        var quals = await ReadQualifiers("cr-r1",
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "col-c", "col-c")));
        quals.Should().ContainSingle().Which.Should().Be("col-c");
    }

    [Fact]
    public async Task No_match()
    {
        var quals = await ReadQualifiers("cr-r1",
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "col-x", "col-z")));
        quals.Should().BeEmpty();
    }

    [Fact]
    public async Task All_columns()
    {
        var quals = await ReadQualifiers("cr-r1",
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "col-a", "col-e")));
        quals.Should().HaveCount(5);
    }

    [Fact]
    public async Task Start_at_beginning()
    {
        var quals = await ReadQualifiers("cr-r1",
            RowFilters.ColumnRange(ColumnRange.ClosedOpen(CF, "", "col-c")));
        quals.Should().BeEquivalentTo(new[] { "col-a", "col-b" });
    }

    [Fact]
    public async Task ColumnRange_with_chain()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "col-b", "col-d")),
            RowFilters.CellsPerColumnLimit(1));
        var quals = await ReadQualifiers("cr-r1", filter);
        quals.Should().HaveCount(3);
    }

    [Fact]
    public async Task ColumnRange_across_rows()
    {
        // Add second row
        await Client.MutateRowAsync(TN, "cr-r2",
            Mutations.SetCell(CF, "col-a", "x", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col-c", "y", new BigtableVersion(1000)));
        var filter = RowFilters.ColumnRange(ColumnRange.Closed(CF, "col-b", "col-c"));
        var count = 0;
        await foreach (var row in Client.ReadRows(TN, filter: filter))
            count += row.Families.SelectMany(f => f.Columns).Count();
        count.Should().BeGreaterThanOrEqualTo(2);
    }
}

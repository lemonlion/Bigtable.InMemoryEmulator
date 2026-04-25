using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for ValueRange filter boundaries and edge cases.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#valuerange
///   "Specifies a contiguous range of raw byte values."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ValueRangeFilterTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "val-range";

    public ValueRangeFilterTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        var tn = TN;
        // Rows with different values for range testing
        await Client.MutateRowAsync(tn, "vr-r1",
            Mutations.SetCell(CF, "c", "alpha", new BigtableVersion(1000)));
        await Client.MutateRowAsync(tn, "vr-r2",
            Mutations.SetCell(CF, "c", "beta", new BigtableVersion(1000)));
        await Client.MutateRowAsync(tn, "vr-r3",
            Mutations.SetCell(CF, "c", "gamma", new BigtableVersion(1000)));
        await Client.MutateRowAsync(tn, "vr-r4",
            Mutations.SetCell(CF, "c", "delta", new BigtableVersion(1000)));
        // Row with numeric-like values
        await Client.MutateRowAsync(tn, "vr-num",
            Mutations.SetCell(CF, "c", "100", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "200", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "300", new BigtableVersion(3000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task Closed_range()
    {
        var filter = RowFilters.ValueRange(ValueRange.Closed("beta", "gamma"));
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(TN, filter: filter))
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    foreach (var cell in col.Cells)
                        vals.Add(cell.Value.ToStringUtf8());
        vals.Should().Contain("beta").And.Contain("gamma").And.Contain("delta");
    }

    [Fact]
    public async Task ClosedOpen_range()
    {
        var filter = RowFilters.ValueRange(ValueRange.ClosedOpen("beta", "gamma"));
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(TN, filter: filter))
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    foreach (var cell in col.Cells)
                        vals.Add(cell.Value.ToStringUtf8());
        vals.Should().Contain("beta").And.Contain("delta");
        vals.Should().NotContain("gamma");
    }

    [Fact]
    public async Task OpenClosed_range()
    {
        var filter = RowFilters.ValueRange(ValueRange.OpenClosed("alpha", "delta"));
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(TN, filter: filter))
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    foreach (var cell in col.Cells)
                        vals.Add(cell.Value.ToStringUtf8());
        vals.Should().NotContain("alpha");
        vals.Should().Contain("beta").And.Contain("delta");
    }

    [Fact]
    public async Task Open_range()
    {
        var filter = RowFilters.ValueRange(ValueRange.Open("alpha", "gamma"));
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(TN, filter: filter))
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    foreach (var cell in col.Cells)
                        vals.Add(cell.Value.ToStringUtf8());
        vals.Should().NotContain("alpha").And.NotContain("gamma");
        vals.Should().Contain("beta").And.Contain("delta");
    }

    [Fact]
    public async Task Range_no_match()
    {
        var filter = RowFilters.ValueRange(ValueRange.Closed("zzz", "zzzz"));
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(TN, filter: filter))
            vals.Add(row.Key.ToStringUtf8());
        vals.Should().BeEmpty();
    }

    [Fact]
    public async Task Range_across_versions()
    {
        // The row vr-num has values "100", "200", "300" at different timestamps
        var filter = RowFilters.ValueRange(ValueRange.Closed("150", "250"));
        var row = await Client.ReadRowAsync(TN, "vr-num", filter);
        var vals = row!.Families[0].Columns[0].Cells.Select(c => c.Value.ToStringUtf8()).ToList();
        vals.Should().ContainSingle().Which.Should().Be("200");
    }

    [Fact]
    public async Task Range_with_chain()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("c"),
            RowFilters.ValueRange(ValueRange.Closed("a", "c")));
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(TN, filter: filter))
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    foreach (var cell in col.Cells)
                        vals.Add(cell.Value.ToStringUtf8());
        vals.Should().Contain("alpha").And.Contain("beta");
        vals.Should().NotContain("gamma").And.NotContain("delta");
    }

    [Fact]
    public async Task Exact_match_closed()
    {
        var filter = RowFilters.ValueRange(ValueRange.Closed("beta", "beta"));
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(TN, filter: filter))
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    foreach (var cell in col.Cells)
                        vals.Add(cell.Value.ToStringUtf8());
        vals.Should().ContainSingle().Which.Should().Be("beta");
    }
}

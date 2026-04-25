using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for RowSampleFilter (probabilistic row sampling).
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
///   "row_sample_filter: Matches all cells from a row with probability p."
///   "p must be > 0 and <= 1.0"
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.GcpOnly)]
public sealed class RowSampleFilterTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const int ROW_COUNT = 100;

    public RowSampleFilterTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync("sample-test", new[] { CF });
        var tn = _fixture.GetTableName("sample-test");
        // Seed 100 rows
        var entries = Enumerable.Range(0, ROW_COUNT).Select(i =>
            Mutations.CreateEntry($"samp-{i:D4}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))).ToArray();
        await _fixture.Client.MutateRowsAsync(tn, entries);
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("sample-test");

    [Fact]
    public async Task Sample_1_0_returns_all_rows()
    {
        // p=1.0 means include all rows
        var filter = RowFilters.RowSample(1.0);
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter))
            rows.Add(row);
        rows.Should().HaveCount(ROW_COUNT);
    }

    [Fact]
    public async Task Sample_very_small_returns_few_or_none()
    {
        // p=0.01 means ~1% so about 0-5 rows from 100
        var filter = RowFilters.RowSample(0.01);
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter))
            rows.Add(row);
        rows.Should().HaveCountLessThanOrEqualTo(20); // generous upper bound
    }

    [Fact]
    public async Task Sample_0_5_returns_roughly_half()
    {
        // p=0.5 means ~50% so about 30-70 rows from 100 (with high probability)
        var filter = RowFilters.RowSample(0.5);
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter))
            rows.Add(row);
        rows.Should().HaveCountGreaterThan(10); // very generous lower bound
        rows.Should().HaveCountLessThan(95); // very generous upper bound
    }

    [Fact]
    public async Task Sample_preserves_cell_data()
    {
        var filter = RowFilters.RowSample(1.0);
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter))
            rows.Add(row);
        // Every row should have data
        foreach (var row in rows)
        {
            row.Families.Should().NotBeEmpty();
            row.Families[0].Columns.Should().NotBeEmpty();
            row.Families[0].Columns[0].Cells.Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task Sample_with_chain_filter()
    {
        var filter = RowFilters.Chain(
            RowFilters.RowSample(1.0),
            RowFilters.CellsPerColumnLimit(1));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter))
            rows.Add(row);
        rows.Should().HaveCount(ROW_COUNT);
    }

    [Fact]
    public async Task Sample_zero_returns_no_rows()
    {
        // p=0 is valid per the SDK (range [0, 1]). With p=0, Random.NextDouble() < 0 is always false.
        var filter = RowFilters.RowSample(0.0);
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public void Sample_invalid_negative_rejected_by_sdk()
    {
        // SDK validates p in [0, 1] — never reaches the server
        var act = () => RowFilters.RowSample(-0.5);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Sample_invalid_greater_than_one_rejected_by_sdk()
    {
        // SDK validates p in [0, 1] — never reaches the server
        var act = () => RowFilters.RowSample(1.5);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

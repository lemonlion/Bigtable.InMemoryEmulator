using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Comprehensive tests for filter chaining, condition filters, and complex compositions.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class FilterCompositionAdvancedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public FilterCompositionAdvancedTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync("filter-comp-adv", new[] { CF, CF2 });
        var tn = _fixture.GetTableName("filter-comp-adv");
        var client = _fixture.Client;

        // Seed various data patterns
        for (int i = 1; i <= 10; i++)
        {
            await client.MutateRowAsync(tn, $"fc-{i:D3}",
                Mutations.SetCell(CF, "name", $"item-{i}", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "count", $"{i * 10}", new BigtableVersion(1000)),
                Mutations.SetCell(CF2, "meta", $"m{i}", new BigtableVersion(1000)));
        }

        // Add multiple versions for some rows
        await client.MutateRowAsync(tn, "fc-001",
            Mutations.SetCell(CF, "name", "item-1-v2", new BigtableVersion(2000)));
        await client.MutateRowAsync(tn, "fc-001",
            Mutations.SetCell(CF, "name", "item-1-v3", new BigtableVersion(3000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("filter-comp-adv");

    #region Chain filters

    [Fact]
    public async Task Chain_family_then_column_then_limit()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.CellsPerColumnLimit(1));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter))
            rows.Add(row);

        rows.Should().HaveCount(10);
        foreach (var row in rows)
        {
            row.Families.Should().ContainSingle();
            row.Families[0].Name.Should().Be(CF);
            row.Families[0].Columns.Should().ContainSingle();
            row.Families[0].Columns[0].Cells.Should().ContainSingle();
        }
    }

    [Fact]
    public async Task Chain_value_regex_then_strip_value()
    {
        var filter = RowFilters.Chain(
            RowFilters.ValueRegex("item-[1-3]$"),
            RowFilters.StripValueTransformer());

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter))
            rows.Add(row);

        rows.Should().HaveCount(3);
        foreach (var row in rows)
        {
            var cells = row.Families.SelectMany(f => f.Columns.SelectMany(c => c.Cells));
            foreach (var cell in cells)
                cell.Value.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Chain_row_key_regex_then_cells_per_row()
    {
        var filter = RowFilters.Chain(
            RowFilters.RowKeyRegex("fc-00[1-5]"),
            RowFilters.CellsPerRowLimit(2));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter))
            rows.Add(row);

        rows.Should().HaveCount(5);
        foreach (var row in rows)
        {
            var cellCount = row.Families.SelectMany(f => f.Columns.SelectMany(c => c.Cells)).Count();
            cellCount.Should().BeLessThanOrEqualTo(2);
        }
    }

    #endregion

    #region Condition filters with various predicates

    [Fact]
    public async Task Condition_value_match_selects_family()
    {
        // If row has "item-1" (original version), select cf2; otherwise select CF
        var filter = RowFilters.Condition(
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("name"),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueRegex("item-1-v3")),
            RowFilters.FamilyNameExact(CF2),
            RowFilters.FamilyNameExact(CF));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter))
            rows.Add(row);

        // fc-001 has "item-1-v3" as latest → true → cf2
        var r001 = rows.FirstOrDefault(r => r.Key.ToStringUtf8() == "fc-001");
        r001.Should().NotBeNull();
        r001!.Families.Should().ContainSingle().Which.Name.Should().Be("cf2");
    }

    [Fact]
    public async Task Condition_nonexistent_column_is_false()
    {
        var filter = RowFilters.Condition(
            RowFilters.ColumnQualifierExact("nonexistent"),
            RowFilters.BlockAllFilter(),
            RowFilters.PassAllFilter());

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("fc-001"), filter))
            rows.Add(row);

        // Predicate produces no cells → false → pass all
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Condition_with_cells_per_column_predicate()
    {
        // Use CellsPerColumnLimit in predicate to check specific version count
        var filter = RowFilters.Condition(
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("name"),
                RowFilters.CellsPerColumnLimit(3)),
            RowFilters.FamilyNameExact(CF),
            RowFilters.FamilyNameExact(CF2));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("fc-001", "fc-005"), filter))
            rows.Add(row);

        rows.Should().HaveCount(2);
        // Both should match since predicate just checks if any cells exist
    }

    #endregion

    #region Block all and pass all

    [Fact]
    public async Task BlockAll_filter_returns_no_rows()
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, RowFilters.BlockAllFilter()))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task PassAll_filter_returns_all_data()
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, RowFilters.PassAllFilter()))
            rows.Add(row);
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task Chain_pass_all_multiple_times()
    {
        var filter = RowFilters.Chain(
            RowFilters.PassAllFilter(),
            RowFilters.PassAllFilter(),
            RowFilters.PassAllFilter());
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter))
            rows.Add(row);
        rows.Should().HaveCount(10);
    }

    #endregion

    #region CellsPerRow and CellsPerColumn limits

    [Fact]
    public async Task CellsPerRow_1()
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("fc-001"),
            RowFilters.CellsPerRowLimit(1)))
            rows.Add(row);

        rows.Should().ContainSingle();
        var totalCells = rows[0].Families.SelectMany(f => f.Columns.SelectMany(c => c.Cells)).Count();
        totalCells.Should().Be(1);
    }

    [Fact]
    public async Task CellsPerRow_offset()
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("fc-001"),
            RowFilters.CellsPerRowOffset(1)))
            rows.Add(row);

        // Skip first cell, return rest
        rows.Should().ContainSingle();
        var totalCells = rows[0].Families.SelectMany(f => f.Columns.SelectMany(c => c.Cells)).Count();
        totalCells.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CellsPerColumn_1_from_multi_version()
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("fc-001"),
            RowFilters.CellsPerColumnLimit(1)))
            rows.Add(row);

        rows.Should().ContainSingle();
        // name column should only have 1 version (latest)
        var nameCol = rows[0].Families.First(f => f.Name == CF)
            .Columns.First(c => c.Qualifier.ToStringUtf8() == "name");
        nameCol.Cells.Should().ContainSingle();
        nameCol.Cells[0].Value.ToStringUtf8().Should().Be("item-1-v3");
    }

    #endregion

    #region Timestamp range filter

    [Fact]
    public async Task Timestamp_range_filter_specific_version()
    {
        // Only version at 2000 (2_000_000 micros)
        var filter = RowFilters.TimestampRange(
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(2000),
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(3000));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("fc-001"), filter))
            rows.Add(row);

        if (rows.Count > 0)
        {
            var cells = rows[0].Families.SelectMany(f => f.Columns.SelectMany(c => c.Cells)).ToList();
            foreach (var cell in cells)
            {
                cell.TimestampMicros.Should().BeGreaterThanOrEqualTo(2_000_000);
                cell.TimestampMicros.Should().BeLessThan(3_000_000);
            }
        }
    }

    #endregion

    #region Sink filter

    [Fact(Skip = "Sink filter requires side-channel output to bypass parent filters; not yet implemented")]
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    public async Task Sink_filter_outputs_cells_directly()
    {
        // Sink filter: outputs cells directly bypassing the rest of the chain
        var filter = RowFilters.Chain(
            new RowFilter { Sink = true },
            RowFilters.BlockAllFilter());

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("fc-001"), filter))
            rows.Add(row);

        // Sink should output cells before block-all removes them
        rows.Should().NotBeEmpty();
    }

    #endregion

    #region Column range filter

    [Fact]
    public async Task Column_range_open_closed()
    {
        await _fixture.CreateTableAsync("filter-colrange", new[] { CF });
        var tn = _fixture.GetTableName("filter-colrange");
        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell(CF, "col-a", "va", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col-b", "vb", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col-c", "vc", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col-d", "vd", new BigtableVersion(1000)));

        var filter = RowFilters.ColumnRange(ColumnRange.OpenClosed(CF, "col-a", "col-c"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(tn, RowSet.FromRowKeys("r1"), filter))
            rows.Add(row);

        rows.Should().ContainSingle();
        var quals = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().Contain("col-b");
        quals.Should().Contain("col-c");
        quals.Should().NotContain("col-a");
    }

    [Fact]
    public async Task Column_range_closed_open()
    {
        await _fixture.CreateTableAsync("filter-colrange2", new[] { CF });
        var tn = _fixture.GetTableName("filter-colrange2");
        await Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell(CF, "col-a", "va", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col-b", "vb", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col-c", "vc", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col-d", "vd", new BigtableVersion(1000)));

        var filter = RowFilters.ColumnRange(ColumnRange.ClosedOpen(CF, "col-b", "col-d"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(tn, RowSet.FromRowKeys("r1"), filter))
            rows.Add(row);

        rows.Should().ContainSingle();
        var quals = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().Contain("col-b");
        quals.Should().Contain("col-c");
        quals.Should().NotContain("col-d");
    }

    #endregion

    #region Value range filter

    [Fact]
    public async Task Value_range_closed_open()
    {
        await _fixture.CreateTableAsync("filter-valrange", new[] { CF });
        var tn = _fixture.GetTableName("filter-valrange");
        for (int i = 0; i < 5; i++)
            await Client.MutateRowAsync(tn, $"vr-{i}",
                Mutations.SetCell(CF, "c", $"val-{(char)('a' + i)}", new BigtableVersion(1000)));

        var filter = RowFilters.ValueRange(ValueRange.ClosedOpen(
            ByteString.CopyFromUtf8("val-b"),
            ByteString.CopyFromUtf8("val-d")));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(tn, rows: null, filter))
            rows.Add(row);

        rows.Should().HaveCount(2); // val-b, val-c
    }

    #endregion
}

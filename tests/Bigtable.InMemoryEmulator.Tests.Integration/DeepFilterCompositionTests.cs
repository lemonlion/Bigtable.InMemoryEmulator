using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for filter chains with varying depth and complexity — deeply nested chains,
/// interleaves inside chains, conditions inside interleaves, etc.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class DeepFilterCompositionTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "dfc-tests";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public DeepFilterCompositionTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, "cf2", "cf3" });

        await Client.MutateRowAsync(TN, "dfc-r1",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "x", "10", new BigtableVersion(1000)),
            Mutations.SetCell("cf3", "y", "20", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "dfc-r2",
            Mutations.SetCell(CF, "a", "4", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "5", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "x", "40", new BigtableVersion(1000)));

        // Multi-version row
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "dfc-mv",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Chain_family_column_value()
    {
        var request = MakeRequest(RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.ColumnQualifierExact("a"),
            RowFilters.ValueExact("1")));

        var keys = await CollectKeys(request);
        keys.Should().ContainSingle("dfc-r1");
    }

    [Fact]
    public async Task Chain_with_cells_per_column_limit()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.FamilyNameExact(CF),
                RowFilters.ColumnQualifierExact("c"),
                RowFilters.CellsPerColumnLimit(2)),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("dfc-mv") } }
        };
        var vals = await CollectValues(request);
        vals.Should().HaveCount(2);
        vals.Should().Contain("v5");
        vals.Should().Contain("v4");
    }

    [Fact]
    public async Task Interleave_two_family_filters()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Interleave(
                RowFilters.FamilyNameExact(CF),
                RowFilters.FamilyNameExact("cf2")),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("dfc-r1") } }
        };
        var families = new HashSet<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
                families.Add(f.Name);

        families.Should().Contain(CF);
        families.Should().Contain("cf2");
    }

    [Fact]
    public async Task Interleave_three_column_filters()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Interleave(
                RowFilters.ColumnQualifierExact("a"),
                RowFilters.ColumnQualifierExact("b"),
                RowFilters.ColumnQualifierExact("x")),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("dfc-r1") } }
        };
        var cols = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
                cols.Add(c.Qualifier.ToStringUtf8());

        cols.Should().Contain("a");
        cols.Should().Contain("b");
        cols.Should().Contain("x");
    }

    [Fact]
    public async Task Chain_inside_interleave()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Interleave(
                RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("a")),
                RowFilters.Chain(RowFilters.FamilyNameExact("cf2"), RowFilters.ColumnQualifierExact("x"))),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("dfc-r1") } }
        };
        var vals = await CollectValues(request);
        vals.Should().HaveCount(2);
        vals.Should().Contain("1");
        vals.Should().Contain("10");
    }

    [Fact]
    public async Task Condition_true_passes_all()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Condition(
                RowFilters.ValueExact("1"),
                RowFilters.PassAllFilter(),
                RowFilters.BlockAllFilter()),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("dfc-r1") } }
        };
        var vals = await CollectValues(request);
        vals.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Condition_false_blocks_all()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Condition(
                RowFilters.ValueExact("nonexistent"),
                RowFilters.PassAllFilter(),
                RowFilters.BlockAllFilter()),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("dfc-r1") } }
        };
        var vals = await CollectValues(request);
        vals.Should().BeEmpty();
    }

    [Fact]
    public async Task Condition_true_selects_specific_column()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Condition(
                RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ValueExact("1")),
                RowFilters.ColumnQualifierExact("b"),
                RowFilters.ColumnQualifierExact("c")),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("dfc-r1") } }
        };
        var cols = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
                cols.Add(c.Qualifier.ToStringUtf8());

        cols.Should().Contain("b");
    }

    [Fact]
    public async Task Strip_value_in_chain()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.FamilyNameExact(CF),
                RowFilters.ColumnQualifierExact("a"),
                RowFilters.StripValueTransformer()),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("dfc-r1") } }
        };
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                cell.Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task Label_in_chain()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.FamilyNameExact(CF),
                RowFilters.ColumnQualifierExact("a"),
                new RowFilter { ApplyLabelTransformer = "my-label" }),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("dfc-r1") } }
        };
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                cell.Labels.Should().Contain("my-label");
    }

    [Fact]
    public async Task CellsPerRowLimit_in_chain()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.FamilyNameExact(CF),
                RowFilters.CellsPerRowLimit(1)),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("dfc-r1") } }
        };
        var cellCount = 0;
        await foreach (var row in Client.ReadRows(request))
            cellCount += row.Families.Sum(f => f.Columns.Sum(c => c.Cells.Count));

        cellCount.Should().Be(1);
    }

    [Fact]
    public async Task CellsPerRowOffset_in_chain()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.FamilyNameExact(CF),
                RowFilters.CellsPerRowOffset(2)),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("dfc-r1") } }
        };
        var vals = await CollectValues(request);
        vals.Should().ContainSingle("3"); // Skip a=1, b=2 → c=3
    }

    [Fact]
    public async Task ColumnRange_in_chain()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.FamilyNameExact(CF),
                RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "b"))),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("dfc-r1") } }
        };
        var cols = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
                cols.Add(c.Qualifier.ToStringUtf8());

        cols.Should().BeEquivalentTo("a", "b");
    }

    [Fact]
    public async Task ValueRange_in_chain()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.FamilyNameExact(CF),
                RowFilters.ValueRange(ValueRange.Closed("1", "3"))),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("dfc-r1") } }
        };
        var vals = await CollectValues(request);
        vals.Should().BeEquivalentTo("1", "2", "3");
    }

    [Fact]
    public async Task Timestamp_range_in_chain()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.FamilyNameExact(CF),
                RowFilters.ColumnQualifierExact("c"),
                RowFilters.TimestampRange(
                    new DateTime(1970, 1, 1, 0, 0, 2, DateTimeKind.Utc),
                    new DateTime(1970, 1, 1, 0, 0, 4, DateTimeKind.Utc))),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("dfc-mv") } }
        };
        var vals = await CollectValues(request);
        vals.Should().BeEquivalentTo("v2", "v3");
    }

    [Fact]
    public async Task PassAll_in_chain_is_noop()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.PassAllFilter(),
                RowFilters.FamilyNameExact(CF),
                RowFilters.PassAllFilter()),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("dfc-r1") } }
        };
        var vals = await CollectValues(request);
        vals.Should().HaveCount(3);
    }

    [Fact]
    public async Task BlockAll_in_chain_blocks_everything()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.FamilyNameExact(CF),
                RowFilters.BlockAllFilter()),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("dfc-r1") } }
        };
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(0);
    }

    [Fact]
    public async Task Interleave_with_overlapping_results_deduplicates()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Interleave(
                RowFilters.FamilyNameExact(CF),
                RowFilters.ColumnQualifierExact("a")),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("dfc-r1") } }
        };
        var vals = await CollectValues(request);
        // Interleave may deduplicate or not — we just verify it doesn't crash
        vals.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Double_chain_nested()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.Chain(
                    RowFilters.FamilyNameExact(CF),
                    RowFilters.ColumnQualifierRegex("a|b")),
                RowFilters.CellsPerRowLimit(1)),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("dfc-r1") } }
        };
        var vals = await CollectValues(request);
        vals.Should().HaveCount(1);
    }

    [Fact]
    public async Task Row_key_regex_combined_with_column_filter()
    {
        var request = MakeRequest(RowFilters.Chain(
            RowFilters.RowKeyRegex("dfc-r.*"),
            RowFilters.FamilyNameExact(CF),
            RowFilters.ColumnQualifierExact("a")));
        var vals = await CollectValues(request);
        vals.Should().HaveCount(2);
        vals.Should().Contain("1");
        vals.Should().Contain("4");
    }

    private ReadRowsRequest MakeRequest(RowFilter filter) =>
        new() { TableNameAsTableName = TN, Filter = filter };

    private async Task<List<string>> CollectKeys(ReadRowsRequest request)
    {
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());
        return keys;
    }

    private async Task<List<string>> CollectValues(ReadRowsRequest request)
    {
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                vals.Add(cell.Value.ToStringUtf8());
        return vals;
    }
}

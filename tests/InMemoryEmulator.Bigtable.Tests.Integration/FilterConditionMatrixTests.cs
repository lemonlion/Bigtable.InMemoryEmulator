using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Systematic condition filter tests: different predicate types × true/false branch types.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
///   "condition { predicate_filter, true_filter, false_filter }"
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class FilterConditionMatrixTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "fcm-tests";
    private const string CF = "cf";

    public FilterConditionMatrixTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private async Task<List<string>> ReadValues(string key, RowFilter filter)
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = filter,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8(key) } }
        };
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                vals.Add(cell.Value.ToStringUtf8());
        return vals;
    }

    private async Task<int> CountCells(string key, RowFilter filter)
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = filter,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8(key) } }
        };
        int count = 0;
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
                count += c.Cells.Count;
        return count;
    }

    [Fact]
    public async Task PassAll_predicate_always_takes_true_branch()
    {
        await Client.MutateRowAsync(TN, "fcm-pa",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var filter = RowFilters.Condition(
            RowFilters.PassAllFilter(),
            RowFilters.CellsPerRowLimit(1),
            RowFilters.BlockAllFilter());
        (await CountCells("fcm-pa", filter)).Should().Be(1);
    }

    [Fact]
    public async Task BlockAll_predicate_always_takes_false_branch()
    {
        await Client.MutateRowAsync(TN, "fcm-ba",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var filter = RowFilters.Condition(
            RowFilters.BlockAllFilter(),
            RowFilters.PassAllFilter(),
            RowFilters.StripValueTransformer());
        var vals = await ReadValues("fcm-ba", filter);
        vals.Should().HaveCount(1);
        vals[0].Should().BeEmpty(); // false branch strips value
    }

    [Fact]
    public async Task ValueExact_predicate_matches_takes_true()
    {
        await Client.MutateRowAsync(TN, "fcm-ve-t",
            Mutations.SetCell(CF, "c", "target", new BigtableVersion(1000)));

        var filter = RowFilters.Condition(
            RowFilters.ValueExact("target"),
            RowFilters.PassAllFilter(),
            RowFilters.BlockAllFilter());
        (await CountCells("fcm-ve-t", filter)).Should().Be(1);
    }

    [Fact]
    public async Task ValueExact_predicate_no_match_takes_false()
    {
        await Client.MutateRowAsync(TN, "fcm-ve-f",
            Mutations.SetCell(CF, "c", "other", new BigtableVersion(1000)));

        var filter = RowFilters.Condition(
            RowFilters.ValueExact("target"),
            RowFilters.PassAllFilter(),
            RowFilters.BlockAllFilter());
        (await CountCells("fcm-ve-f", filter)).Should().Be(0);
    }

    [Fact]
    public async Task FamilyName_predicate_matches()
    {
        await Client.MutateRowAsync(TN, "fcm-fn",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var filter = RowFilters.Condition(
            RowFilters.FamilyNameExact(CF),
            RowFilters.PassAllFilter(),
            RowFilters.BlockAllFilter());
        (await CountCells("fcm-fn", filter)).Should().Be(1);
    }

    [Fact]
    public async Task FamilyName_predicate_no_match()
    {
        await Client.MutateRowAsync(TN, "fcm-fn-f",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var filter = RowFilters.Condition(
            RowFilters.FamilyNameExact("nonexistent"),
            RowFilters.PassAllFilter(),
            RowFilters.BlockAllFilter());
        (await CountCells("fcm-fn-f", filter)).Should().Be(0);
    }

    [Fact]
    public async Task ColumnQualifier_predicate_matches()
    {
        await Client.MutateRowAsync(TN, "fcm-cq",
            Mutations.SetCell(CF, "target", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "other", "v", new BigtableVersion(1000)));

        var filter = RowFilters.Condition(
            RowFilters.ColumnQualifierExact("target"),
            RowFilters.PassAllFilter(),
            RowFilters.BlockAllFilter());
        // Predicate matched (some output) → true branch → pass all data
        (await CountCells("fcm-cq", filter)).Should().Be(2);
    }

    [Fact]
    public async Task RowKeyRegex_predicate_matches()
    {
        await Client.MutateRowAsync(TN, "fcm-rkr",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var filter = RowFilters.Condition(
            RowFilters.RowKeyRegex("fcm-rkr"),
            RowFilters.PassAllFilter(),
            RowFilters.BlockAllFilter());
        (await CountCells("fcm-rkr", filter)).Should().Be(1);
    }

    [Fact]
    public async Task RowKeyRegex_predicate_no_match()
    {
        await Client.MutateRowAsync(TN, "fcm-rkr-f",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var filter = RowFilters.Condition(
            RowFilters.RowKeyRegex("nomatch.*"),
            RowFilters.PassAllFilter(),
            RowFilters.BlockAllFilter());
        (await CountCells("fcm-rkr-f", filter)).Should().Be(0);
    }

    [Fact]
    public async Task ValueRegex_predicate_matches()
    {
        await Client.MutateRowAsync(TN, "fcm-vreg",
            Mutations.SetCell(CF, "c", "hello-123", new BigtableVersion(1000)));

        var filter = RowFilters.Condition(
            RowFilters.ValueRegex("hello-.*"),
            RowFilters.PassAllFilter(),
            RowFilters.BlockAllFilter());
        (await CountCells("fcm-vreg", filter)).Should().Be(1);
    }

    [Fact]
    public async Task Nested_condition_predicate()
    {
        await Client.MutateRowAsync(TN, "fcm-nested",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var innerCondition = RowFilters.Condition(
            RowFilters.PassAllFilter(),
            RowFilters.ValueExact("v"),
            RowFilters.BlockAllFilter());

        var outerFilter = RowFilters.Condition(
            innerCondition,
            RowFilters.PassAllFilter(),
            RowFilters.BlockAllFilter());
        (await CountCells("fcm-nested", outerFilter)).Should().Be(1);
    }

    [Fact]
    public async Task Chain_predicate_with_two_filters()
    {
        await Client.MutateRowAsync(TN, "fcm-chain-pred",
            Mutations.SetCell(CF, "target", "yes", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "other", "no", new BigtableVersion(1000)));

        var filter = RowFilters.Condition(
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("target"),
                RowFilters.ValueExact("yes")),
            RowFilters.PassAllFilter(),
            RowFilters.BlockAllFilter());
        (await CountCells("fcm-chain-pred", filter)).Should().Be(2);
    }

    [Fact]
    public async Task True_branch_strips_false_branch_passes()
    {
        await Client.MutateRowAsync(TN, "fcm-tf-strip",
            Mutations.SetCell(CF, "c", "data", new BigtableVersion(1000)));

        var filter = RowFilters.Condition(
            RowFilters.ValueExact("data"),
            RowFilters.StripValueTransformer(),
            RowFilters.PassAllFilter());
        var vals = await ReadValues("fcm-tf-strip", filter);
        vals.Should().HaveCount(1);
        vals[0].Should().BeEmpty(); // true branch strips
    }

    [Fact]
    public async Task True_branch_limits_cells()
    {
        await Client.MutateRowAsync(TN, "fcm-tf-limit",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(1000)));

        var filter = RowFilters.Condition(
            RowFilters.PassAllFilter(),
            RowFilters.CellsPerRowLimit(1),
            RowFilters.PassAllFilter());
        (await CountCells("fcm-tf-limit", filter)).Should().Be(1);
    }

    [Fact]
    public async Task False_branch_limits_cells()
    {
        await Client.MutateRowAsync(TN, "fcm-ff-limit",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(1000)));

        var filter = RowFilters.Condition(
            RowFilters.BlockAllFilter(),
            RowFilters.PassAllFilter(),
            RowFilters.CellsPerRowLimit(2));
        (await CountCells("fcm-ff-limit", filter)).Should().Be(2);
    }

    [Fact]
    public async Task Condition_with_column_range_predicate()
    {
        await Client.MutateRowAsync(TN, "fcm-cr-pred",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "m", "v2", new BigtableVersion(1000)));

        var filter = RowFilters.Condition(
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "m", "z")),
            RowFilters.PassAllFilter(),
            RowFilters.BlockAllFilter());
        // "m" exists → predicate matches → passall
        (await CountCells("fcm-cr-pred", filter)).Should().Be(2);
    }

    [Fact]
    public async Task Condition_with_value_range_predicate()
    {
        await Client.MutateRowAsync(TN, "fcm-vr-pred",
            Mutations.SetCell(CF, "c", "medium", new BigtableVersion(1000)));

        var filter = RowFilters.Condition(
            RowFilters.ValueRange(ValueRange.Closed("a", "z")),
            RowFilters.PassAllFilter(),
            RowFilters.BlockAllFilter());
        (await CountCells("fcm-vr-pred", filter)).Should().Be(1);
    }

    [Fact]
    public async Task Condition_with_timestamp_range_predicate()
    {
        var ts = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        await Client.MutateRowAsync(TN, "fcm-ts-pred",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(ts)));

        var filter = RowFilters.Condition(
            RowFilters.TimestampRange(ts, ts.AddDays(1)),
            RowFilters.PassAllFilter(),
            RowFilters.BlockAllFilter());
        (await CountCells("fcm-ts-pred", filter)).Should().Be(1);
    }

    [Fact]
    public async Task Condition_empty_row_takes_false_branch()
    {
        // No data for this key
        var filter = RowFilters.Condition(
            RowFilters.PassAllFilter(),
            RowFilters.PassAllFilter(),
            RowFilters.BlockAllFilter());

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = filter,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fcm-empty") } }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task True_branch_with_family_filter()
    {
        await Client.MutateRowAsync(TN, "fcm-tb-fam",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)));

        var filter = RowFilters.Condition(
            RowFilters.PassAllFilter(),
            RowFilters.FamilyNameExact(CF),
            RowFilters.BlockAllFilter());
        (await CountCells("fcm-tb-fam", filter)).Should().Be(1);
    }

    [Fact]
    public async Task Interleave_predicate()
    {
        await Client.MutateRowAsync(TN, "fcm-ilv-pred",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)));

        var filter = RowFilters.Condition(
            RowFilters.Interleave(
                RowFilters.ColumnQualifierExact("a"),
                RowFilters.ColumnQualifierExact("nonexistent")),
            RowFilters.PassAllFilter(),
            RowFilters.BlockAllFilter());
        // "a" exists through interleave → predicate outputs → true branch
        (await CountCells("fcm-ilv-pred", filter)).Should().Be(1);
    }

    [Fact]
    public async Task Condition_with_label_in_true_branch()
    {
        await Client.MutateRowAsync(TN, "fcm-label",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var filter = RowFilters.Condition(
            RowFilters.PassAllFilter(),
            new RowFilter { ApplyLabelTransformer = "from-true" },
            RowFilters.PassAllFilter());

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = filter,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fcm-label") } }
        };
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                cell.Labels.Should().Contain("from-true");
    }
}

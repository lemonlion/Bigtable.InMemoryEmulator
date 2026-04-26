using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for condition (ternary) filter behavior with various predicate patterns.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ConditionFilterExtendedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "cofe-tests";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public ConditionFilterExtendedTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });

        await Client.MutateRowAsync(TN, "cofe-active",
            Mutations.SetCell(CF, "status", "active", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "name", "Alice", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "cofe-inactive",
            Mutations.SetCell(CF, "status", "inactive", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "name", "Bob", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "cofe-multi",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "3", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task True_branch_when_predicate_matches()
    {
        var request = MakeRequest("cofe-active",
            RowFilters.Condition(
                RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueExact("active")),
                RowFilters.ColumnQualifierExact("name"),
                RowFilters.BlockAllFilter()));
        var vals = await CollectValues(request);
        vals.Should().ContainSingle("Alice");
    }

    [Fact]
    public async Task False_branch_when_predicate_no_match()
    {
        var request = MakeRequest("cofe-inactive",
            RowFilters.Condition(
                RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueExact("active")),
                RowFilters.BlockAllFilter(),
                RowFilters.ColumnQualifierExact("name")));
        var vals = await CollectValues(request);
        vals.Should().ContainSingle("Bob");
    }

    [Fact]
    public async Task Pass_all_predicate_matches_any_row()
    {
        var request = MakeRequest("cofe-active",
            RowFilters.Condition(
                RowFilters.PassAllFilter(),
                new RowFilter { ApplyLabelTransformer = "exists" },
                RowFilters.BlockAllFilter()));
        var labels = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                labels.AddRange(cell.Labels);
        labels.Should().Contain("exists");
    }

    [Fact]
    public async Task Block_all_predicate_always_false()
    {
        var request = MakeRequest("cofe-active",
            RowFilters.Condition(
                RowFilters.BlockAllFilter(),
                RowFilters.BlockAllFilter(),
                RowFilters.PassAllFilter()));
        var cellCount = 0;
        await foreach (var row in Client.ReadRows(request))
            cellCount += row.Families.Sum(f => f.Columns.Sum(c => c.Cells.Count));
        cellCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Condition_on_nonexistent_row_uses_false_branch()
    {
        var request = MakeRequest("cofe-ghost",
            RowFilters.Condition(
                RowFilters.PassAllFilter(),
                new RowFilter { ApplyLabelTransformer = "found" },
                new RowFilter { ApplyLabelTransformer = "missing" }));
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(0); // Row doesn't exist, so no output at all
    }

    [Fact]
    public async Task True_branch_with_family_filter()
    {
        var request = MakeRequest("cofe-multi",
            RowFilters.Condition(
                RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("a")),
                RowFilters.FamilyNameExact("cf2"),
                RowFilters.BlockAllFilter()));
        var vals = await CollectValues(request);
        vals.Should().ContainSingle("3");
    }

    [Fact]
    public async Task True_branch_limits_output()
    {
        var request = MakeRequest("cofe-multi",
            RowFilters.Condition(
                RowFilters.PassAllFilter(),
                RowFilters.CellsPerRowLimit(1),
                RowFilters.PassAllFilter()));
        var cellCount = 0;
        await foreach (var row in Client.ReadRows(request))
            cellCount += row.Families.Sum(f => f.Columns.Sum(c => c.Cells.Count));
        cellCount.Should().Be(1);
    }

    [Fact]
    public async Task Nested_condition()
    {
        var request = MakeRequest("cofe-active",
            RowFilters.Condition(
                RowFilters.PassAllFilter(),
                RowFilters.Condition(
                    RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueExact("active")),
                    RowFilters.ColumnQualifierExact("name"),
                    RowFilters.BlockAllFilter()),
                RowFilters.BlockAllFilter()));
        var vals = await CollectValues(request);
        vals.Should().ContainSingle("Alice");
    }

    [Fact]
    public async Task Condition_with_value_regex_predicate()
    {
        var request = MakeRequest("cofe-active",
            RowFilters.Condition(
                RowFilters.ValueRegex("active"),
                RowFilters.CellsPerRowLimit(1),
                RowFilters.PassAllFilter()));
        var cellCount = 0;
        await foreach (var row in Client.ReadRows(request))
            cellCount += row.Families.Sum(f => f.Columns.Sum(c => c.Cells.Count));
        cellCount.Should().Be(1);
    }

    [Fact]
    public async Task Condition_with_row_key_regex_predicate()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Condition(
                RowFilters.RowKeyRegex("cofe-active"),
                new RowFilter { ApplyLabelTransformer = "active-row" },
                new RowFilter { ApplyLabelTransformer = "other-row" })
        };
        var labelMap = new Dictionary<string, string>();
        await foreach (var row in Client.ReadRows(request))
        {
            var firstLabel = row.Families.SelectMany(f => f.Columns)
                .SelectMany(c => c.Cells).SelectMany(c => c.Labels).First();
            labelMap[row.Key.ToStringUtf8()] = firstLabel;
        }
        labelMap.Should().ContainKey("cofe-active");
        labelMap["cofe-active"].Should().Be("active-row");
    }

    [Fact]
    public async Task Condition_with_column_range_predicate()
    {
        var request = MakeRequest("cofe-multi",
            RowFilters.Condition(
                RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "a")),
                RowFilters.ColumnQualifierExact("b"),
                RowFilters.BlockAllFilter()));
        var vals = await CollectValues(request);
        vals.Should().ContainSingle("2");
    }

    [Fact]
    public async Task True_branch_with_strip_value()
    {
        var request = MakeRequest("cofe-active",
            RowFilters.Condition(
                RowFilters.PassAllFilter(),
                RowFilters.StripValueTransformer(),
                RowFilters.PassAllFilter()));
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                cell.Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task Condition_across_multiple_rows()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Condition(
                RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueExact("active")),
                RowFilters.ColumnQualifierExact("name"),
                RowFilters.BlockAllFilter()),
            Rows = new RowSet
            {
                RowKeys =
                {
                    ByteString.CopyFromUtf8("cofe-active"),
                    ByteString.CopyFromUtf8("cofe-inactive")
                }
            }
        };
        var results = new Dictionary<string, string>();
        await foreach (var row in Client.ReadRows(request))
        {
            var vals = row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
                .Select(c => c.Value.ToStringUtf8()).ToList();
            if (vals.Any()) results[row.Key.ToStringUtf8()] = vals.First();
        }
        results.Should().ContainKey("cofe-active");
        results["cofe-active"].Should().Be("Alice");
        // cofe-inactive should be blocked
        results.Should().NotContainKey("cofe-inactive");
    }

    [Fact]
    public async Task Condition_with_timestamp_predicate()
    {
        await Client.MutateRowAsync(TN, "cofe-ts",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(5000)));

        var start = new DateTime(1970, 1, 1, 0, 0, 4, DateTimeKind.Utc);
        var end = new DateTime(1970, 1, 1, 0, 0, 6, DateTimeKind.Utc);

        var request = MakeRequest("cofe-ts",
            RowFilters.Condition(
                RowFilters.TimestampRange(start, end),
                new RowFilter { ApplyLabelTransformer = "recent" },
                new RowFilter { ApplyLabelTransformer = "stale" }));
        var labels = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                labels.AddRange(cell.Labels);
        labels.Should().Contain("recent");
    }

    private ReadRowsRequest MakeRequest(string key, RowFilter filter) =>
        new()
        {
            TableNameAsTableName = TN,
            Filter = filter,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8(key) } }
        };

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

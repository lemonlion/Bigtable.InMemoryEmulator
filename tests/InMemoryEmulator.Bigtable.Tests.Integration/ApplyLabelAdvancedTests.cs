using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for apply label transformer filter and label interactions with other filters.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
///   "apply_label_transformer: Applies the given label to all cells in the output row."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ApplyLabelAdvancedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string CF2 = "cf2";
    private const string Table = "label-adv";

    public ApplyLabelAdvancedTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
        var tn = TN;
        await _fixture.Client.MutateRowAsync(tn, "lb-r1",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "3", new BigtableVersion(1000)));
        await _fixture.Client.MutateRowAsync(tn, "lb-r2",
            Mutations.SetCell(CF, "x", "10", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    #region Basic labels

    [Fact]
    public async Task Label_applied_to_all_cells()
    {
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("lb-r1"),
            filter: new RowFilter { ApplyLabelTransformer = "test-label" }))
        {
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    foreach (var cell in col.Cells)
                        cell.Labels.Should().Contain("test-label");
        }
    }

    [Fact]
    public async Task Label_empty_string_rejected()
    {
        // Ref: ApplyLabelTransformer must match [a-z0-9\-]+, so empty is invalid
        var act = async () =>
        {
            await foreach (var _ in Client.ReadRows(TN, RowSet.FromRowKeys("lb-r2"),
                filter: new RowFilter { ApplyLabelTransformer = "" })) { }
        };
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    #endregion

    #region Label with chain

    [Fact]
    public async Task Chain_filter_then_label()
    {
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("a"),
            new RowFilter { ApplyLabelTransformer = "col-a" });
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("lb-r1"),
            filter: filter))
        {
            var cells = row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
            cells.Should().ContainSingle();
            cells[0].Labels.Should().Contain("col-a");
        }
    }

    [Fact]
    public async Task Chain_family_filter_then_label()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameRegex(CF),
            new RowFilter { ApplyLabelTransformer = "cf-cells" });
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("lb-r1"),
            filter: filter))
        {
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    foreach (var cell in col.Cells)
                        cell.Labels.Should().Contain("cf-cells");
        }
    }

    #endregion

    #region Label with interleave

    [Fact]
    public async Task Interleave_different_labels_per_branch()
    {
        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.FamilyNameRegex(CF), new RowFilter { ApplyLabelTransformer = "from-cf" }),
            RowFilters.Chain(RowFilters.FamilyNameRegex(CF2), new RowFilter { ApplyLabelTransformer = "from-cf2" }));
        var allLabels = new List<string>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("lb-r1"),
            filter: filter))
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    foreach (var cell in col.Cells)
                        allLabels.AddRange(cell.Labels);
        allLabels.Should().Contain("from-cf").And.Contain("from-cf2");
    }

    #endregion

    #region Label with condition

    [Fact]
    public async Task Condition_true_branch_labeled()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("a"), RowFilters.ValueRegex("1")),
            RowFilters.Chain(RowFilters.PassAllFilter(), new RowFilter { ApplyLabelTransformer = "matched" }),
            RowFilters.Chain(RowFilters.PassAllFilter(), new RowFilter { ApplyLabelTransformer = "unmatched" }));
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("lb-r1"),
            filter: filter))
        {
            var labels = row.Families.SelectMany(f => f.Columns)
                .SelectMany(c => c.Cells).SelectMany(c => c.Labels).Distinct().ToList();
            labels.Should().Contain("matched").And.NotContain("unmatched");
        }
    }

    [Fact]
    public async Task Condition_false_branch_labeled()
    {
        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("a"), RowFilters.ValueRegex("999")),
            RowFilters.Chain(RowFilters.PassAllFilter(), new RowFilter { ApplyLabelTransformer = "matched" }),
            RowFilters.Chain(RowFilters.PassAllFilter(), new RowFilter { ApplyLabelTransformer = "unmatched" }));
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("lb-r1"),
            filter: filter))
        {
            var labels = row.Families.SelectMany(f => f.Columns)
                .SelectMany(c => c.Cells).SelectMany(c => c.Labels).Distinct().ToList();
            labels.Should().Contain("unmatched").And.NotContain("matched");
        }
    }

    #endregion

    #region Label across rows

    [Fact]
    public async Task Label_applied_consistently_across_rows()
    {
        var rowLabels = new Dictionary<string, List<string>>();
        await foreach (var row in Client.ReadRows(TN, rows: null,
            filter: new RowFilter { ApplyLabelTransformer = "global" }))
        {
            var labels = row.Families.SelectMany(f => f.Columns)
                .SelectMany(c => c.Cells).SelectMany(c => c.Labels).ToList();
            rowLabels[row.Key.ToStringUtf8()] = labels;
        }
        foreach (var (key, labels) in rowLabels)
            labels.Should().OnlyContain(l => l == "global");
    }

    #endregion

    #region Label preserves data

    [Fact]
    public async Task Label_does_not_modify_values()
    {
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("lb-r1"),
            filter: new RowFilter { ApplyLabelTransformer = "tag" }))
        {
            var values = row.Families.SelectMany(f => f.Columns)
                .SelectMany(c => c.Cells).Select(c => c.Value.ToStringUtf8()).ToList();
            values.Should().BeEquivalentTo(new[] { "1", "2", "3" });
        }
    }

    [Fact]
    public async Task Label_does_not_modify_timestamps()
    {
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("lb-r1"),
            filter: new RowFilter { ApplyLabelTransformer = "tag" }))
        {
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    foreach (var cell in col.Cells)
                        cell.TimestampMicros.Should().Be(1_000_000);
        }
    }

    #endregion
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for ApplyLabelTransformer filter: label setting, validation, chain rules.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
///   "apply_label_transformer: Applies the given label to all cells in the output row."
///   "Labels must be at most 15 characters in length, match re2:[a-z0-9\\-]+ and cannot be empty."
///   "A chain cannot have more than one cell-outputting stage that uses apply_label_transformer."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ApplyLabelTransformerTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public ApplyLabelTransformerTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync("lbl-test", new[] { CF });
        var tn = _fixture.GetTableName("lbl-test");
        await _fixture.Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell(CF, "c1", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c2", "v2", new BigtableVersion(1000)));
    }
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("lbl-test");

    #region Valid labels

    [Fact]
    public async Task Label_applied_to_cells()
    {
        var filter = new RowFilter { ApplyLabelTransformer = "test-label" };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter))
            rows.Add(row);

        rows.Should().ContainSingle();
        foreach (var fam in rows[0].Families)
            foreach (var col in fam.Columns)
                foreach (var cell in col.Cells)
                    cell.Labels.Should().Contain("test-label");
    }

    [Fact]
    public async Task Single_char_label()
    {
        var filter = new RowFilter { ApplyLabelTransformer = "a" };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter))
            rows.Add(row);
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells[0].Labels.Should().Contain("a");
    }

    [Fact]
    public async Task Label_with_digits()
    {
        var filter = new RowFilter { ApplyLabelTransformer = "label-123" };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter))
            rows.Add(row);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Label_with_hyphens()
    {
        var filter = new RowFilter { ApplyLabelTransformer = "my-test-lbl" };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter))
            rows.Add(row);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Label_exactly_15_chars()
    {
        // Exactly 15 characters = valid
        var filter = new RowFilter { ApplyLabelTransformer = "abcdefghijklmno" };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter))
            rows.Add(row);
        rows.Should().ContainSingle();
    }

    #endregion

    #region Invalid labels

    [Fact]
    public async Task Label_too_long_throws()
    {
        // 16+ chars is invalid
        var filter = new RowFilter { ApplyLabelTransformer = "abcdefghijklmnop" };
        var act = async () =>
        {
            await foreach (var _ in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter)) { }
        };
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    [Fact]
    public async Task Label_with_uppercase_throws()
    {
        // Only lowercase allowed: [a-z0-9\-]+
        var filter = new RowFilter { ApplyLabelTransformer = "BadLabel" };
        var act = async () =>
        {
            await foreach (var _ in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter)) { }
        };
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    [Fact]
    public async Task Label_with_underscore_throws()
    {
        var filter = new RowFilter { ApplyLabelTransformer = "bad_label" };
        var act = async () =>
        {
            await foreach (var _ in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter)) { }
        };
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    [Fact]
    public async Task Label_with_space_throws()
    {
        var filter = new RowFilter { ApplyLabelTransformer = "bad label" };
        var act = async () =>
        {
            await foreach (var _ in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter)) { }
        };
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    [Fact]
    public async Task Empty_label_throws()
    {
        var filter = new RowFilter { ApplyLabelTransformer = "" };
        var act = async () =>
        {
            await foreach (var _ in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter)) { }
        };
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    #endregion

    #region Chain rules for labels

    [Fact]
    public async Task Chain_with_label_and_pass_all()
    {
        var filter = RowFilters.Chain(
            RowFilters.PassAllFilter(),
            new RowFilter { ApplyLabelTransformer = "tagged" });
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter))
            rows.Add(row);
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells[0].Labels.Should().Contain("tagged");
    }

    [Fact]
    public async Task Label_in_interleave()
    {
        // Labels in separate interleave branches should work independently
        var filter = RowFilters.Interleave(
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("c1"),
                new RowFilter { ApplyLabelTransformer = "branch-a" }),
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("c2"),
                new RowFilter { ApplyLabelTransformer = "branch-b" }));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter))
            rows.Add(row);
        rows.Should().ContainSingle();
        var allCells = rows[0].Families.SelectMany(f => f.Columns.SelectMany(c => c.Cells)).ToList();
        allCells.Should().HaveCount(2);
        var labelA = allCells.Where(c => c.Labels.Contains("branch-a")).ToList();
        var labelB = allCells.Where(c => c.Labels.Contains("branch-b")).ToList();
        labelA.Should().ContainSingle();
        labelB.Should().ContainSingle();
    }

    [Fact]
    public async Task Chain_with_two_labels_throws()
    {
        // A chain cannot have more than one apply_label_transformer
        var filter = RowFilters.Chain(
            new RowFilter { ApplyLabelTransformer = "first" },
            new RowFilter { ApplyLabelTransformer = "second" });
        var act = async () =>
        {
            await foreach (var _ in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter)) { }
        };
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    #endregion

    #region Label with condition filter

    [Fact]
    public async Task Label_in_condition_true_branch()
    {
        var filter = RowFilters.Condition(
            RowFilters.ValueRegex("v1"),
            new RowFilter { ApplyLabelTransformer = "matched" },
            RowFilters.PassAllFilter());
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter))
            rows.Add(row);
        rows.Should().ContainSingle();
        // At least some cells should have the label
        var labeled = rows[0].Families.SelectMany(f => f.Columns.SelectMany(c => c.Cells))
            .Where(c => c.Labels.Contains("matched")).ToList();
        labeled.Should().NotBeEmpty();
    }

    #endregion
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class LabelFilterBehaviorTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "lbl-beh";
    private const string CF = "cf";

    public LabelFilterBehaviorTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        await Client.MutateRowAsync(TN, "r1", Mutations.SetCell(CF, "a", "v1"));
        await Client.MutateRowAsync(TN, "r2", Mutations.SetCell(CF, "b", "v2"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Label_is_attached_to_cells()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
        var filter = new RowFilter { ApplyLabelTransformer = "tagged" };
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Labels.Should().Contain("tagged");
    }

    [Fact]
    public async Task Label_with_digits()
    {
        var filter = new RowFilter { ApplyLabelTransformer = "label-123" };
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row!.Families[0].Columns[0].Cells[0].Labels.Should().Contain("label-123");
    }

    [Fact]
    public async Task Label_in_interleave()
    {
        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("a"), new RowFilter { ApplyLabelTransformer = "col-a" }),
            RowFilters.Chain(RowFilters.ColumnQualifierExact("b"), new RowFilter { ApplyLabelTransformer = "col-b" }));
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Labels.Should().Contain("col-a");
    }

    [Fact]
    public async Task Label_does_not_affect_value()
    {
        var filter = new RowFilter { ApplyLabelTransformer = "mytag" };
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v1");
    }

    [Fact]
    public async Task Label_on_missing_row_returns_null()
    {
        var filter = new RowFilter { ApplyLabelTransformer = "tag" };
        var row = await Client.ReadRowAsync(TN, "no-exist", filter);
        row.Should().BeNull();
    }

    [Fact]
    public async Task Label_with_chain_filter()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            new RowFilter { ApplyLabelTransformer = "fam" });
        var row = await Client.ReadRowAsync(TN, "r2", filter);
        row!.Families[0].Columns[0].Cells[0].Labels.Should().Contain("fam");
    }

    [Fact]
    public async Task Label_on_all_rows()
    {
        var filter = new RowFilter { ApplyLabelTransformer = "all" };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: filter)) rows.Add(r);
        rows.Should().HaveCount(2);
        foreach (var row in rows)
            row.Families[0].Columns[0].Cells[0].Labels.Should().Contain("all");
    }

    [Fact]
    public async Task Multiple_labels_via_interleave()
    {
        // Interleave two label filters — should produce two cells with different labels
        var filter = RowFilters.Interleave(
            new RowFilter { ApplyLabelTransformer = "first" },
            new RowFilter { ApplyLabelTransformer = "second" });
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns.SelectMany(c => c.Cells)).ToList();
        cells.Should().HaveCount(2);
        cells.SelectMany(c => c.Labels).Should().Contain("first").And.Contain("second");
    }
}

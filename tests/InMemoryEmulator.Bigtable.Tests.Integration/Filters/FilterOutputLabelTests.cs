using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class FilterOutputLabelTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "fol-tests";
    private const string CF = "cf";

    public FilterOutputLabelTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null)
    {
        var list = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rows, filter: filter))
            list.Add(r);
        return list;
    }

    [Fact]
    public async Task Single_label_applied_to_all_cells()
    {
        var rk = "fol-single-1";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a", "v1"));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "b", "v2"));

        var row = await Client.ReadRowAsync(TN, rk, new RowFilter { ApplyLabelTransformer = "tag1" });
        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(2);
        cells.Should().AllSatisfy(c => c.Labels.Should().Contain("tag1"));
    }

    [Fact]
    public async Task Label_in_chain_applied_after_filter()
    {
        var rk = "fol-chain-1";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "target", "good"));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "other", "skip"));

        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("target"),
            new RowFilter { ApplyLabelTransformer = "matched" });
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().ContainSingle();
        cells[0].Labels.Should().Contain("matched");
    }

    [Fact]
    public async Task Different_labels_in_interleave()
    {
        var rk = "fol-inter-labels";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "alpha", "a"));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "beta", "b"));

        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("alpha"), new RowFilter { ApplyLabelTransformer = "label-a" }),
            RowFilters.Chain(RowFilters.ColumnQualifierExact("beta"), new RowFilter { ApplyLabelTransformer = "label-b" }));
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(2);
        cells.Single(c => c.Value.ToStringUtf8() == "a").Labels.Should().Contain("label-a");
        cells.Single(c => c.Value.ToStringUtf8() == "b").Labels.Should().Contain("label-b");
    }

    [Fact]
    public async Task Label_on_nonexistent_row_returns_null()
    {
        var row = await Client.ReadRowAsync(TN, "fol-nonexist", new RowFilter { ApplyLabelTransformer = "ghost" });
        row.Should().BeNull();
    }

    [Fact]
    public async Task Label_with_condition_true_branch()
    {
        var rk = "fol-cond-true";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "status", "active"));

        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueExact("active")),
            trueFilter: new RowFilter { ApplyLabelTransformer = "is-active" },
            falseFilter: new RowFilter { ApplyLabelTransformer = "not-active" });
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
        var labels = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).SelectMany(c => c.Labels).Distinct().ToList();
        labels.Should().Contain("is-active");
        labels.Should().NotContain("not-active");
    }

    [Fact]
    public async Task Label_with_condition_false_branch()
    {
        var rk = "fol-cond-false";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "status", "inactive"));

        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueExact("active")),
            trueFilter: new RowFilter { ApplyLabelTransformer = "is-active" },
            falseFilter: new RowFilter { ApplyLabelTransformer = "not-active" });
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
        var labels = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).SelectMany(c => c.Labels).Distinct().ToList();
        labels.Should().Contain("not-active");
        labels.Should().NotContain("is-active");
    }

    [Fact]
    public async Task Label_applied_to_multiple_versions()
    {
        var rk = "fol-multi-ver";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "v2", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, rk, new RowFilter { ApplyLabelTransformer = "versioned" });
        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(2);
        cells.Should().AllSatisfy(c => c.Labels.Should().Contain("versioned"));
    }

    [Fact]
    public async Task Label_preserves_value()
    {
        var rk = "fol-pres-val";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "important"));

        var row = await Client.ReadRowAsync(TN, rk, new RowFilter { ApplyLabelTransformer = "labeled" });
        row.Should().NotBeNull();
        var cell = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Single();
        cell.Value.ToStringUtf8().Should().Be("important");
        cell.Labels.Should().Contain("labeled");
    }

    [Fact]
    public async Task Label_preserves_timestamp()
    {
        var rk = "fol-pres-ts";
        var ts = new BigtableVersion(5000);
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "data", ts));

        var row = await Client.ReadRowAsync(TN, rk, new RowFilter { ApplyLabelTransformer = "ts-label" });
        row.Should().NotBeNull();
        var cell = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Single();
        cell.TimestampMicros.Should().Be(ts.Micros);
    }

    [Fact]
    public async Task Label_applied_across_multiple_rows()
    {
        for (int i = 0; i < 3; i++)
            await Client.MutateRowAsync(TN, $"fol-multi-r{i}", Mutations.SetCell(CF, "c", $"v{i}"));

        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("fol-multi-r", "fol-multi-s")),
            new RowFilter { ApplyLabelTransformer = "batch-label" });

        rows.Should().HaveCount(3);
        foreach (var row in rows)
            row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
                .Should().AllSatisfy(c => c.Labels.Should().Contain("batch-label"));
    }

    [Fact]
    public async Task Label_with_strip_filter()
    {
        var rk = "fol-strip-label";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "data"));

        var filter = RowFilters.Chain(
            new RowFilter { ApplyLabelTransformer = "strip-me" },
            RowFilters.StripValueTransformer());
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
        var cell = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Single();
        cell.Value.IsEmpty.Should().BeTrue();
        // Labels may or may not survive through strip — just verify value is stripped
    }

    [Fact]
    public async Task Label_with_cells_per_column_limit()
    {
        var rk = "fol-limit-label";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "v2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "v3", new BigtableVersion(3000)));

        var filter = RowFilters.Chain(
            RowFilters.CellsPerColumnLimit(1),
            new RowFilter { ApplyLabelTransformer = "latest" });
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().ContainSingle();
        cells[0].Labels.Should().Contain("latest");
        cells[0].Value.ToStringUtf8().Should().Be("v3");
    }

    [Fact]
    public async Task Label_with_value_filter()
    {
        var rk = "fol-val-label";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a", "match"));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "b", "nomatch"));

        var filter = RowFilters.Chain(
            RowFilters.ValueExact("match"),
            new RowFilter { ApplyLabelTransformer = "found" });
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().ContainSingle();
        cells[0].Labels.Should().Contain("found");
    }

    [Fact]
    public async Task Label_with_special_characters()
    {
        var rk = "fol-special-label";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "val"));

        var row = await Client.ReadRowAsync(TN, rk,
            new RowFilter { ApplyLabelTransformer = "tag-dash-99" });
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .SelectMany(c => c.Labels).Should().Contain("tag-dash-99");
    }

    [Fact]
    public async Task Interleave_one_branch_labeled_other_not()
    {
        var rk = "fol-inter-partial";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "labeled", "val1"));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "unlabeled", "val2"));

        var filter = RowFilters.Interleave(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("labeled"), new RowFilter { ApplyLabelTransformer = "has-label" }),
            RowFilters.ColumnQualifierExact("unlabeled"));
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(2);
        cells.Single(c => c.Value.ToStringUtf8() == "val1").Labels.Should().Contain("has-label");
        cells.Single(c => c.Value.ToStringUtf8() == "val2").Labels.Should().BeEmpty();
    }
}

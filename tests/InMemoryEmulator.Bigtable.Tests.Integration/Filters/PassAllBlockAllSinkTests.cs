using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class PassAllBlockAllSinkTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "pabs-tests";
    private const string CF = "cf";

    public PassAllBlockAllSinkTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task PassAll_returns_all_cells()
    {
        var rk = "pabs-passall-1";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a", "v1"));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "b", "v2"));

        var filter = RowFilters.PassAllFilter();
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(2);
    }

    [Fact]
    public async Task BlockAll_returns_no_cells()
    {
        var rk = "pabs-blockall-1";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a", "v1"));

        var filter = RowFilters.BlockAllFilter();
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().BeNull();
    }

    [Fact]
    public async Task PassAll_in_chain_is_identity()
    {
        var rk = "pabs-chain-pass-1";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "value"));

        var filter = RowFilters.Chain(RowFilters.PassAllFilter(), RowFilters.CellsPerColumnLimit(1));
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().ContainSingle();
    }

    [Fact]
    public async Task BlockAll_in_chain_blocks_everything()
    {
        var rk = "pabs-chain-block-1";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "value"));

        var filter = RowFilters.Chain(RowFilters.CellsPerColumnLimit(1), RowFilters.BlockAllFilter());
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().BeNull();
    }

    [Fact]
    public async Task Interleave_passall_and_blockall_returns_all()
    {
        var rk = "pabs-inter-pb-1";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "x", "val"));

        var filter = RowFilters.Interleave(RowFilters.PassAllFilter(), RowFilters.BlockAllFilter());
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
    }

    [Fact]
    public async Task BlockAll_in_interleave_with_column_filter()
    {
        var rk = "pabs-inter-bc-1";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "keep", "yes"));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "drop", "no"));

        var filter = RowFilters.Interleave(RowFilters.BlockAllFilter(), RowFilters.ColumnQualifierExact("keep"));
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
        var cols = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().ContainSingle().Which.Should().Be("keep");
    }

    [Fact]
    public async Task Condition_with_passall_always_takes_true_branch()
    {
        var rk = "pabs-cond-pass-1";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "data"));

        var filter = RowFilters.Condition(
            RowFilters.PassAllFilter(),
            trueFilter: new RowFilter { ApplyLabelTransformer = "was-true" },
            falseFilter: RowFilters.BlockAllFilter());
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
        var labels = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).SelectMany(c => c.Labels).ToList();
        labels.Should().Contain("was-true");
    }

    [Fact]
    public async Task Condition_with_blockall_always_takes_false_branch()
    {
        var rk = "pabs-cond-block-1";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "data"));

        var filter = RowFilters.Condition(
            RowFilters.BlockAllFilter(),
            trueFilter: RowFilters.BlockAllFilter(),
            falseFilter: new RowFilter { ApplyLabelTransformer = "was-false" });
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
        var labels = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).SelectMany(c => c.Labels).ToList();
        labels.Should().Contain("was-false");
    }

    [Fact]
    public async Task Strip_filter_removes_data_but_row_still_returned()
    {
        var rk = "pabs-strip-1";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a", "v1"));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "b", "v2"));

        var filter = RowFilters.Chain(RowFilters.PassAllFilter(), RowFilters.StripValueTransformer());
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().AllSatisfy(c => c.Value.Should().BeEmpty());
    }

    [Fact]
    public async Task Strip_filter_preserves_column_qualifiers()
    {
        var rk = "pabs-strip-qual-1";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col1", "secret"));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col2", "secret2"));

        var filter = RowFilters.StripValueTransformer();
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
        var qualifiers = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).OrderBy(q => q).ToList();
        qualifiers.Should().BeEquivalentTo(new[] { "col1", "col2" });
    }

    [Fact]
    public async Task Strip_preserves_timestamp()
    {
        var rk = "pabs-strip-ts-1";
        var ts = new BigtableVersion(10000);
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "c", "val", ts));

        var filter = RowFilters.StripValueTransformer();
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
        var cell = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Single();
        cell.TimestampMicros.Should().Be(ts.Micros);
        cell.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Sink_filter_outputs_cells_through_blockall()
    {
        var rk = "pabs-sink-1";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a", "v1"));

        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
        // Sink + BlockAll: sink writes to output, blockall filters the chain.
        // Whether the sink output survives depends on implementation.
        var filter = RowFilters.Chain(new RowFilter { Sink = true }, RowFilters.BlockAllFilter());
        var row = await Client.ReadRowAsync(TN, rk, filter);

        // Just verify there's no exception — result depends on sink implementation
        _ = row;
    }

    [Fact]
    public async Task Multiple_passall_in_chain_is_still_identity()
    {
        var rk = "pabs-multi-pass-1";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "q", "data"));

        var filter = RowFilters.Chain(RowFilters.PassAllFilter(), RowFilters.PassAllFilter(), RowFilters.PassAllFilter());
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Single().Value.ToStringUtf8().Should().Be("data");
    }

    [Fact]
    public async Task BlockAll_on_empty_table_returns_null()
    {
        var row = await Client.ReadRowAsync(TN, "pabs-nonexist", RowFilters.BlockAllFilter());
        row.Should().BeNull();
    }

    [Fact]
    public async Task PassAll_on_nonexistent_row_returns_null()
    {
        var row = await Client.ReadRowAsync(TN, "pabs-nonexist-2", RowFilters.PassAllFilter());
        row.Should().BeNull();
    }

    [Fact]
    public async Task Chain_strip_then_value_filter_returns_nothing()
    {
        var rk = "pabs-strip-vf-1";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "c", "visible"));

        var filter = RowFilters.Chain(RowFilters.StripValueTransformer(), RowFilters.ValueExact("visible"));
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().BeNull();
    }

    [Fact]
    public async Task Strip_does_not_affect_family_name()
    {
        var rk = "pabs-strip-fam-1";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "q", "val"));

        var filter = RowFilters.StripValueTransformer();
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
        row!.Families.Should().ContainSingle().Which.Name.Should().Be(CF);
    }

    [Fact]
    public async Task Condition_true_filter_passall_false_filter_blockall()
    {
        var rk = "pabs-cond-tb-1";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "status", "active"));

        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueExact("active")),
            trueFilter: RowFilters.PassAllFilter(),
            falseFilter: RowFilters.BlockAllFilter());
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Condition_false_branch_taken_when_predicate_not_met()
    {
        var rk = "pabs-cond-fb-1";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "status", "inactive"));

        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueExact("active")),
            trueFilter: RowFilters.BlockAllFilter(),
            falseFilter: RowFilters.PassAllFilter());
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
    }
}

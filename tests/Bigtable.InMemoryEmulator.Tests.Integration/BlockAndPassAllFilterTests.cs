using System.Collections.Generic;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for BlockAllFilter and PassAllFilter behavior.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
///   "block_all_filter: Does not match any cells, regardless of input."
///   "pass_all_filter: Matches all cells, regardless of input."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class BlockAndPassAllFilterTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "block-pass";

    public BlockAndPassAllFilterTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task BlockAll_returns_null_row()
    {
        await Client.MutateRowAsync(TN, "bp-r1",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "bp-r1",
            RowFilters.BlockAllFilter());
        row.Should().BeNull();
    }

    [Fact]
    public async Task PassAll_returns_all_data()
    {
        await Client.MutateRowAsync(TN, "bp-r2",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "bp-r2",
            RowFilters.PassAllFilter());
        row!.Families[0].Columns.Should().HaveCount(2);
    }

    [Fact]
    public async Task BlockAll_returns_no_rows_in_scan()
    {
        await Client.MutateRowAsync(TN, "bp-r3a",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "bp-r3b",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN, filter: RowFilters.BlockAllFilter()))
            rows.Add(__row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task PassAll_returns_all_rows_in_scan()
    {
        await Client.MutateRowAsync(TN, "bp-r4a",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "bp-r4b",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN, filter: RowFilters.PassAllFilter()))
            rows.Add(__row);
        rows.Count.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Chain_block_after_pass_blocks()
    {
        await Client.MutateRowAsync(TN, "bp-r5",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "bp-r5",
            RowFilters.Chain(RowFilters.PassAllFilter(), RowFilters.BlockAllFilter()));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Chain_pass_after_block_blocks()
    {
        await Client.MutateRowAsync(TN, "bp-r6",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "bp-r6",
            RowFilters.Chain(RowFilters.BlockAllFilter(), RowFilters.PassAllFilter()));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Interleave_block_and_pass_returns_data()
    {
        await Client.MutateRowAsync(TN, "bp-r7",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "bp-r7",
            RowFilters.Interleave(RowFilters.BlockAllFilter(), RowFilters.PassAllFilter()));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Interleave_two_blocks_returns_null()
    {
        await Client.MutateRowAsync(TN, "bp-r8",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "bp-r8",
            RowFilters.Interleave(RowFilters.BlockAllFilter(), RowFilters.BlockAllFilter()));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Condition_with_block_as_predicate()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
        //   "If predicate_filter outputs any cells, then true_filter is evaluated."
        // BlockAll outputs no cells, so false_filter should be evaluated.
        await Client.MutateRowAsync(TN, "bp-r9",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "bp-r9",
            RowFilters.Condition(
                RowFilters.BlockAllFilter(),
                RowFilters.BlockAllFilter(),
                RowFilters.PassAllFilter()));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Condition_with_pass_as_predicate()
    {
        // PassAll outputs cells, so true_filter should be evaluated.
        await Client.MutateRowAsync(TN, "bp-r10",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "bp-r10",
            RowFilters.Condition(
                RowFilters.PassAllFilter(),
                RowFilters.PassAllFilter(),
                RowFilters.BlockAllFilter()));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task BlockAll_with_multiple_versions()
    {
        await Client.MutateRowAsync(TN, "bp-r11",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "bp-r11",
            RowFilters.BlockAllFilter());
        row.Should().BeNull();
    }

    [Fact]
    public async Task PassAll_preserves_all_versions()
    {
        await Client.MutateRowAsync(TN, "bp-r12",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "bp-r12",
            RowFilters.PassAllFilter());
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
    }
}

using System.Collections.Generic;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for CellsPerRowLimit and CellsPerRowOffset filter interactions.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
///   "cells_per_row_limit_filter: Matches only the first N cells of each row."
///   "cells_per_row_offset_filter: Skips the first N cells of each row, matching all subsequent cells."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CellsPerRowFilterTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "cpr-filt";

    public CellsPerRowFilterTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task CellsPerRowLimit_1()
    {
        await Client.MutateRowAsync(TN, "cpr-r1",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "cpr-r1",
            RowFilters.CellsPerRowLimit(1));
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().ContainSingle();
    }

    [Fact]
    public async Task CellsPerRowLimit_exceeding()
    {
        await Client.MutateRowAsync(TN, "cpr-r2",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "cpr-r2",
            RowFilters.CellsPerRowLimit(100));
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task CellsPerRowOffset_skips_first()
    {
        await Client.MutateRowAsync(TN, "cpr-r3",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "cpr-r3",
            RowFilters.CellsPerRowOffset(1));
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task CellsPerRowOffset_skip_all()
    {
        await Client.MutateRowAsync(TN, "cpr-r4",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "cpr-r4",
            RowFilters.CellsPerRowOffset(5));
        row.Should().BeNull();
    }

    [Fact]
    public async Task CellsPerRowOffset_0()
    {
        await Client.MutateRowAsync(TN, "cpr-r5",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "cpr-r5",
            RowFilters.CellsPerRowOffset(0));
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task Limit_includes_versions_as_cells()
    {
        await Client.MutateRowAsync(TN, "cpr-r6",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        var row = await Client.ReadRowAsync(TN, "cpr-r6",
            RowFilters.CellsPerRowLimit(2));
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task Offset_counts_versions_as_cells()
    {
        await Client.MutateRowAsync(TN, "cpr-r7",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        var row = await Client.ReadRowAsync(TN, "cpr-r7",
            RowFilters.CellsPerRowOffset(2));
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().ContainSingle();
    }

    [Fact]
    public async Task CellsPerRowLimit_per_row_not_global()
    {
        await Client.MutateRowAsync(TN, "cpr-r8a",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "cpr-r8b",
            Mutations.SetCell(CF, "a", "3", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "4", new BigtableVersion(1000)));
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN,
            RowSet.FromRowKeys("cpr-r8a", "cpr-r8b"),
            RowFilters.CellsPerRowLimit(1)))
            rows.Add(__row);
        rows.Should().HaveCount(2);
        foreach (var row in rows)
        {
            var cells = row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
            cells.Should().ContainSingle();
        }
    }

    [Fact]
    public async Task Limit_and_offset_in_chain()
    {
        await Client.MutateRowAsync(TN, "cpr-r9",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "d", "4", new BigtableVersion(1000)));
        // Skip 1, then limit 2
        var row = await Client.ReadRowAsync(TN, "cpr-r9",
            RowFilters.Chain(
                RowFilters.CellsPerRowOffset(1),
                RowFilters.CellsPerRowLimit(2)));
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task CellsPerColumnLimit_independent_of_row_limit()
    {
        await Client.MutateRowAsync(TN, "cpr-r10",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "b", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "cpr-r10",
            RowFilters.CellsPerColumnLimit(1));
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(2); // 1 per column × 2 columns
    }
}

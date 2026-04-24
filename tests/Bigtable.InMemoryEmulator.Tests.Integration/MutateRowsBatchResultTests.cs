using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for MutateRows batch operations focusing on entry-level results and ordering.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowsrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutateRowsBatchResultTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "mr-batch";
    private const string CF = "cf";

    public MutateRowsBatchResultTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null)
    {
        var list = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: rows, filter: filter))
            list.Add(row);
        return list;
    }

    #region Basic batch operations

    [Fact]
    public async Task Batch_single_entry()
    {
        var entries = new MutateRowsRequest.Types.Entry[]
        {
            Mutations.CreateEntry("mr-b-01",
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(rows: RowSet.FromRowKeys("mr-b-01"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Batch_multiple_entries()
    {
        var entries = Enumerable.Range(0, 10).Select(i =>
            Mutations.CreateEntry($"mr-b-02-{i:D2}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("mr-b-02-.*"));
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task Batch_50_entries()
    {
        var entries = Enumerable.Range(0, 50).Select(i =>
            Mutations.CreateEntry($"mr-b-03-{i:D3}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("mr-b-03-.*"));
        rows.Should().HaveCount(50);
    }

    #endregion

    #region Multiple mutations per entry

    [Fact]
    public async Task Entry_with_multiple_set_cells()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mr-b-04",
                Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(rows: RowSet.FromRowKeys("mr-b-04"));
        rows[0].Families[0].Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task Entry_with_set_and_delete()
    {
        // First write some data
        await Client.MutateRowAsync(TN, "mr-b-05",
            Mutations.SetCell(CF, "old", "data", new BigtableVersion(1000)));
        // Then batch with set + delete
        var entries = new[]
        {
            Mutations.CreateEntry("mr-b-05",
                Mutations.DeleteFromColumn(CF, "old"),
                Mutations.SetCell(CF, "new", "data", new BigtableVersion(2000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(rows: RowSet.FromRowKeys("mr-b-05"));
        var cols = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("new");
        cols.Should().NotContain("old");
    }

    #endregion

    #region Cross-family batch

    [Fact]
    public async Task Batch_cross_family_writes()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mr-b-06",
                Mutations.SetCell(CF, "c1", "v1", new BigtableVersion(1000)),
                Mutations.SetCell("cf2", "c2", "v2", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(rows: RowSet.FromRowKeys("mr-b-06"));
        rows[0].Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Batch_multiple_entries_cross_family()
    {
        var entries = Enumerable.Range(0, 5).Select(i =>
            Mutations.CreateEntry($"mr-b-07-{i}",
                Mutations.SetCell(CF, "c", $"cf1-{i}", new BigtableVersion(1000)),
                Mutations.SetCell("cf2", "c", $"cf2-{i}", new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        for (int i = 0; i < 5; i++)
        {
            var rows = await ReadAll(rows: RowSet.FromRowKeys($"mr-b-07-{i}"));
            rows[0].Families.Should().HaveCount(2);
        }
    }

    #endregion

    #region Same row in multiple entries

    [Fact]
    public async Task Same_row_two_entries_last_wins()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mr-b-08",
                Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000))),
            Mutations.CreateEntry("mr-b-08",
                Mutations.SetCell(CF, "c", "second", new BigtableVersion(2000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(rows: RowSet.FromRowKeys("mr-b-08"),
            filter: RowFilters.CellsPerColumnLimit(1));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task Same_row_different_columns()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mr-b-09",
                Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000))),
            Mutations.CreateEntry("mr-b-09",
                Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(rows: RowSet.FromRowKeys("mr-b-09"));
        rows[0].Families[0].Columns.Should().HaveCount(2);
    }

    #endregion

    #region Delete operations in batch

    [Fact]
    public async Task Batch_delete_from_row()
    {
        await Client.MutateRowAsync(TN, "mr-b-10",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var entries = new[]
        {
            Mutations.CreateEntry("mr-b-10", Mutations.DeleteFromRow())
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(rows: RowSet.FromRowKeys("mr-b-10"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Batch_delete_from_family()
    {
        await Client.MutateRowAsync(TN, "mr-b-11",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "v", new BigtableVersion(1000)));
        var entries = new[]
        {
            Mutations.CreateEntry("mr-b-11", Mutations.DeleteFromFamily(CF))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(rows: RowSet.FromRowKeys("mr-b-11"));
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be("cf2");
    }

    [Fact]
    public async Task Batch_mixed_write_and_delete_entries()
    {
        await Client.MutateRowAsync(TN, "mr-b-12-del",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var entries = new[]
        {
            Mutations.CreateEntry("mr-b-12-del", Mutations.DeleteFromRow()),
            Mutations.CreateEntry("mr-b-12-new",
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var deleted = await ReadAll(rows: RowSet.FromRowKeys("mr-b-12-del"));
        deleted.Should().BeEmpty();
        var created = await ReadAll(rows: RowSet.FromRowKeys("mr-b-12-new"));
        created.Should().ContainSingle();
    }

    #endregion

    #region Sequential batches

    [Fact]
    public async Task Sequential_batches_accumulate_data()
    {
        for (int batch = 0; batch < 5; batch++)
        {
            var entries = Enumerable.Range(0, 3).Select(i =>
                Mutations.CreateEntry($"mr-b-13-{batch}-{i}",
                    Mutations.SetCell(CF, "c", $"b{batch}i{i}", new BigtableVersion(1000)))
            ).ToArray();
            await Client.MutateRowsAsync(TN, entries);
        }
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("mr-b-13-.*"));
        rows.Should().HaveCount(15);
    }

    [Fact]
    public async Task Sequential_batch_overwrite()
    {
        var entries1 = new[]
        {
            Mutations.CreateEntry("mr-b-14",
                Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries1);

        var entries2 = new[]
        {
            Mutations.CreateEntry("mr-b-14",
                Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)))
        };
        await Client.MutateRowsAsync(TN, entries2);

        var rows = await ReadAll(rows: RowSet.FromRowKeys("mr-b-14"),
            filter: RowFilters.CellsPerColumnLimit(1));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v2");
    }

    #endregion

    #region Batch with versions

    [Fact]
    public async Task Batch_writes_multiple_versions()
    {
        var entries = Enumerable.Range(1, 5).Select(v =>
            Mutations.CreateEntry("mr-b-15",
                Mutations.SetCell(CF, "c", $"v{v}", new BigtableVersion(v)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(rows: RowSet.FromRowKeys("mr-b-15"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(5);
    }

    [Fact]
    public async Task Batch_with_same_version_overwrites()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mr-b-16",
                Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000))),
            Mutations.CreateEntry("mr-b-16",
                Mutations.SetCell(CF, "c", "second", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(rows: RowSet.FromRowKeys("mr-b-16"),
            filter: RowFilters.CellsPerColumnLimit(1));
        // Same version overwrites => should have the last value
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("second");
    }

    #endregion

    #region Large values

    [Fact]
    public async Task Batch_with_large_values()
    {
        var largeValue = new string('X', 10_000);
        var entries = Enumerable.Range(0, 5).Select(i =>
            Mutations.CreateEntry($"mr-b-17-{i}",
                Mutations.SetCell(CF, "c", largeValue, new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(filter: RowFilters.RowKeyRegex("mr-b-17-.*"));
        rows.Should().HaveCount(5);
        foreach (var row in rows)
            row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Length.Should().Be(10_000);
    }

    #endregion
}

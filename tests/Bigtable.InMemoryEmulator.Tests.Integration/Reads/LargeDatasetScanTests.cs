using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class LargeDatasetScanTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "lds-tests";
    private const string CF = "cf";

    public LargeDatasetScanTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null, long? rowsLimit = null)
    {
        var list = new List<Row>();
        if (rowsLimit.HasValue)
        {
            var request = new ReadRowsRequest { TableNameAsTableName = TN, RowsLimit = rowsLimit.Value };
            if (filter != null) request.Filter = filter;
            // For rowsLimit with RowSet, build manually
            if (rows != null)
            {
                // Use ReadRows overload with rows and iterate with limit checked externally
                await foreach (var r in Client.ReadRows(TN, rows: rows, filter: filter))
                {
                    list.Add(r);
                    if (list.Count >= rowsLimit.Value) break;
                }
                return list;
            }
            await foreach (var r in Client.ReadRows(request))
                list.Add(r);
        }
        else
        {
            await foreach (var r in Client.ReadRows(TN, rows: rows, filter: filter))
                list.Add(r);
        }
        return list;
    }

    [Fact]
    public async Task Write_and_read_100_rows()
    {
        var entries = Enumerable.Range(0, 100)
            .Select(i => Mutations.CreateEntry($"lds-100r-{i:D4}", Mutations.SetCell(CF, "c", $"v{i}")))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);

        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("lds-100r-", "lds-100s-")));
        rows.Should().HaveCount(100);
    }

    [Fact]
    public async Task Row_with_50_columns()
    {
        var rk = "lds-50cols";
        var mutations = Enumerable.Range(0, 50)
            .Select(i => Mutations.SetCell(CF, $"col{i:D3}", $"val{i}"))
            .ToArray();
        await Client.MutateRowAsync(TN, rk, mutations);

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(50);
    }

    [Fact]
    public async Task Column_with_20_versions()
    {
        var rk = "lds-20ver";
        for (int i = 1; i <= 20; i++)
            await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", $"v{i}", new BigtableVersion(i * 1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(20);
    }

    [Fact]
    public async Task CellsPerColumnLimit_on_many_versions()
    {
        var rk = "lds-limit-ver";
        for (int i = 1; i <= 20; i++)
            await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", $"v{i}", new BigtableVersion(i * 1000)));

        var row = await Client.ReadRowAsync(TN, rk, RowFilters.CellsPerColumnLimit(5));
        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(5);
        cells.Select(c => c.Value.ToStringUtf8()).Should().BeEquivalentTo(
            new[] { "v20", "v19", "v18", "v17", "v16" });
    }

    [Fact]
    public async Task Batch_mutate_50_rows()
    {
        var entries = Enumerable.Range(0, 50)
            .Select(i => Mutations.CreateEntry($"lds-batch50-{i:D3}",
                Mutations.SetCell(CF, "a", $"va{i}"),
                Mutations.SetCell(CF, "b", $"vb{i}")))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);

        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("lds-batch50-", "lds-batch51-")));
        rows.Should().HaveCount(50);
    }

    [Fact]
    public async Task Read_with_limit_on_large_dataset()
    {
        var entries = Enumerable.Range(0, 50)
            .Select(i => Mutations.CreateEntry($"lds-limit-{i:D3}", Mutations.SetCell(CF, "c", $"v{i}")))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);

        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("lds-limit-", "lds-limiu-")),
            rowsLimit: 10);
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task Many_columns_two_families()
    {
        var rk = "lds-2fam-cols";
        var mutations = new List<Mutation>();
        for (int i = 0; i < 25; i++)
        {
            mutations.Add(Mutations.SetCell(CF, $"col{i:D3}", $"cf1-{i}"));
            mutations.Add(Mutations.SetCell("cf2", $"col{i:D3}", $"cf2-{i}"));
        }
        await Client.MutateRowAsync(TN, rk, mutations.ToArray());

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(50);
    }

    [Fact]
    public async Task Filter_on_large_row_set()
    {
        var entries = Enumerable.Range(0, 30)
            .Select(i => Mutations.CreateEntry($"lds-filt-{i:D3}",
                Mutations.SetCell(CF, "type", i % 2 == 0 ? "even" : "odd")))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);

        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("lds-filt-", "lds-filu-")),
            RowFilters.ValueExact("even"));
        rows.Should().HaveCount(15);
    }

    [Fact]
    public async Task Value_with_1KB_data()
    {
        var rk = "lds-1kb";
        var data = new string('X', 1024);
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "big", data));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Single()
            .Value.ToStringUtf8().Length.Should().Be(1024);
    }

    [Fact]
    public async Task Value_with_5KB_data()
    {
        var rk = "lds-5kb";
        var data = new string('Y', 5120);
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "big", data));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Single()
            .Value.ToStringUtf8().Length.Should().Be(5120);
    }

    [Fact]
    public async Task Multiple_rows_with_multiple_versions()
    {
        for (int r = 0; r < 10; r++)
            for (int v = 1; v <= 5; v++)
                await Client.MutateRowAsync(TN, $"lds-mv-{r:D2}",
                    Mutations.SetCell(CF, "col", $"r{r}v{v}", new BigtableVersion(v * 1000)));

        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("lds-mv-", "lds-mw-")),
            RowFilters.CellsPerColumnLimit(2));
        rows.Should().HaveCount(10);
        foreach (var row in rows)
            row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(2);
    }

    [Fact]
    public async Task ReadModifyWrite_increment_to_large_value()
    {
        var rk = "lds-rmw-large";
        for (int i = 0; i < 100; i++)
            await Client.ReadModifyWriteRowAsync(TN, rk, ReadModifyWriteRules.Increment(CF, "counter", 1));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
    var cell = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).First();
    System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(cell.Value.Span).Should().Be(100);
    }

    [Fact]
    public async Task ReadModifyWrite_append_builds_up_string()
    {
        var rk = "lds-rmw-append";
        for (int i = 0; i < 20; i++)
            await Client.ReadModifyWriteRowAsync(TN, rk, ReadModifyWriteRules.Append(CF, "log", $"entry{i};"));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        var val = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).First().Value.ToStringUtf8();
        for (int i = 0; i < 20; i++)
            val.Should().Contain($"entry{i};");
    }

    [Fact]
    public async Task Delete_half_the_rows()
    {
        var entries = Enumerable.Range(0, 20)
            .Select(i => Mutations.CreateEntry($"lds-delhalf-{i:D3}", Mutations.SetCell(CF, "c", $"v{i}")))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);

        var deleteEntries = Enumerable.Range(0, 20).Where(i => i % 2 == 0)
            .Select(i => Mutations.CreateEntry($"lds-delhalf-{i:D3}", Mutations.DeleteFromRow()))
            .ToArray();
        await Client.MutateRowsAsync(TN, deleteEntries);

        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("lds-delhalf-", "lds-delhalg-")));
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task Scan_returns_results_in_sorted_order()
    {
        var indices = Enumerable.Range(0, 30).OrderBy(_ => Guid.NewGuid()).ToList();
        foreach (var i in indices)
            await Client.MutateRowAsync(TN, $"lds-sorted-{i:D3}", Mutations.SetCell(CF, "c", "v"));

        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("lds-sorted-", "lds-sortee-")));
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Wide_row_with_many_families_and_columns()
    {
        var rk = "lds-wide";
        var mutations = new List<Mutation>();
        for (int c = 0; c < 20; c++)
            mutations.Add(Mutations.SetCell(CF, $"wide{c:D3}", $"val{c}"));
        for (int c = 0; c < 20; c++)
            mutations.Add(Mutations.SetCell("cf2", $"wide{c:D3}", $"val{c}"));
        await Client.MutateRowAsync(TN, rk, mutations.ToArray());

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(40);
    }

    [Fact]
    public async Task Row_key_pattern_scan_across_100_rows()
    {
        var entries = Enumerable.Range(0, 100)
            .Select(i => Mutations.CreateEntry($"lds-scan-{i:D4}", Mutations.SetCell(CF, "c", "v")))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);

        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("lds-scan-0025", "lds-scan-0075")));
        rows.Should().HaveCount(50);
    }
}

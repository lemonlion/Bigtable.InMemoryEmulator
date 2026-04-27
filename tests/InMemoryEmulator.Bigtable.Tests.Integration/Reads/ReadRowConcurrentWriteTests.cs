using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowConcurrentWriteTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "rr-conc";
    private const string CF = "cf";

    public ReadRowConcurrentWriteTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Concurrent_writes_to_different_rows()
    {
        var tasks = Enumerable.Range(0, 20).Select(i =>
            Client.MutateRowAsync(TN, $"cw-{i:D2}", Mutations.SetCell(CF, "c", $"v{i}")));
        await Task.WhenAll(tasks);
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN)) rows.Add(r);
        rows.Where(r => r.Key.ToStringUtf8().StartsWith("cw-")).Should().HaveCount(20);
    }

    [Fact]
    public async Task Concurrent_writes_same_row_different_columns()
    {
        var tasks = Enumerable.Range(0, 10).Select(i =>
            Client.MutateRowAsync(TN, "shared", Mutations.SetCell(CF, $"c{i}", $"v{i}")));
        await Task.WhenAll(tasks);
        var row = await Client.ReadRowAsync(TN, "shared");
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(10);
    }

    [Fact]
    public async Task Concurrent_batch_writes()
    {
        var t1 = Client.MutateRowsAsync(TN, Enumerable.Range(0, 10)
            .Select(i => Mutations.CreateEntry($"cb1-{i}", Mutations.SetCell(CF, "c", "v"))).ToArray());
        var t2 = Client.MutateRowsAsync(TN, Enumerable.Range(0, 10)
            .Select(i => Mutations.CreateEntry($"cb2-{i}", Mutations.SetCell(CF, "c", "v"))).ToArray());
        await Task.WhenAll(t1, t2);
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, RowSet.FromRowRanges(RowRange.ClosedOpen("cb", "cc"))))
            rows.Add(r);
        rows.Should().HaveCount(20);
    }

    [Fact]
    public async Task Concurrent_rmw_on_different_rows()
    {
        var tasks = Enumerable.Range(0, 5).Select(i =>
            Client.ReadModifyWriteRowAsync(TN, $"rmw-{i}",
                ReadModifyWriteRules.Append(CF, "c", $"val-{i}")));
        await Task.WhenAll(tasks);
        for (int i = 0; i < 5; i++)
        {
            var row = await Client.ReadRowAsync(TN, $"rmw-{i}");
            row.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Concurrent_check_and_mutate_different_rows()
    {
        // Pre-create rows
        for (int i = 0; i < 5; i++)
            await Client.MutateRowAsync(TN, $"cam-{i}", Mutations.SetCell(CF, "c", "initial", new BigtableVersion(1000)));
        var tasks = Enumerable.Range(0, 5).Select(i =>
            Client.CheckAndMutateRowAsync(TN, $"cam-{i}",
                RowFilters.PassAllFilter(),
                trueMutations: new[] { Mutations.SetCell(CF, "status", "checked") },
                falseMutations: null));
        var results = await Task.WhenAll(tasks);
        results.Should().OnlyContain(r => r.PredicateMatched);
    }

    [Fact]
    public async Task Write_then_immediate_read()
    {
        await Client.MutateRowAsync(TN, "wir", Mutations.SetCell(CF, "c", "val"));
        var row = await Client.ReadRowAsync(TN, "wir");
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("val");
    }

    [Fact]
    public async Task Delete_then_immediate_read()
    {
        await Client.MutateRowAsync(TN, "dir", Mutations.SetCell(CF, "c", "val"));
        await Client.MutateRowAsync(TN, "dir", Mutations.DeleteFromRow());
        var row = await Client.ReadRowAsync(TN, "dir");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Concurrent_reads_same_row()
    {
        await Client.MutateRowAsync(TN, "multi-read", Mutations.SetCell(CF, "c", "val"));
        var tasks = Enumerable.Range(0, 10).Select(_ =>
            Client.ReadRowAsync(TN, "multi-read"));
        var results = await Task.WhenAll(tasks);
        results.Should().OnlyContain(r => r != null);
        results.Should().OnlyContain(r => r!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8() == "val");
    }
}

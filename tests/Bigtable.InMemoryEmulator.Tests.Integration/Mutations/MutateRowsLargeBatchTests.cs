using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutateRowsLargeBatchTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "mut-large";
    private const string CF = "cf";

    public MutateRowsLargeBatchTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Batch_100_rows()
    {
        var entries = Enumerable.Range(0, 100)
            .Select(i => Mutations.CreateEntry($"row-{i:D3}", Mutations.SetCell(CF, "c", $"v{i}")))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN)) rows.Add(r);
        rows.Should().HaveCount(100);
    }

    [Fact]
    public async Task Batch_rows_sorted_on_read()
    {
        var entries = Enumerable.Range(0, 50)
            .Select(i => Mutations.CreateEntry($"z{i:D2}", Mutations.SetCell(CF, "c", $"v{i}")))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, RowSet.FromRowRanges(RowRange.ClosedOpen("z00", "z99"))))
            rows.Add(r);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Batch_with_multiple_mutations_per_entry()
    {
        var entries = Enumerable.Range(0, 20)
            .Select(i => Mutations.CreateEntry($"multi-{i:D2}",
                Mutations.SetCell(CF, "a", $"va-{i}"),
                Mutations.SetCell(CF, "b", $"vb-{i}"),
                Mutations.SetCell(CF, "c", $"vc-{i}")))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "multi-10");
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(3);
    }

    [Fact]
    public async Task Batch_overwrite_existing_rows()
    {
        await Client.MutateRowAsync(TN, "ow-00", Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));
        var entries = new[]
        {
            Mutations.CreateEntry("ow-00", Mutations.SetCell(CF, "c", "new", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "ow-00");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Batch_delete_all()
    {
        // Create then delete in batch
        for (int i = 0; i < 10; i++)
            await Client.MutateRowAsync(TN, $"del-{i}", Mutations.SetCell(CF, "c", "v"));
        var entries = Enumerable.Range(0, 10)
            .Select(i => Mutations.CreateEntry($"del-{i}", Mutations.DeleteFromRow()))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);
        for (int i = 0; i < 10; i++)
        {
            var row = await Client.ReadRowAsync(TN, $"del-{i}");
            row.Should().BeNull();
        }
    }

    [Fact]
    public async Task Batch_mixed_operations()
    {
        await Client.MutateRowAsync(TN, "mx-del", Mutations.SetCell(CF, "c", "to-delete"));
        var entries = new[]
        {
            Mutations.CreateEntry("mx-new", Mutations.SetCell(CF, "c", "created")),
            Mutations.CreateEntry("mx-del", Mutations.DeleteFromRow())
        };
        await Client.MutateRowsAsync(TN, entries);
        (await Client.ReadRowAsync(TN, "mx-new")).Should().NotBeNull();
        (await Client.ReadRowAsync(TN, "mx-del")).Should().BeNull();
    }

    [Fact]
    public async Task Batch_same_row_multiple_entries()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("dup", Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000))),
            Mutations.CreateEntry("dup", Mutations.SetCell(CF, "c", "second", new BigtableVersion(2000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "dup");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
    }
}

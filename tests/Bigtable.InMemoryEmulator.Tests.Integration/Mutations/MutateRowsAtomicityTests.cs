using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutateRowsAtomicityTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "mut-atom";
    private const string CF = "cf";

    public MutateRowsAtomicityTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Multiple_mutations_same_row_applied_in_order()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowrequest
        // Mutations applied atomically and in order
        await Client.MutateRowAsync(TN, "r1",
            Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "second", new BigtableVersion(1000))); // same ts overwrites
        var row = await Client.ReadRowAsync(TN, "r1");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task Set_then_delete_leaves_row_empty()
    {
        await Client.MutateRowAsync(TN, "r2",
            Mutations.SetCell(CF, "c", "val"),
            Mutations.DeleteFromRow());
        var row = await Client.ReadRowAsync(TN, "r2");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Delete_then_set_leaves_value()
    {
        await Client.MutateRowAsync(TN, "r3",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "r3",
            Mutations.DeleteFromRow(),
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "r3");
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Batch_mutate_multiple_rows()
    {
        var entries = Enumerable.Range(0, 10)
            .Select(i => Mutations.CreateEntry($"batch-{i:D2}", Mutations.SetCell(CF, "c", $"v{i}")))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, RowSet.FromRowRanges(RowRange.ClosedOpen("batch-00", "batch-99"))))
            rows.Add(r);
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task Batch_with_deletes()
    {
        await Client.MutateRowAsync(TN, "del-1", Mutations.SetCell(CF, "c", "v1"));
        await Client.MutateRowAsync(TN, "del-2", Mutations.SetCell(CF, "c", "v2"));
        var entries = new[]
        {
            Mutations.CreateEntry("del-1", Mutations.DeleteFromRow()),
            Mutations.CreateEntry("del-2", Mutations.DeleteFromRow())
        };
        await Client.MutateRowsAsync(TN, entries);
        var r1 = await Client.ReadRowAsync(TN, "del-1");
        var r2 = await Client.ReadRowAsync(TN, "del-2");
        r1.Should().BeNull();
        r2.Should().BeNull();
    }

    [Fact]
    public async Task Batch_mixed_set_and_delete()
    {
        await Client.MutateRowAsync(TN, "mix-1", Mutations.SetCell(CF, "c", "old"));
        var entries = new[]
        {
            Mutations.CreateEntry("mix-1", Mutations.DeleteFromRow()),
            Mutations.CreateEntry("mix-2", Mutations.SetCell(CF, "c", "new"))
        };
        await Client.MutateRowsAsync(TN, entries);
        (await Client.ReadRowAsync(TN, "mix-1")).Should().BeNull();
        (await Client.ReadRowAsync(TN, "mix-2")).Should().NotBeNull();
    }

    [Fact]
    public async Task Multiple_set_cells_different_columns()
    {
        await Client.MutateRowAsync(TN, "mc",
            Mutations.SetCell(CF, "a", "va"),
            Mutations.SetCell(CF, "b", "vb"),
            Mutations.SetCell(CF, "c", "vc"));
        var row = await Client.ReadRowAsync(TN, "mc");
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(3);
    }

    [Fact]
    public async Task Multiple_set_cells_different_versions()
    {
        await Client.MutateRowAsync(TN, "mv",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        var row = await Client.ReadRowAsync(TN, "mv");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task Delete_family_then_set_in_same_family()
    {
        await Client.MutateRowAsync(TN, "df",
            Mutations.SetCell(CF, "a", "old"),
            Mutations.SetCell(CF, "b", "old"));
        await Client.MutateRowAsync(TN, "df",
            Mutations.DeleteFromFamily(CF),
            Mutations.SetCell(CF, "c", "new"));
        var row = await Client.ReadRowAsync(TN, "df");
        row.Should().NotBeNull();
        var cols = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().ContainSingle().Which.Should().Be("c");
    }

    [Fact]
    public async Task Single_entry_batch()
    {
        var entries = new[] { Mutations.CreateEntry("single", Mutations.SetCell(CF, "c", "v")) };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "single");
        row.Should().NotBeNull();
    }
}

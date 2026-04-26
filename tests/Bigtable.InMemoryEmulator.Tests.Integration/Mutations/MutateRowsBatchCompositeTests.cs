using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutateRowsBatchCompositeTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "mrbc-tests";
    private const string CF = "cf";

    public MutateRowsBatchCompositeTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Single_entry()
    {
        await Client.MutateRowsAsync(TN, new[] { Mutations.CreateEntry("mrbc-single", Mutations.SetCell(CF, "col", "val")) });
        var row = await Client.ReadRowAsync(TN, "mrbc-single");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Fifty_entries()
    {
        var entries = Enumerable.Range(0, 50)
            .Select(i => Mutations.CreateEntry($"mrbc-fifty-{i:D4}", Mutations.SetCell(CF, "col", $"val-{i}")))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = new List<Row>();
        var rowSet = RowSet.FromRowKeys(entries.Select(e => (BigtableByteString)e.RowKey).ToArray());
        await foreach (var r in Client.ReadRows(TN, rows: rowSet))
            rows.Add(r);
        rows.Should().HaveCount(50);
    }

    [Fact]
    public async Task Multiple_mutations_per_entry()
    {
        await Client.MutateRowsAsync(TN, new[] {
            Mutations.CreateEntry("mrbc-mmut", Mutations.SetCell(CF, "a", "1"), Mutations.SetCell(CF, "b", "2"), Mutations.SetCell(CF, "c", "3"))
        });
        var row = await Client.ReadRowAsync(TN, "mrbc-mmut");
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(3);
    }

    [Fact]
    public async Task Delete_mutation_in_batch()
    {
        await Client.MutateRowAsync(TN, "mrbc-del", Mutations.SetCell(CF, "col", "old"));
        await Client.MutateRowsAsync(TN, new[] { Mutations.CreateEntry("mrbc-del", Mutations.DeleteFromRow()) });
        var row = await Client.ReadRowAsync(TN, "mrbc-del");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Mixed_set_and_delete()
    {
        await Client.MutateRowAsync(TN, "mrbc-mix", Mutations.SetCell(CF, "a", "1"), Mutations.SetCell(CF, "b", "2"));
        await Client.MutateRowsAsync(TN, new[] {
            Mutations.CreateEntry("mrbc-mix", Mutations.DeleteFromColumn(CF, "a"), Mutations.SetCell(CF, "c", "3"))
        });
        var row = await Client.ReadRowAsync(TN, "mrbc-mix");
        var cols = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).OrderBy(c => c).ToList();
        cols.Should().BeEquivalentTo(new[] { "b", "c" });
    }

    [Fact]
    public async Task Explicit_timestamps()
    {
        await Client.MutateRowsAsync(TN, new[] {
            Mutations.CreateEntry("mrbc-ts",
                Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "col", "v2", new BigtableVersion(2000)))
        });
        var row = await Client.ReadRowAsync(TN, "mrbc-ts");
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(2);
    }

    [Fact]
    public async Task Overwrite_same_row_different_batches()
    {
        await Client.MutateRowsAsync(TN, new[] { Mutations.CreateEntry("mrbc-ow", Mutations.SetCell(CF, "col", "first")) });
        await Client.MutateRowsAsync(TN, new[] { Mutations.CreateEntry("mrbc-ow", Mutations.SetCell(CF, "col", "second")) });
        var row = await Client.ReadRowAsync(TN, "mrbc-ow", RowFilters.CellsPerColumnLimit(1));
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Single().Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task Delete_from_family_in_batch()
    {
        await Client.MutateRowAsync(TN, "mrbc-delfam", Mutations.SetCell(CF, "a", "1"), Mutations.SetCell(CF, "b", "2"));
        await Client.MutateRowsAsync(TN, new[] { Mutations.CreateEntry("mrbc-delfam", Mutations.DeleteFromFamily(CF)) });
        var row = await Client.ReadRowAsync(TN, "mrbc-delfam");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Create_multiple_rows_at_once()
    {
        await Client.MutateRowsAsync(TN, new[] {
            Mutations.CreateEntry("mrbc-cr-1", Mutations.SetCell(CF, "col", "a")),
            Mutations.CreateEntry("mrbc-cr-2", Mutations.SetCell(CF, "col", "b")),
            Mutations.CreateEntry("mrbc-cr-3", Mutations.SetCell(CF, "col", "c")),
        });
        for (int i = 1; i <= 3; i++)
        {
            var row = await Client.ReadRowAsync(TN, $"mrbc-cr-{i}");
            row.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Delete_column_version_range_in_batch()
    {
        var rk = "mrbc-dv";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "col", "v3", new BigtableVersion(3000)));
        await Client.MutateRowsAsync(TN, new[] {
            Mutations.CreateEntry(rk, Mutations.DeleteFromColumn(CF, "col", new BigtableVersionRange(new BigtableVersion(1000), new BigtableVersion(2000))))
        });
        var row = await Client.ReadRowAsync(TN, rk);
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(2);
        cells.Select(c => c.Value.ToStringUtf8()).Should().BeEquivalentTo(new[] { "v2", "v3" });
    }

    [Fact]
    public async Task Idempotent_same_timestamp()
    {
        var entries = new[] { Mutations.CreateEntry("mrbc-idem", Mutations.SetCell(CF, "col", "val", new BigtableVersion(1000))) };
        await Client.MutateRowsAsync(TN, entries);
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "mrbc-idem");
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().ContainSingle();
    }
}

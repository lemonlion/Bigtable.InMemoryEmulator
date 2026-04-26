using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutateRowsBatchValidationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "mr-batch-val";
    private const string CF = "cf";

    public MutateRowsBatchValidationTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Single_entry_batch()
    {
        var entries = new[] { Mutations.CreateEntry("r1", Mutations.SetCell(CF, "c", "v")) };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "r1");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Multiple_entries_different_rows()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("b-r1", Mutations.SetCell(CF, "c", "1")),
            Mutations.CreateEntry("b-r2", Mutations.SetCell(CF, "c", "2")),
            Mutations.CreateEntry("b-r3", Mutations.SetCell(CF, "c", "3")),
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: RowSet.FromRowKeys("b-r1", "b-r2", "b-r3")))
            rows.Add(r);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Batch_with_deletes()
    {
        await Client.MutateRowAsync(TN, "del1", Mutations.SetCell(CF, "c", "v"));
        await Client.MutateRowAsync(TN, "del2", Mutations.SetCell(CF, "c", "v"));
        var entries = new[]
        {
            Mutations.CreateEntry("del1", Mutations.DeleteFromRow()),
            Mutations.CreateEntry("del2", Mutations.DeleteFromRow()),
        };
        await Client.MutateRowsAsync(TN, entries);
        (await Client.ReadRowAsync(TN, "del1")).Should().BeNull();
        (await Client.ReadRowAsync(TN, "del2")).Should().BeNull();
    }

    [Fact]
    public async Task Batch_with_mixed_set_and_delete()
    {
        await Client.MutateRowAsync(TN, "mix-del", Mutations.SetCell(CF, "c", "v"));
        var entries = new[]
        {
            Mutations.CreateEntry("mix-set", Mutations.SetCell(CF, "c", "new")),
            Mutations.CreateEntry("mix-del", Mutations.DeleteFromRow()),
        };
        await Client.MutateRowsAsync(TN, entries);
        (await Client.ReadRowAsync(TN, "mix-set")).Should().NotBeNull();
        (await Client.ReadRowAsync(TN, "mix-del")).Should().BeNull();
    }

    [Fact]
    public async Task Batch_multiple_mutations_per_entry()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("multi",
                Mutations.SetCell(CF, "a", "1"),
                Mutations.SetCell(CF, "b", "2"),
                Mutations.SetCell(CF, "c", "3")),
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "multi");
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(3);
    }

    [Fact]
    public async Task Batch_same_row_key_overwrites()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("dup-key", Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000))),
            Mutations.CreateEntry("dup-key", Mutations.SetCell(CF, "c", "second", new BigtableVersion(1000))),
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "dup-key");
        row.Should().NotBeNull();
        // Both applied; same timestamp = overwrite
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task Batch_10_entries()
    {
        var entries = Enumerable.Range(0, 10)
            .Select(i => Mutations.CreateEntry($"ten-{i:D2}", Mutations.SetCell(CF, "c", $"{i}")))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex("ten-.*")))
            rows.Add(r);
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task Batch_with_version()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("ver-r", Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000))),
        };
        await Client.MutateRowsAsync(TN, entries);
        var entries2 = new[]
        {
            Mutations.CreateEntry("ver-r", Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000))),
        };
        await Client.MutateRowsAsync(TN, entries2);
        var row = await Client.ReadRowAsync(TN, "ver-r");
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(2);
    }

    [Fact]
    public async Task Batch_then_read_each_row()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("br-1", Mutations.SetCell(CF, "c", "a")),
            Mutations.CreateEntry("br-2", Mutations.SetCell(CF, "c", "b")),
            Mutations.CreateEntry("br-3", Mutations.SetCell(CF, "c", "c")),
        };
        await Client.MutateRowsAsync(TN, entries);
        for (int i = 1; i <= 3; i++)
        {
            var row = await Client.ReadRowAsync(TN, $"br-{i}");
            row.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Batch_delete_column()
    {
        await Client.MutateRowAsync(TN, "dc-r",
            Mutations.SetCell(CF, "a", "1"),
            Mutations.SetCell(CF, "b", "2"));
        var entries = new[]
        {
            Mutations.CreateEntry("dc-r", Mutations.DeleteFromColumn(CF, "a")),
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "dc-r");
        row!.Families.SelectMany(f => f.Columns).Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("b");
    }

    [Fact]
    public async Task Batch_50_entries()
    {
        var entries = Enumerable.Range(0, 50)
            .Select(i => Mutations.CreateEntry($"fifty-{i:D3}", Mutations.SetCell(CF, "c", $"{i}")))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex("fifty-.*")))
            rows.Add(r);
        rows.Should().HaveCount(50);
    }

    [Fact]
    public async Task Batch_delete_from_family()
    {
        await Client.MutateRowAsync(TN, "df-r",
            Mutations.SetCell(CF, "a", "1"),
            Mutations.SetCell(CF, "b", "2"));
        var entries = new[]
        {
            Mutations.CreateEntry("df-r", Mutations.DeleteFromFamily(CF)),
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "df-r");
        row.Should().BeNull();
    }
}

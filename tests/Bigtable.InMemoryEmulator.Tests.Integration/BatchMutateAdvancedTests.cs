using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for MutateRows (batch) advanced entry-level semantics.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowsrequest
///   "Mutates multiple rows in a batch. Each individual row is mutated atomically..."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class BatchMutateAdvancedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "batch-adv2";

    public BatchMutateAdvancedTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task Batch_single_entry()
    {
        var entries = new[] { Mutations.CreateEntry("ba2-r1", Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000))) };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "ba2-r1");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Batch_multiple_entries()
    {
        var entries = Enumerable.Range(0, 10)
            .Select(i => Mutations.CreateEntry($"ba2-m{i}", Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000))))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);
        for (int i = 0; i < 10; i++)
        {
            var row = await Client.ReadRowAsync(TN, $"ba2-m{i}");
            row.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Batch_with_deletes()
    {
        await Client.MutateRowAsync(TN, "ba2-del",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var entries = new[] { Mutations.CreateEntry("ba2-del", Mutations.DeleteFromRow()) };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "ba2-del");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Batch_mixed_create_and_delete()
    {
        await Client.MutateRowAsync(TN, "ba2-mix-del",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));
        var entries = new[]
        {
            Mutations.CreateEntry("ba2-mix-del", Mutations.DeleteFromRow()),
            Mutations.CreateEntry("ba2-mix-new", Mutations.SetCell(CF, "c", "new", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var deleted = await Client.ReadRowAsync(TN, "ba2-mix-del");
        deleted.Should().BeNull();
        var created = await Client.ReadRowAsync(TN, "ba2-mix-new");
        created.Should().NotBeNull();
    }

    [Fact]
    public async Task Batch_multiple_mutations_per_entry()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("ba2-multi",
                Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "ba2-multi");
        row!.Families[0].Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task Batch_50_entries()
    {
        var entries = Enumerable.Range(0, 50)
            .Select(i => Mutations.CreateEntry($"ba2-50-{i:D3}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000))))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var count = 0;
        await foreach (var _ in Client.ReadRows(TN,
            RowSet.FromRowRanges(RowRange.Closed("ba2-50-000", "ba2-50-999"))))
            count++;
        count.Should().Be(50);
    }

    [Fact]
    public async Task Batch_with_server_timestamps()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("ba2-ts1", Mutations.SetCell(CF, "c", "v1", new BigtableVersion(-1))),
            Mutations.CreateEntry("ba2-ts2", Mutations.SetCell(CF, "c", "v2", new BigtableVersion(-1)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row1 = await Client.ReadRowAsync(TN, "ba2-ts1");
        var row2 = await Client.ReadRowAsync(TN, "ba2-ts2");
        row1!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().BeGreaterThan(0);
        row2!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Batch_preserves_existing_rows()
    {
        await Client.MutateRowAsync(TN, "ba2-exist",
            Mutations.SetCell(CF, "orig", "data", new BigtableVersion(1000)));
        var entries = new[]
        {
            Mutations.CreateEntry("ba2-new", Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var existing = await Client.ReadRowAsync(TN, "ba2-exist");
        existing.Should().NotBeNull();
    }

    [Fact]
    public async Task Batch_version_range_delete()
    {
        await Client.MutateRowAsync(TN, "ba2-vdel",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        var entries = new[]
        {
            Mutations.CreateEntry("ba2-vdel",
                Mutations.DeleteFromColumn(CF, "c",
                    new BigtableVersionRange(new BigtableVersion(1000), new BigtableVersion(3000))))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "ba2-vdel");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v3");
    }

    [Fact]
    public async Task Batch_empty_value()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("ba2-empty", Mutations.SetCell(CF, "c", "", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "ba2-empty");
        row!.Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }
}

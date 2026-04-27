using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for multiple mutations in a single MutateRow call — ordering, overwrite, and combination semantics.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowrequest
///   "Mutates a row atomically. Cells already present in a row are left unchanged
///    unless explicitly changed by mutation."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutateRowMultiMutationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "multi-mut";

    public MutateRowMultiMutationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task Multiple_set_cells_different_columns()
    {
        await Client.MutateRowAsync(TN, "mm-r1",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "mm-r1");
        var cols = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().HaveCount(3);
    }

    [Fact]
    public async Task Multiple_set_cells_same_column_different_timestamps()
    {
        await Client.MutateRowAsync(TN, "mm-r2",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        var row = await Client.ReadRowAsync(TN, "mm-r2");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task Set_then_delete_same_column()
    {
        await Client.MutateRowAsync(TN, "mm-r3",
            Mutations.SetCell(CF, "c", "temp", new BigtableVersion(1000)),
            Mutations.DeleteFromColumn(CF, "c"));
        var row = await Client.ReadRowAsync(TN, "mm-r3");
        // After delete, the column should be gone
        row.Should().BeNull();
    }

    [Fact]
    public async Task Delete_then_set_same_column()
    {
        // First write a value, then in second call delete+set
        await Client.MutateRowAsync(TN, "mm-r4",
            Mutations.SetCell(CF, "c", "original", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "mm-r4",
            Mutations.DeleteFromColumn(CF, "c"),
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "mm-r4");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Delete_from_row_removes_everything()
    {
        await Client.MutateRowAsync(TN, "mm-r5",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "mm-r5", Mutations.DeleteFromRow());
        var row = await Client.ReadRowAsync(TN, "mm-r5");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Delete_from_family_preserves_other_families()
    {
        await Client.MutateRowAsync(TN, "mm-r6",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "b", "2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "mm-r6", Mutations.DeleteFromFamily(CF));
        var row = await Client.ReadRowAsync(TN, "mm-r6");
        row.Should().NotBeNull();
        row!.Families.Should().ContainSingle();
        row.Families[0].Name.Should().Be("cf2");
    }

    [Fact]
    public async Task Same_timestamp_overwrite_in_single_request()
    {
        await Client.MutateRowAsync(TN, "mm-r7",
            Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "second", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "mm-r7");
        // Only one cell at that timestamp, last write wins
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task Set_cell_across_families_in_single_request()
    {
        await Client.MutateRowAsync(TN, "mm-r8",
            Mutations.SetCell(CF, "a", "cf-val", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "b", "cf2-val", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "mm-r8");
        row!.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Many_mutations_in_single_request()
    {
        var mutations = Enumerable.Range(0, 20)
            .Select(i => Mutations.SetCell(CF, $"col{i:D3}", $"v{i}", new BigtableVersion(1000)))
            .ToArray();
        await Client.MutateRowAsync(TN, "mm-r9", mutations);
        var row = await Client.ReadRowAsync(TN, "mm-r9");
        row!.Families[0].Columns.Should().HaveCount(20);
    }

    [Fact]
    public async Task Set_empty_value()
    {
        await Client.MutateRowAsync(TN, "mm-r10",
            Mutations.SetCell(CF, "c", "", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "mm-r10");
        row!.Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task Delete_from_column_with_version_range()
    {
        await Client.MutateRowAsync(TN, "mm-r11",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)),
            Mutations.SetCell(CF, "c", "v4", new BigtableVersion(4000)));
        await Client.MutateRowAsync(TN, "mm-r11",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(4000))));
        var row = await Client.ReadRowAsync(TN, "mm-r11");
        var vals = row!.Families[0].Columns[0].Cells.Select(c => c.Value.ToStringUtf8()).ToList();
        vals.Should().BeEquivalentTo(new[] { "v4", "v1" });
    }

    [Fact]
    public async Task Mutate_preserves_existing_data()
    {
        await Client.MutateRowAsync(TN, "mm-r12",
            Mutations.SetCell(CF, "orig", "data", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "mm-r12",
            Mutations.SetCell(CF, "new", "data2", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "mm-r12");
        var cols = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("orig").And.Contain("new");
    }

    [Fact]
    public async Task Server_timestamp_mutations()
    {
        await Client.MutateRowAsync(TN, "mm-r13",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(-1)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(-1)));
        var row = await Client.ReadRowAsync(TN, "mm-r13");
        row!.Families[0].Columns.Should().HaveCount(2);
        foreach (var col in row.Families[0].Columns)
            col.Cells[0].TimestampMicros.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Multiple_deletes_in_single_request()
    {
        await Client.MutateRowAsync(TN, "mm-r14",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "mm-r14",
            Mutations.DeleteFromColumn(CF, "a"),
            Mutations.DeleteFromColumn(CF, "c"));
        var row = await Client.ReadRowAsync(TN, "mm-r14");
        row!.Families[0].Columns.Should().ContainSingle();
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("b");
    }
}

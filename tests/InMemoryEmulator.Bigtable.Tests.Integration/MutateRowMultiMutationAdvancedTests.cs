using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for MutateRow with multiple mutations in a single request,
/// testing mutation ordering, combining mutations, and edge cases.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowrequest
///   "Mutations are applied in order, meaning that earlier mutations can be masked by later ones."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutateRowMultiMutationAdvancedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "mr-multimut-adv";
    private const string CF = "cf";

    public MutateRowMultiMutationAdvancedTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    // Ref: "Mutations are applied in order, meaning that earlier mutations can be masked by later ones."
    [Fact]
    public async Task Set_then_delete_same_cell_leaves_empty()
    {
        var rk = new BigtableByteString("mm-sd");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "val", new BigtableVersion(1000)),
            Mutations.DeleteFromColumn(CF, "col"));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().BeNull();
    }

    [Fact]
    public async Task Delete_then_set_same_cell_has_value()
    {
        var rk = new BigtableByteString("mm-ds");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "old", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "col"),
            Mutations.SetCell(CF, "col", "new", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Three_sets_same_cell_same_timestamp_last_wins()
    {
        var rk = new BigtableByteString("mm-3sets");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "first", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col", "second", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col", "third", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("third");
    }

    [Fact]
    public async Task Set_multiple_columns_in_single_mutation()
    {
        var rk = new BigtableByteString("mm-multicol");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "d", "4", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "e", "5", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns.Should().HaveCount(5);
    }

    [Fact]
    public async Task Set_multiple_versions_in_single_mutation()
    {
        var rk = new BigtableByteString("mm-multivers");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "col", "v3", new BigtableVersion(3000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task DeleteFromRow_then_set_recreates_row()
    {
        var rk = new BigtableByteString("mm-delrow-set");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "old", "gone", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromRow(),
            Mutations.SetCell(CF, "new", "here", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns.Should().HaveCount(1);
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task DeleteFromFamily_then_set_in_same_family()
    {
        var rk = new BigtableByteString("mm-delfam-set");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "old", "gone", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromFamily(CF),
            Mutations.SetCell(CF, "new", "here", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns.Should().HaveCount(1);
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Set_across_families_in_single_mutation()
    {
        var rk = new BigtableByteString("mm-xfam");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "b", "2", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Ten_mutations_in_single_request()
    {
        var rk = new BigtableByteString("mm-10mut");
        var mutations = Enumerable.Range(0, 10)
            .Select(i => Mutations.SetCell(CF, $"c{i}", $"v{i}", new BigtableVersion(1000)))
            .ToArray();

        await Client.MutateRowAsync(TN, rk, mutations);

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns.Should().HaveCount(10);
    }

    [Fact]
    public async Task Empty_value_set()
    {
        var rk = new BigtableByteString("mm-empty");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().BeEmpty();
    }

    [Fact]
    public async Task Set_then_delete_family_then_set_other_family()
    {
        var rk = new BigtableByteString("mm-crossfam");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.DeleteFromFamily(CF),
            Mutations.SetCell("cf2", "b", "2", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.Should().HaveCount(1);
        row.Families[0].Name.Should().Be("cf2");
    }

    [Fact]
    public async Task Overwrite_with_newer_timestamp()
    {
        var rk = new BigtableByteString("mm-newer");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "new", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, rk);
        // Both versions exist
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Overwrite_with_older_timestamp_keeps_both()
    {
        var rk = new BigtableByteString("mm-older");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "new", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "old", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
        // Newest first
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
        row.Families[0].Columns[0].Cells[1].Value.ToStringUtf8().Should().Be("old");
    }

    [Fact]
    public async Task Delete_from_column_with_time_range_in_multi_mutation()
    {
        var rk = new BigtableByteString("mm-deltr");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "col", "v3", new BigtableVersion(3000)));

        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "col",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(3000))),
            Mutations.SetCell(CF, "keep", "yes", new BigtableVersion(4000)));

        var row = await Client.ReadRowAsync(TN, rk);
        var colData = row!.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "col");
        colData.Cells.Should().HaveCount(2); // v1 and v3
        colData.Cells.Select(c => c.Value.ToStringUtf8()).Should().Contain("v1");
        colData.Cells.Select(c => c.Value.ToStringUtf8()).Should().Contain("v3");
    }

    [Fact]
    public async Task Large_value_in_mutation()
    {
        var rk = new BigtableByteString("mm-large");
        var largeVal = new string('A', 50000);
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", largeVal, new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Length.Should().Be(50000);
    }
}

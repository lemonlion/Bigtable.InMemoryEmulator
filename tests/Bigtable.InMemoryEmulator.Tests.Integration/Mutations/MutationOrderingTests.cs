using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutationOrderingTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "mord-tests";
    private const string CF = "cf";

    public MutationOrderingTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Set_then_delete_same_column_results_in_deletion()
    {
        var rk = "mord-set-del";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "value"),
            Mutations.DeleteFromColumn(CF, "col"));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().BeNull();
    }

    [Fact]
    public async Task Delete_then_set_same_column_results_in_value()
    {
        var rk = "mord-del-set";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "old"));

        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "col"),
            Mutations.SetCell(CF, "col", "new"));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Single()
            .Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Multiple_sets_to_same_cell_last_wins()
    {
        var rk = "mord-multi-set";
        var ts = new BigtableVersion(1000);
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "first", ts),
            Mutations.SetCell(CF, "col", "second", ts),
            Mutations.SetCell(CF, "col", "third", ts));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Single()
            .Value.ToStringUtf8().Should().Be("third");
    }

    [Fact]
    public async Task Set_to_different_versions_creates_multiple_cells()
    {
        var rk = "mord-diff-ver";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "col", "v3", new BigtableVersion(3000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(3);
    }

    [Fact]
    public async Task Delete_family_then_set_in_same_family()
    {
        var rk = "mord-delfam-set";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "old1", "x"),
            Mutations.SetCell(CF, "old2", "y"));

        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromFamily(CF),
            Mutations.SetCell(CF, "new1", "z"));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8())
            .Should().ContainSingle().Which.Should().Be("new1");
    }

    [Fact]
    public async Task Delete_row_then_set_results_in_value()
    {
        var rk = "mord-delrow-set";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "old", "x"));

        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromRow(),
            Mutations.SetCell(CF, "new", "z"));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8())
            .Should().ContainSingle().Which.Should().Be("new");
    }

    [Fact]
    public async Task Set_multiple_columns_returns_sorted()
    {
        var rk = "mord-multi-col";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "3"),
            Mutations.SetCell(CF, "a", "1"),
            Mutations.SetCell(CF, "b", "2"));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8())
            .Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Delete_specific_version_preserves_others()
    {
        var rk = "mord-del-ver";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "col", "v3", new BigtableVersion(3000)));

        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "col",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(3000))));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        var timestamps = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Select(c => c.TimestampMicros).OrderBy(t => t).ToList();
        timestamps.Should().HaveCount(2);
        timestamps.Should().Contain(1000L * 1000);
        timestamps.Should().Contain(3000L * 1000);
    }

    [Fact]
    public async Task Batch_mutation_ordering_within_single_entry()
    {
        var rk = "mord-batch-order";
        var entries = new[]
        {
            Mutations.CreateEntry(rk,
                Mutations.SetCell(CF, "x", "before"),
                Mutations.DeleteFromColumn(CF, "x"),
                Mutations.SetCell(CF, "x", "after"))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Single()
            .Value.ToStringUtf8().Should().Be("after");
    }

    [Fact]
    public async Task Batch_entries_processed_independently()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mord-ind-1", Mutations.SetCell(CF, "a", "v1")),
            Mutations.CreateEntry("mord-ind-2", Mutations.SetCell(CF, "b", "v2"))
        };
        await Client.MutateRowsAsync(TN, entries);

        (await Client.ReadRowAsync(TN, "mord-ind-1"))!.Families.SelectMany(f => f.Columns).Single()
            .Qualifier.ToStringUtf8().Should().Be("a");
        (await Client.ReadRowAsync(TN, "mord-ind-2"))!.Families.SelectMany(f => f.Columns).Single()
            .Qualifier.ToStringUtf8().Should().Be("b");
    }

    [Fact]
    public async Task Multiple_delete_from_column_mutations()
    {
        var rk = "mord-multi-del";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "1"),
            Mutations.SetCell(CF, "b", "2"),
            Mutations.SetCell(CF, "c", "3"));

        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "a"),
            Mutations.DeleteFromColumn(CF, "c"));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8())
            .Should().ContainSingle().Which.Should().Be("b");
    }

    [Fact]
    public async Task Set_cell_with_empty_value()
    {
        var rk = "mord-empty-val";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", ""));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Single().Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Set_cell_with_binary_value()
    {
        var rk = "mord-binary";
        var bytes = new byte[] { 0x00, 0x01, 0xFF, 0xFE, 0x00 };
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "bin", ByteString.CopyFrom(bytes)));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Single()
            .Value.ToByteArray().Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public async Task Delete_from_row_on_nonexistent_row_is_noop()
    {
        await Client.MutateRowAsync(TN, "mord-noop-del", Mutations.DeleteFromRow());
        var row = await Client.ReadRowAsync(TN, "mord-noop-del");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Set_then_delete_row_then_set_again()
    {
        var rk = "mord-set-del-set";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "phase1", "a"),
            Mutations.DeleteFromRow(),
            Mutations.SetCell(CF, "phase2", "b"));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8())
            .Should().ContainSingle().Which.Should().Be("phase2");
    }

    [Fact]
    public async Task Large_batch_maintains_sorted_columns()
    {
        var rk = "mord-large-batch";
        var mutations = Enumerable.Range(0, 50)
            .Select(i => Mutations.SetCell(CF, $"col{i:D3}", $"val{i}"))
            .ToArray();
        await Client.MutateRowAsync(TN, rk, mutations);

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        var cols = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().HaveCount(50);
        cols.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Delete_column_versions_then_set_new_version()
    {
        var rk = "mord-del-ver-set";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "old", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "col"),
            Mutations.SetCell(CF, "col", "new", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().ContainSingle();
        cells.Single().Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Sequential_single_mutations_accumulate()
    {
        var rk = "mord-seq-acc";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a", "1"));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "b", "2"));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "c", "3"));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).OrderBy(c => c)
            .Should().BeEquivalentTo(new[] { "a", "b", "c" });
    }
}

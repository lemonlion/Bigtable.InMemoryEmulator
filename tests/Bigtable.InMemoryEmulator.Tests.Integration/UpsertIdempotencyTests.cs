using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for upsert (write-if-not-exists vs overwrite) patterns using MutateRow.
/// Bigtable doesn't have an explicit upsert — writes always succeed. This tests
/// idempotency, version semantics, and overwrite behavior.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class UpsertIdempotencyTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "upsert-idem";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public UpsertIdempotencyTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Write_to_nonexistent_row_creates_it()
    {
        await Client.MutateRowAsync(TN, "ups-new",
            Mutations.SetCell(CF, "c", "val", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "ups-new");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Same_write_twice_same_version_is_idempotent()
    {
        await Client.MutateRowAsync(TN, "ups-idem1",
            Mutations.SetCell(CF, "c", "val", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ups-idem1",
            Mutations.SetCell(CF, "c", "val", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "ups-idem1");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
    }

    [Fact]
    public async Task Same_key_different_version_creates_new_cell()
    {
        await Client.MutateRowAsync(TN, "ups-newver",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ups-newver",
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, "ups-newver");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task Same_version_different_value_overwrites()
    {
        await Client.MutateRowAsync(TN, "ups-overwrite",
            Mutations.SetCell(CF, "c", "old-value", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ups-overwrite",
            Mutations.SetCell(CF, "c", "new-value", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "ups-overwrite");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new-value");
    }

    [Fact]
    public async Task Adding_new_column_preserves_existing()
    {
        await Client.MutateRowAsync(TN, "ups-addcol",
            Mutations.SetCell(CF, "existing", "val1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ups-addcol",
            Mutations.SetCell(CF, "new-col", "val2", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "ups-addcol");
        row!.Families[0].Columns.Should().HaveCount(2);
    }

    [Fact]
    public async Task Batch_upsert_creates_multiple_rows()
    {
        var entries = Enumerable.Range(0, 5)
            .Select(i => Mutations.CreateEntry($"ups-batch-{i}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000))))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);

        for (int i = 0; i < 5; i++)
        {
            var row = await Client.ReadRowAsync(TN, $"ups-batch-{i}");
            row.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Batch_upsert_existing_rows_overwrites()
    {
        // First write
        var entries1 = new[]
        {
            Mutations.CreateEntry("ups-batch-ow",
                Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries1);

        // Second write same version
        var entries2 = new[]
        {
            Mutations.CreateEntry("ups-batch-ow",
                Mutations.SetCell(CF, "c", "second", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries2);

        var row = await Client.ReadRowAsync(TN, "ups-batch-ow");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task CheckAndMutate_as_conditional_upsert()
    {
        // Write only if row doesn't exist
        var result = await Client.CheckAndMutateRowAsync(TN, "ups-cam-new",
            RowFilters.PassAllFilter(),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "c", "created", new BigtableVersion(1000)) });

        result.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, "ups-cam-new");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("created");
    }

    [Fact]
    public async Task CheckAndMutate_conditional_upsert_existing_row()
    {
        await Client.MutateRowAsync(TN, "ups-cam-exist",
            Mutations.SetCell(CF, "c", "original", new BigtableVersion(1000)));

        // Try to write only if row doesn't exist — should NOT write
        var result = await Client.CheckAndMutateRowAsync(TN, "ups-cam-exist",
            RowFilters.PassAllFilter(),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "c", "should-not-appear", new BigtableVersion(2000)) });

        result.PredicateMatched.Should().BeTrue();
        // true matched = row exists, false mutations not applied
        var row = await Client.ReadRowAsync(TN, "ups-cam-exist");
        row!.Families[0].Columns[0].Cells
            .Select(c => c.Value.ToStringUtf8())
            .Should().NotContain("should-not-appear");
    }

    [Fact]
    public async Task Repeated_writes_hundred_times_same_version()
    {
        for (int i = 0; i < 100; i++)
            await Client.MutateRowAsync(TN, "ups-100x",
                Mutations.SetCell(CF, "c", $"val-{i}", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "ups-100x");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("val-99");
    }

    [Fact]
    public async Task Write_empty_value_then_overwrite()
    {
        await Client.MutateRowAsync(TN, "ups-empty-ow",
            Mutations.SetCell(CF, "c", "", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ups-empty-ow",
            Mutations.SetCell(CF, "c", "non-empty", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "ups-empty-ow");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("non-empty");
    }

    [Fact]
    public async Task Overwrite_with_empty_value()
    {
        await Client.MutateRowAsync(TN, "ups-ow-empty",
            Mutations.SetCell(CF, "c", "has-value", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ups-ow-empty",
            Mutations.SetCell(CF, "c", "", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "ups-ow-empty");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_then_re_create_same_key()
    {
        await Client.MutateRowAsync(TN, "ups-recreate",
            Mutations.SetCell(CF, "c", "original", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ups-recreate", Mutations.DeleteFromRow());
        await Client.MutateRowAsync(TN, "ups-recreate",
            Mutations.SetCell(CF, "c", "recreated", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, "ups-recreate");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("recreated");
    }

    [Fact]
    public async Task Multiple_deletes_on_nonexistent_row_is_safe()
    {
        // Deleting something that doesn't exist should not error
        await Client.MutateRowAsync(TN, "ups-del-nx",
            Mutations.DeleteFromRow());
        await Client.MutateRowAsync(TN, "ups-del-nx",
            Mutations.DeleteFromRow());

        var row = await Client.ReadRowAsync(TN, "ups-del-nx");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Write_with_server_timestamp_then_explicit()
    {
        await Client.MutateRowAsync(TN, "ups-ts-mix",
            Mutations.SetCell(CF, "c", "server-ts", new BigtableVersion(-1)));
        await Client.MutateRowAsync(TN, "ups-ts-mix",
            Mutations.SetCell(CF, "c", "explicit-ts", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "ups-ts-mix");
        // Should have 2 versions (different timestamps)
        row!.Families[0].Columns[0].Cells.Should().HaveCountGreaterThanOrEqualTo(2);
    }
}

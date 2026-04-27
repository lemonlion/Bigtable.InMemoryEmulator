using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Mutation atomicity and ordering integration tests — verifies that multiple mutations
/// in a single request are atomic, tests ordering semantics, and covers MutateRows batch
/// edge cases.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.MutateRowRequest
///   "Mutates a row atomically."
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.MutateRowsRequest
///   "Each individual row is mutated atomically."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutationAtomicityIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "mut-atom-tests";
    private const string CF = "cf";

    public MutationAtomicityIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    #region Multiple mutations in single MutateRow

    [Fact]
    public async Task SetCell_multiple_columns_same_request()
    {
        await Client.MutateRowAsync(TN, "atom-mcol",
            Mutations.SetCell(CF, "a", "va", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "vb", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "vc", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "atom-mcol");
        row!.Families[0].Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task SetCell_multiple_families_same_request()
    {
        await Client.MutateRowAsync(TN, "atom-mfam",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "v2", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "atom-mfam");
        row!.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task SetCell_multiple_versions_same_request()
    {
        await Client.MutateRowAsync(TN, "atom-mver",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));

        var row = await Client.ReadRowAsync(TN, "atom-mver");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(3);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v3");
    }

    [Fact]
    public async Task SetCell_then_delete_same_column_same_request()
    {
        // Ref: Mutations are applied sequentially within a request
        await Client.MutateRowAsync(TN, "atom-sd",
            Mutations.SetCell(CF, "c", "visible", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "also_visible", new BigtableVersion(2000)),
            Mutations.DeleteFromColumn(CF, "c"));

        var row = await Client.ReadRowAsync(TN, "atom-sd");
        // Delete should have removed both cells
        row.Should().BeNull();
    }

    [Fact]
    public async Task Delete_then_SetCell_same_column_same_request()
    {
        // Pre-populate
        await Client.MutateRowAsync(TN, "atom-ds",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));

        // Delete then set in same request
        await Client.MutateRowAsync(TN, "atom-ds",
            Mutations.DeleteFromColumn(CF, "c"),
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, "atom-ds");
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task DeleteFromRow_then_SetCell_same_request()
    {
        // Pre-populate
        await Client.MutateRowAsync(TN, "atom-delrow-set",
            Mutations.SetCell(CF, "a", "x", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "b", "y", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "atom-delrow-set",
            Mutations.DeleteFromRow(),
            Mutations.SetCell(CF, "c", "z", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, "atom-delrow-set");
        row.Should().NotBeNull();
        row!.Families.Should().ContainSingle().Which.Name.Should().Be(CF);
        row.Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("c");
    }

    [Fact]
    public async Task DeleteFromFamily_then_SetCell_in_that_family()
    {
        await Client.MutateRowAsync(TN, "atom-delfam-set",
            Mutations.SetCell(CF, "old", "x", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "atom-delfam-set",
            Mutations.DeleteFromFamily(CF),
            Mutations.SetCell(CF, "new", "y", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, "atom-delfam-set");
        row.Should().NotBeNull();
        row!.Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Multiple_deletes_in_same_request()
    {
        await Client.MutateRowAsync(TN, "atom-multi-del",
            Mutations.SetCell(CF, "a", "va", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "vb", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "vc", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "atom-multi-del",
            Mutations.DeleteFromColumn(CF, "a"),
            Mutations.DeleteFromColumn(CF, "c"));

        var row = await Client.ReadRowAsync(TN, "atom-multi-del");
        row!.Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("b");
    }

    #endregion

    #region Cross-family mutations

    [Fact]
    public async Task SetCell_in_both_families_same_request()
    {
        await Client.MutateRowAsync(TN, "atom-xfam",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "v2", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "atom-xfam");
        row!.Families.Should().HaveCount(2);
        row.Families.Should().Contain(f => f.Name == CF);
        row.Families.Should().Contain(f => f.Name == "cf2");
    }

    [Fact]
    public async Task Delete_one_family_set_other_family()
    {
        await Client.MutateRowAsync(TN, "atom-xfam-ds",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "v2", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "atom-xfam-ds",
            Mutations.DeleteFromFamily(CF),
            Mutations.SetCell("cf2", "d", "v3", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, "atom-xfam-ds");
        row!.Families.Should().ContainSingle().Which.Name.Should().Be("cf2");
        row.Families[0].Columns.Should().HaveCount(2);
    }

    #endregion

    #region MutateRows batch edge cases

    [Fact]
    public async Task MutateRows_100_entries()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.MutateRowsRequest
        //   "entries: … at most 100,000 entries."
        var entries = Enumerable.Range(1, 100)
            .Select(i => Mutations.CreateEntry($"atom-100-{i:D3}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000))))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);

        // Spot-check
        var first = await Client.ReadRowAsync(TN, "atom-100-001");
        var last = await Client.ReadRowAsync(TN, "atom-100-100");
        first.Should().NotBeNull();
        last.Should().NotBeNull();
    }

    [Fact]
    public async Task MutateRows_batch_with_only_deletes()
    {
        // Pre-populate
        for (int i = 1; i <= 3; i++)
            await Client.MutateRowAsync(TN, $"atom-del-batch-{i}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("atom-del-batch-1", Mutations.DeleteFromRow()),
            Mutations.CreateEntry("atom-del-batch-2", Mutations.DeleteFromRow()),
            Mutations.CreateEntry("atom-del-batch-3", Mutations.DeleteFromRow()),
        };
        await Client.MutateRowsAsync(TN, entries);

        for (int i = 1; i <= 3; i++)
        {
            var row = await Client.ReadRowAsync(TN, $"atom-del-batch-{i}");
            row.Should().BeNull();
        }
    }

    [Fact]
    public async Task MutateRows_batch_same_row_mixed_mutations()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("atom-same-row",
                Mutations.SetCell(CF, "a", "va", new BigtableVersion(1000))),
            Mutations.CreateEntry("atom-same-row",
                Mutations.SetCell(CF, "b", "vb", new BigtableVersion(1000))),
            Mutations.CreateEntry("atom-same-row",
                Mutations.SetCell(CF, "c", "vc", new BigtableVersion(1000))),
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "atom-same-row");
        row!.Families[0].Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task MutateRows_batch_same_row_same_column_last_wins()
    {
        // When multiple entries target same row/column/timestamp, last entry wins
        var entries = new[]
        {
            Mutations.CreateEntry("atom-last-wins",
                Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000))),
            Mutations.CreateEntry("atom-last-wins",
                Mutations.SetCell(CF, "c", "second", new BigtableVersion(1000))),
            Mutations.CreateEntry("atom-last-wins",
                Mutations.SetCell(CF, "c", "third", new BigtableVersion(1000))),
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "atom-last-wins");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("third");
    }

    [Fact]
    public async Task MutateRows_batch_set_then_delete_in_separate_entries()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("atom-batch-sd",
                Mutations.SetCell(CF, "c", "val", new BigtableVersion(1000))),
            Mutations.CreateEntry("atom-batch-sd",
                Mutations.DeleteFromRow()),
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "atom-batch-sd");
        row.Should().BeNull();
    }

    [Fact]
    public async Task MutateRows_batch_preserves_lexicographic_order()
    {
        // Write rows in reverse order, verify they read back in lex order
        var entries = Enumerable.Range(0, 10).Reverse()
            .Select(i => Mutations.CreateEntry($"atom-lex-{i:D2}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000))))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);

        var readRows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, RowSet.FromRowRanges(
            RowRange.Closed("atom-lex-00", "atom-lex-99"))))
            readRows.Add(r);

        readRows.Should().HaveCount(10);
        readRows.Select(r => r.Key.ToStringUtf8()).Should().BeInAscendingOrder();
    }

    #endregion

    #region Mutation idempotency

    [Fact]
    public async Task SetCell_same_value_same_timestamp_is_idempotent()
    {
        await Client.MutateRowAsync(TN, "atom-idem",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "atom-idem",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "atom-idem");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteFromRow_on_nonexistent_row_is_idempotent()
    {
        // Should not throw
        await Client.MutateRowAsync(TN, "atom-idem-del", Mutations.DeleteFromRow());
        await Client.MutateRowAsync(TN, "atom-idem-del", Mutations.DeleteFromRow());
        var row = await Client.ReadRowAsync(TN, "atom-idem-del");
        row.Should().BeNull();
    }

    [Fact]
    public async Task DeleteFromColumn_on_nonexistent_column_is_noop()
    {
        await Client.MutateRowAsync(TN, "atom-delcol-noop",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)));

        // Delete a column that doesn't exist
        await Client.MutateRowAsync(TN, "atom-delcol-noop",
            Mutations.DeleteFromColumn(CF, "nonexistent"));

        var row = await Client.ReadRowAsync(TN, "atom-delcol-noop");
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("a");
    }

    #endregion

    #region Large mutations

    [Fact]
    public async Task SetCell_with_1KB_value()
    {
        var value = new string('x', 1024);
        await Client.MutateRowAsync(TN, "atom-1kb",
            Mutations.SetCell(CF, "c", value, new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "atom-1kb");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().HaveLength(1024);
    }

    [Fact]
    public async Task SetCell_with_10KB_value()
    {
        var value = new string('y', 10240);
        await Client.MutateRowAsync(TN, "atom-10kb",
            Mutations.SetCell(CF, "c", value, new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "atom-10kb");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().HaveLength(10240);
    }

    [Fact]
    public async Task SetCell_20_columns_in_single_mutation()
    {
        var mutations = Enumerable.Range(0, 20)
            .Select(i => Mutations.SetCell(CF, $"col-{i:D2}", $"val-{i}", new BigtableVersion(1000)))
            .ToArray();
        await Client.MutateRowAsync(TN, "atom-20cols", mutations);

        var row = await Client.ReadRowAsync(TN, "atom-20cols");
        row!.Families[0].Columns.Should().HaveCount(20);
    }

    [Fact]
    public async Task SetCell_20_versions_of_same_column_in_single_mutation()
    {
        var mutations = Enumerable.Range(1, 20)
            .Select(i => Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)))
            .ToArray();
        await Client.MutateRowAsync(TN, "atom-20ver", mutations);

        var row = await Client.ReadRowAsync(TN, "atom-20ver");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(20);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v20");
    }

    #endregion
}

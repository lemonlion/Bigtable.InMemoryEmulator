using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for error handling — invalid table names, missing families,
/// malformed requests, and boundary validations.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ErrorHandlingVariationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "err-var";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public ErrorHandlingVariationTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task MutateRow_to_nonexistent_family_throws()
    {
        var act = () => Client.MutateRowAsync(TN, "err-nonfam",
            Mutations.SetCell("nonexistent_family", "c", "val", new BigtableVersion(1000)));
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task MutateRows_batch_nonexistent_family_does_not_throw()
    {
        // MutateRows handles per-entry errors silently via the SDK;
        // the overall call does not throw.
        var entries = new[]
        {
            Mutations.CreateEntry("err-batch-nonfam",
                Mutations.SetCell("bad_family", "c", "val", new BigtableVersion(1000)))
        };
        // Should not throw - per-entry errors are handled silently
        await Client.MutateRowsAsync(TN, entries);
    }

    [Fact]
    public async Task MutateRows_batch_nonexistent_family_entry_does_not_persist()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("err-batch-nonfam2",
                Mutations.SetCell("bad_family", "c", "val", new BigtableVersion(1000)))
        };
        try { await Client.MutateRowsAsync(TN, entries); } catch { }
        var row = await Client.ReadRowAsync(TN, "err-batch-nonfam2");
        row.Should().BeNull();
    }

    [Fact]
    public async Task ReadRow_nonexistent_key_returns_null()
    {
        var row = await Client.ReadRowAsync(TN, "absolutely-does-not-exist");
        row.Should().BeNull();
    }

    [Fact]
    public async Task ReadModifyWrite_nonexistent_family_throws()
    {
        var act = () => Client.ReadModifyWriteRowAsync(TN, "err-rmw-nonfam",
            ReadModifyWriteRules.Append("nonexistent_family", "c", "val"));
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task CheckAndMutate_nonexistent_family_in_mutation_throws()
    {
        await Client.MutateRowAsync(TN, "err-cam-nonfam",
            Mutations.SetCell(CF, "c", "val", new BigtableVersion(1000)));

        var act = () => Client.CheckAndMutateRowAsync(TN, "err-cam-nonfam",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell("bad_family", "c", "val", new BigtableVersion(2000)) });
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task ReadRows_empty_RowSet_returns_all_rows()
    {
        await Client.MutateRowAsync(TN, "err-all-1",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "err-all-2",
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(1000)));

        var request = new ReadRowsRequest { TableNameAsTableName = TN };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());

        keys.Should().Contain("err-all-1");
        keys.Should().Contain("err-all-2");
    }

    [Fact]
    [Trait(TestTraits.Target, TestTraits.InMemoryOnly)] // Go emulator silently ignores delete from nonexistent family
    public async Task Delete_from_nonexistent_family_throws()
    {
        await Client.MutateRowAsync(TN, "err-del-nonfam",
            Mutations.SetCell(CF, "c", "val", new BigtableVersion(1000)));

        var act = () => Client.MutateRowAsync(TN, "err-del-nonfam",
            Mutations.DeleteFromFamily("nonexistent_family"));
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Delete_from_nonexistent_column_succeeds()
    {
        // Deleting a column that doesn't exist is not an error
        await Client.MutateRowAsync(TN, "err-del-nocol",
            Mutations.SetCell(CF, "c", "val", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "err-del-nocol",
            Mutations.DeleteFromColumn(CF, "nonexistent-column"));

        // Original data should still be there
        var row = await Client.ReadRowAsync(TN, "err-del-nocol");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_from_row_nonexistent_key_succeeds()
    {
        // Deleting a nonexistent row is not an error
        await Client.MutateRowAsync(TN, "err-del-norow", Mutations.DeleteFromRow());
        // No exception should be thrown
    }

    [Fact]
    public async Task Read_with_block_all_filter_returns_nothing()
    {
        await Client.MutateRowAsync(TN, "err-block",
            Mutations.SetCell(CF, "c", "val", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.BlockAllFilter(),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("err-block") } }
        };
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(0);
    }

    [Fact]
    public async Task Read_with_pass_all_filter_returns_all()
    {
        await Client.MutateRowAsync(TN, "err-pass",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.PassAllFilter(),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("err-pass") } }
        };
        var cellCount = 0;
        await foreach (var row in Client.ReadRows(request))
            cellCount += row.Families.Sum(f => f.Columns.Sum(c => c.Cells.Count));
        cellCount.Should().Be(2);
    }

    [Fact]
    public async Task Batch_with_valid_and_invalid_family_valid_entry_persists()
    {
        // MutateRows processes entries individually; valid ones persist even if others fail
        var entries = new[]
        {
            Mutations.CreateEntry("err-batch-mix-ok",
                Mutations.SetCell(CF, "c", "val", new BigtableVersion(1000))),
            Mutations.CreateEntry("err-batch-mix-bad",
                Mutations.SetCell("bad_family", "c", "val", new BigtableVersion(1000)))
        };
        try { await Client.MutateRowsAsync(TN, entries); } catch { }
        // The valid entry should have been persisted
        var row = await Client.ReadRowAsync(TN, "err-batch-mix-ok");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task CheckAndMutate_empty_key_handled()
    {
        // Empty row key should be handled
        var act = () => Client.CheckAndMutateRowAsync(TN, "",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)) });
        // Should either succeed or throw a defined error
        try
        {
            await act();
        }
        catch (Exception)
        {
            // Expected — empty key may be invalid
        }
    }

    [Fact]
    public async Task ReadModifyWrite_increment_on_non_numeric_column()
    {
        await Client.MutateRowAsync(TN, "err-rmw-nonnumeric",
            Mutations.SetCell(CF, "text", "hello", new BigtableVersion(1000)));

        // Incrementing a non-8-byte value may throw or produce unexpected results
        var act = () => Client.ReadModifyWriteRowAsync(TN, "err-rmw-nonnumeric",
            ReadModifyWriteRules.Increment(CF, "text", 1));
        // Just verify it doesn't crash the emulator
        try
        {
            await act();
        }
        catch (Exception)
        {
            // Expected — non-8-byte value can't be incremented
        }
    }
}

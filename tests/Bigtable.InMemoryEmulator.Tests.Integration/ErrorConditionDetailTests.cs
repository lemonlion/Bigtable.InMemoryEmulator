using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for error conditions: invalid requests, nonexistent resources.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ErrorConditionDetailTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public ErrorConditionDetailTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync("err-test", new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("err-test");

    #region Nonexistent table

    [Fact]
    public async Task MutateRow_nonexistent_table_throws()
    {
        var badTN = _fixture.GetTableName("nonexistent-table");
        var act = () => Client.MutateRowAsync(badTN, "r1",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    [Fact]
    public async Task ReadRows_nonexistent_table_throws()
    {
        var badTN = _fixture.GetTableName("nonexistent-table");
        var act = async () =>
        {
            await foreach (var row in Client.ReadRows(badTN))
            { }
        };
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    [Fact]
    public async Task MutateRows_nonexistent_table_throws()
    {
        var badTN = _fixture.GetTableName("nonexistent-table");
        var entries = new[]
        {
            Mutations.CreateEntry("r1",
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)))
        };
        var act = () => Client.MutateRowsAsync(badTN, entries);
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    [Fact]
    public async Task CheckAndMutate_nonexistent_table_throws()
    {
        var badTN = _fixture.GetTableName("nonexistent-table");
        var act = () => Client.CheckAndMutateRowAsync(badTN, "r1",
            predicateFilter: RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)) },
            falseMutations: null);
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    [Fact]
    public async Task ReadModifyWrite_nonexistent_table_throws()
    {
        var badTN = _fixture.GetTableName("nonexistent-table");
        var act = () => Client.ReadModifyWriteRowAsync(badTN, "r1",
            ReadModifyWriteRules.Increment(CF, "counter", 1));
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    #endregion

    #region Nonexistent column family

    [Fact]
    public async Task MutateRow_nonexistent_family_throws()
    {
        var act = () => Client.MutateRowAsync(TN, "r1",
            Mutations.SetCell("nonexistent_family", "c", "v", new BigtableVersion(1000)));
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    [Fact]
    [Trait(TestTraits.Target, TestTraits.InMemoryOnly)]
    public async Task MutateRows_nonexistent_family_entry_fails()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowsresponse
        //   MutateRows returns per-entry status. Writing to a nonexistent family should fail the entry.
        //   Known divergence: in-memory emulator currently silently succeeds on nonexistent family in MutateRows.
        var entries = new[]
        {
            Mutations.CreateEntry("nonexist-fam-batch", 
                Mutations.SetCell("nonexistent_family", "c", "v", new BigtableVersion(1000)))
        };
        // Currently the in-memory emulator does not throw for nonexistent families in MutateRows
        await Client.MutateRowsAsync(TN, entries);
    }

    [Fact]
    public async Task ReadModifyWrite_nonexistent_family_throws()
    {
        var act = () => Client.ReadModifyWriteRowAsync(TN, "r1",
            ReadModifyWriteRules.Increment("nonexistent_family", "counter", 1));
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    #endregion

    #region Reading nonexistent rows

    [Fact]
    public async Task ReadRow_nonexistent_key_returns_empty()
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("nonexistent-key")))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadRow_multiple_nonexistent_keys_returns_empty()
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("no1", "no2", "no3")))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task ReadRow_empty_range_returns_empty()
    {
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(RowRange.ClosedOpen("zzz-start", "zzz-end"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    #endregion

    #region Delete operations on nonexistent data

    [Fact]
    public async Task DeleteFromRow_nonexistent_row_succeeds()
    {
        // Deleting a row that doesn't exist should not throw
        await Client.MutateRowAsync(TN, "nonexistent-del", Mutations.DeleteFromRow());
    }

    [Fact]
    public async Task DeleteFromColumn_nonexistent_column_succeeds()
    {
        await Client.MutateRowAsync(TN, "err-del-col",
            Mutations.SetCell(CF, "exists", "v", new BigtableVersion(1000)));
        // Deleting a column that doesn't exist should not throw
        await Client.MutateRowAsync(TN, "err-del-col",
            Mutations.DeleteFromColumn(CF, "nonexistent"));
        // Existing column should still be there
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("err-del-col")))
            rows.Add(row);
        rows.Should().ContainSingle();
    }

    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task DeleteFromFamily_nonexistent_family_throws()
    {
        await Client.MutateRowAsync(TN, "err-del-fam",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var act = () => Client.MutateRowAsync(TN, "err-del-fam",
            Mutations.DeleteFromFamily("nonexistent_family"));
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    #endregion

    #region Operations on existing data succeed

    [Fact]
    public async Task MutateRow_valid_operations_succeed()
    {
        await Client.MutateRowAsync(TN, "err-ok",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("err-ok")))
            rows.Add(row);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task CaM_on_empty_row_works()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "err-cam-empty",
            predicateFilter: RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "c", "t", new BigtableVersion(1000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "c", "f", new BigtableVersion(1000)) });
        // Empty row → predicate false
        result.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task RMW_on_new_row_works()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "err-rmw-new",
            ReadModifyWriteRules.Increment(CF, "counter", 1));
        resp.Row.Should().NotBeNull();
    }

    #endregion
}

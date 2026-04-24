using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Integration tests for request validation and gRPC error codes.
/// Verifies that the emulator returns correct status codes for invalid inputs.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ErrorValidationIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "err-tests";
    private const string Family = "cf";

    public ErrorValidationIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { Family });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private BigtableClient Client => _fixture.Client;
    private BigtableTableAdminClient AdminClient => _fixture.AdminClient;
    private BigtableServiceApiClient ServiceApiClient => _fixture.ServiceApiClient;
    private TableName TN => _fixture.GetTableName(Table);

    #region MutateRow validation

    [Fact]
    public async Task MutateRow_empty_row_key_throws_ArgumentException()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowrequest
        //   "row_key must not be empty"
        // The SDK validates this client-side before sending the gRPC call.
        var act = () => Client.MutateRowAsync(TN, new BigtableByteString(""),
            Mutations.SetCell(Family, "col", "val", new BigtableVersion(1000)));
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task MutateRow_nonexistent_table_throws()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowrequest
        //   "If the table does not exist, returns NOT_FOUND."
        var badTable = _fixture.GetTableName("nonexistent-table-xyz");
        var act = () => Client.MutateRowAsync(badTable, new BigtableByteString("row1"),
            Mutations.SetCell(Family, "col", "val", new BigtableVersion(1000)));
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task MutateRow_nonexistent_family_throws()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
        //   "If the family does not exist, returns INVALID_ARGUMENT or FAILED_PRECONDITION."
        var act = () => Client.MutateRowAsync(TN, new BigtableByteString("row1"),
            Mutations.SetCell("nonexistent-fam", "col", "val", new BigtableVersion(1000)));
        await act.Should().ThrowAsync<RpcException>();
    }

    #endregion

    #region MutateRows validation

    [Fact]
    public async Task MutateRows_empty_entries_throws()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowsrequest
        //   "entries must not be empty"
        var act = () => Client.MutateRowsAsync(TN, Array.Empty<MutateRowsRequest.Types.Entry>());
        await act.Should().ThrowAsync<Exception>();
    }

    #endregion

    #region Admin validation

    // Go emulator divergence: returns wrong status code for duplicate table creation.
    // Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#google.bigtable.admin.v2.BigtableTableAdmin.CreateTable
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task CreateTable_duplicate_returns_AlreadyExists()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#createtablerequest
        //   "If a table with the given ID already exists, returns ALREADY_EXISTS."
        var act = async () =>
        {
            // Table already created in InitializeAsync
            await _fixture.CreateTableAsync(Table, new[] { Family });
        };
        await act.Should().ThrowAsync<RpcException>()
            .Where(ex => ex.StatusCode == StatusCode.AlreadyExists);
    }

    [Fact]
    public async Task GetTable_nonexistent_returns_NotFound()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#gettablerequest
        var act = () => AdminClient.GetTableAsync(_fixture.InstanceName + "/tables/nonexistent-table-xyz");
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteTable_nonexistent_returns_NotFound()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#deletetablerequest
        var act = () => AdminClient.DeleteTableAsync(_fixture.InstanceName + "/tables/nonexistent-table-xyz");
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task ModifyColumnFamilies_add_duplicate_returns_AlreadyExists()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#modifycolumnfamiliesrequest
        var modReq = new ModifyColumnFamiliesRequest
        {
            Name = _fixture.InstanceName + "/tables/" + Table,
        };
        modReq.Modifications.Add(new ModifyColumnFamiliesRequest.Types.Modification
        {
            Id = Family,
            Create = new Google.Cloud.Bigtable.Admin.V2.ColumnFamily(),
        });
        var act = () => AdminClient.ModifyColumnFamiliesAsync(modReq);
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.AlreadyExists);
    }

    // Go emulator divergence: returns wrong status code for dropping non-existent family.
    // Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#google.bigtable.admin.v2.BigtableTableAdmin.ModifyColumnFamilies
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task ModifyColumnFamilies_drop_nonexistent_returns_NotFound()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#modifycolumnfamiliesrequest
        var modReq = new ModifyColumnFamiliesRequest
        {
            Name = _fixture.InstanceName + "/tables/" + Table,
        };
        modReq.Modifications.Add(new ModifyColumnFamiliesRequest.Types.Modification
        {
            Id = "nonexistent-fam",
            Drop = true,
        });
        var act = () => AdminClient.ModifyColumnFamiliesAsync(modReq);
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    #endregion

    #region ReadRows validation

    [Fact]
    public async Task ReadRows_nonexistent_table_throws_NotFound()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
        var badTable = _fixture.GetTableName("nonexistent-table-xyz");
        var act = async () =>
        {
            var stream = Client.ReadRows(badTable);
            await foreach (var _ in stream) { }
        };
        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.NotFound);
    }

    #endregion

    #region CheckAndMutateRow validation

    [Fact]
    public async Task CheckAndMutateRow_empty_true_and_false_mutations_throws()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#checkandmutaterowrequest
        //   "At least one of true_mutations or false_mutations must be supplied."
        var act = () => Client.CheckAndMutateRowAsync(
            TN, new BigtableByteString("row1"),
            RowFilters.PassAllFilter(),
            null,
            null);
        await act.Should().ThrowAsync<Exception>();
    }

    #endregion

    #region ExecuteQuery validation

    // Go emulator divergence: does not implement the ExecuteQuery RPC (GoogleSQL).
    // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.Bigtable.ExecuteQuery
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task ExecuteQuery_invalid_sql_throws_InvalidArgument()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#executequeryrequest
        var request = new ExecuteQueryRequest
        {
            InstanceName = _fixture.InstanceName,
            Query = "INVALID SQL !@#$%",
        };

        var stream = ServiceApiClient.ExecuteQuery(request);
        var act = async () =>
        {
            var enumerator = stream.GetResponseStream().GetAsyncEnumerator(default);
            while (await enumerator.MoveNextAsync()) { }
        };

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    #endregion

    #region AuthorizedView validation

    // Go emulator divergence: does not implement authorized_view_name; ignores the field instead of returning Unimplemented.
    // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.ReadRowsRequest
    //   authorized_view_name field
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task ReadRows_with_authorized_view_name_throws_Unimplemented()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
        //   "authorized_view_name" — not supported by the in-memory emulator.
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            AuthorizedViewName = $"{_fixture.InstanceName}/tables/{Table}/authorizedViews/my-view",
        };
        var stream = ServiceApiClient.ReadRows(request);
        var act = async () =>
        {
            var enumerator = stream.GetResponseStream().GetAsyncEnumerator(default);
            while (await enumerator.MoveNextAsync()) { }
        };
        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.Unimplemented);
    }

    // Go emulator divergence: does not implement authorized_view_name; ignores the field instead of returning Unimplemented.
    // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.MutateRowRequest
    //   authorized_view_name field
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task MutateRow_with_authorized_view_name_throws_Unimplemented()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowrequest
        //   "authorized_view_name" — not supported by the in-memory emulator.
        var request = new MutateRowRequest
        {
            TableNameAsTableName = TN,
            AuthorizedViewName = $"{_fixture.InstanceName}/tables/{Table}/authorizedViews/my-view",
            RowKey = ByteString.CopyFromUtf8("row1"),
        };
        request.Mutations.Add(Mutations.SetCell(Family, "col", "val", new BigtableVersion(1000)));
        var act = () => ServiceApiClient.MutateRowAsync(request);
        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.Unimplemented);
    }

    // Go emulator divergence: does not implement authorized_view_name; ignores the field instead of returning Unimplemented.
    // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.SampleRowKeysRequest
    //   authorized_view_name field
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task SampleRowKeys_with_authorized_view_name_throws_Unimplemented()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#samplerowkeysrequest
        //   "authorized_view_name" — not supported by the in-memory emulator.
        var request = new SampleRowKeysRequest
        {
            TableNameAsTableName = TN,
            AuthorizedViewName = $"{_fixture.InstanceName}/tables/{Table}/authorizedViews/my-view",
        };
        var stream = ServiceApiClient.SampleRowKeys(request);
        var act = async () =>
        {
            var enumerator = stream.GetResponseStream().GetAsyncEnumerator(default);
            while (await enumerator.MoveNextAsync()) { }
        };
        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.Unimplemented);
    }

    #endregion
}

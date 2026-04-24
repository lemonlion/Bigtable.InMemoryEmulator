using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Comprehensive error validation tests across all gRPC methods.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ErrorValidationStressTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "error-stress";
    private const string CF = "cf";

    public ErrorValidationStressTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private BigtableTableAdminClient Admin => _fixture.AdminClient;
    private string Instance => _fixture.InstanceName;

    #region MutateRow errors

    // Go emulator divergence: returns Unknown instead of InvalidArgument.
    // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.Bigtable.MutateRow
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task MutateRow_nonexistent_family_returns_error()
    {
        var act = () => Client.MutateRowAsync(TN, "err-nf",
            Mutations.SetCell("nosuchfamily", "c", "v", new BigtableVersion(1000)));
        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task MutateRow_nonexistent_table_returns_error()
    {
        var badTable = _fixture.GetTableName("nonexistent-table-err");
        var act = () => Client.MutateRowAsync(badTable, "err-nt",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.NotFound);
    }

    [Fact]
    public async Task MutateRow_timestamp_negative_version_throws()
    {
        // Ref: Timestamp must be non-negative
        // BigtableVersion constructor validates ms alignment client-side
        var act = () => Client.MutateRowAsync(TN, "err-ts",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(-1000)));
        await act.Should().ThrowAsync<Exception>();
    }

    #endregion

    #region MutateRows errors

    // Note: In-memory emulator does not validate family names in MutateRows per-entry.
    // See ErrorConditionsIntegrationTests for GcpOnly family validation tests.

    [Fact]
    public async Task MutateRows_nonexistent_table_throws()
    {
        var badTable = _fixture.GetTableName("nonexistent-table-err2");
        var entries = new[]
        {
            Mutations.CreateEntry("err-r", Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)))
        };
        var act = () => Client.MutateRowsAsync(badTable, entries);
        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.NotFound);
    }

    #endregion

    #region ReadRows errors

    [Fact]
    public async Task ReadRows_nonexistent_table_throws()
    {
        var badTable = _fixture.GetTableName("nonexistent-table-err3");
        var act = async () =>
        {
            await foreach (var _ in Client.ReadRows(badTable))
            { }
        };
        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.NotFound);
    }

    #endregion

    #region CheckAndMutateRow errors

    [Fact]
    public async Task CheckAndMutate_nonexistent_table_throws()
    {
        var badTable = _fixture.GetTableName("nonexistent-table-err4");
        var act = () => Client.CheckAndMutateRowAsync(badTable, "err-cam",
            RowFilters.PassAllFilter(),
            new[] { Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)) },
            null);
        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.NotFound);
    }

    // Note: In-memory emulator does not validate family names in CAM mutations.
    // See ErrorConditionsIntegrationTests for GcpOnly family validation tests.

    [Fact]
    public async Task CheckAndMutate_no_true_or_false_mutations_throws()
    {
        // Ref: Must have at least one of true_mutations or false_mutations
        var act = () => Client.CheckAndMutateRowAsync(TN, "err-camempty",
            RowFilters.PassAllFilter(), null, null);
        await act.Should().ThrowAsync<Exception>();
    }

    #endregion

    #region ReadModifyWriteRow errors

    [Fact]
    public async Task ReadModifyWrite_nonexistent_table_throws()
    {
        var badTable = _fixture.GetTableName("nonexistent-table-err5");
        var act = () => Client.ReadModifyWriteRowAsync(badTable, "err-rmw",
            ReadModifyWriteRules.Increment(CF, "c", 1));
        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.NotFound);
    }

    // Go emulator divergence: returns Unknown instead of InvalidArgument.
    // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.Bigtable.ReadModifyWriteRow
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task ReadModifyWrite_nonexistent_family_throws()
    {
        var act = () => Client.ReadModifyWriteRowAsync(TN, "err-rmwnf",
            ReadModifyWriteRules.Increment("badfamily", "c", 1));
        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    #endregion

    #region SampleRowKeys errors

    [Fact]
    public async Task SampleRowKeys_nonexistent_table_throws()
    {
        var act = async () =>
        {
            var fakeName = _fixture.InstanceName + "/tables/nonexistent-table-err6";
            var response = _fixture.ServiceApiClient.SampleRowKeys(new SampleRowKeysRequest
            {
                TableName = fakeName
            });
            var e = response.GetResponseStream().GetAsyncEnumerator(default);
            while (await e.MoveNextAsync()) { }
        };
        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.NotFound);
    }

    #endregion

    #region Admin errors

    [Fact]
    public async Task CreateTable_duplicate_name_throws_AlreadyExists()
    {
        // Table already created in InitializeAsync
        var act = () => Admin.CreateTableAsync(new CreateTableRequest
        {
            Parent = Instance,
            TableId = Table,
            Table = new Google.Cloud.Bigtable.Admin.V2.Table()
        });
        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.AlreadyExists);
    }

    [Fact]
    public async Task GetTable_nonexistent_throws_NotFound()
    {
        var act = () => Admin.GetTableAsync(Instance + "/tables/no-such-table-err");
        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteTable_nonexistent_throws_NotFound()
    {
        var act = () => Admin.DeleteTableAsync(Instance + "/tables/no-such-table-err2");
        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.NotFound);
    }

    [Fact]
    public async Task ModifyColumnFamilies_drop_nonexistent_family_throws()
    {
        var act = () => Admin.ModifyColumnFamiliesAsync(Instance + "/tables/" + Table,
            new[]
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "nonexistent-cf",
                    Drop = true
                }
            });
        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.NotFound);
    }

    #endregion

    #region Size validation

    [Fact]
    public async Task RowKey_exceeds_4KiB_throws()
    {
        // Ref: Max row key size is 4 KiB (4096 bytes)
        var bigKey = new string('X', 4097);
        var act = () => Client.MutateRowAsync(TN, bigKey,
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task RowKey_exactly_4KiB_succeeds()
    {
        var key = new string('Y', 4096);
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
    }

    [Fact]
    public async Task Empty_row_key_throws()
    {
        var act = () => Client.MutateRowAsync(TN, "",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await act.Should().ThrowAsync<Exception>();
    }

    #endregion
}

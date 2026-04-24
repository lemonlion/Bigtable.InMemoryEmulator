using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Comprehensive error condition integration tests — validation edge cases,
/// invalid inputs, and error codes that matter for parity.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ErrorConditionsIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "error-tests";
    private const string CF = "cf";

    public ErrorConditionsIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private BigtableTableAdminClient Admin => _fixture.AdminClient;
    private TableName TN => _fixture.GetTableName(Table);
    private string TablePath => _fixture.InstanceName + "/tables/" + Table;

    #region Row key validation

    [Fact]
    public async Task MutateRow_empty_key_throws()
    {
        // Ref: "row_key must not be empty"
        var act = () => Client.MutateRowAsync(TN, "",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task ReadRow_empty_key_throws_client_side()
    {
        // SDK validates empty row key client-side before sending to server
        var act = () => Client.ReadRowAsync(TN, "");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    #endregion

    #region Family validation

    // Go emulator divergence: returns StatusCode.Unknown instead of InvalidArgument for non-existent family.
    // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.Bigtable.MutateRow
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task MutateRow_to_nonexistent_family_throws()
    {
        var act = () => Client.MutateRowAsync(TN, "err-fam",
            Mutations.SetCell("nonexistent_family_xyz", "c", "v", new BigtableVersion(1000)));
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // Go emulator divergence: silently ignores delete from non-existent family instead of throwing.
    // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.Mutation.DeleteFromFamily
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task DeleteFromFamily_nonexistent_throws()
    {
        var act = () => Client.MutateRowAsync(TN, "err-del-fam",
            Mutations.DeleteFromFamily("no_such_family"));
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // Go emulator divergence: returns StatusCode.Unknown instead of InvalidArgument for non-existent family.
    // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.Mutation.DeleteFromColumn
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task DeleteFromColumn_nonexistent_family_throws()
    {
        var act = () => Client.MutateRowAsync(TN, "err-del-col-fam",
            Mutations.DeleteFromColumn("no_such_family", "col"));
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    #endregion

    #region Table validation

    [Fact]
    public async Task MutateRow_to_nonexistent_table_throws()
    {
        var fakeTableName = new TableName(
            _fixture.GetTableName(Table).ProjectId,
            _fixture.GetTableName(Table).InstanceId,
            "nonexistent_table_xyz");
        var act = () => Client.MutateRowAsync(fakeTableName, "err-table",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task ReadRows_from_nonexistent_table_throws()
    {
        var fakeTableName = new TableName(
            _fixture.GetTableName(Table).ProjectId,
            _fixture.GetTableName(Table).InstanceId,
            "nonexistent_table_xyz");
        var act = async () =>
        {
            await foreach (var _ in Client.ReadRows(fakeTableName)) { }
        };
        await act.Should().ThrowAsync<RpcException>();
    }

    [Fact]
    public async Task GetTable_nonexistent_throws_not_found()
    {
        var act = () => Admin.GetTableAsync(
            _fixture.InstanceName + "/tables/nonexistent_table_xyz");
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteTable_nonexistent_throws_not_found()
    {
        var act = () => Admin.DeleteTableAsync(
            _fixture.InstanceName + "/tables/nonexistent_table_xyz");
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task CreateTable_duplicate_throws_already_exists()
    {
        // Table already created in InitializeAsync
        var act = () => Admin.CreateTableAsync(new Google.Cloud.Bigtable.Admin.V2.CreateTableRequest
        {
            Parent = _fixture.InstanceName,
            TableId = Table,
            Table = new Google.Cloud.Bigtable.Admin.V2.Table
            {
                ColumnFamilies = { { CF, new Google.Cloud.Bigtable.Admin.V2.ColumnFamily() } }
            }
        });
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.AlreadyExists);
    }

    #endregion

    #region ModifyColumnFamilies errors

    [Fact]
    public async Task ModifyColumnFamilies_add_duplicate_throws()
    {
        var act = () => Admin.ModifyColumnFamiliesAsync(
            new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest
            {
                Name = TablePath,
                Modifications =
                {
                    new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest.Types.Modification
                    {
                        Id = CF,
                        Create = new Google.Cloud.Bigtable.Admin.V2.ColumnFamily()
                    }
                }
            });
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.AlreadyExists);
    }

    // Go emulator divergence: returns StatusCode.Unknown instead of NotFound for dropping non-existent family.
    // Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#google.bigtable.admin.v2.BigtableTableAdmin.ModifyColumnFamilies
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task ModifyColumnFamilies_drop_nonexistent_throws()
    {
        var act = () => Admin.ModifyColumnFamiliesAsync(
            new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest
            {
                Name = TablePath,
                Modifications =
                {
                    new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest.Types.Modification
                    {
                        Id = "nonexistent_family",
                        Drop = true
                    }
                }
            });
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    // Go emulator divergence: returns StatusCode.Unknown instead of NotFound for updating non-existent family.
    // Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#google.bigtable.admin.v2.BigtableTableAdmin.ModifyColumnFamilies
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task ModifyColumnFamilies_update_nonexistent_throws()
    {
        var act = () => Admin.ModifyColumnFamiliesAsync(
            new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest
            {
                Name = TablePath,
                Modifications =
                {
                    new Google.Cloud.Bigtable.Admin.V2.ModifyColumnFamiliesRequest.Types.Modification
                    {
                        Id = "nonexistent_family",
                        Update = new Google.Cloud.Bigtable.Admin.V2.ColumnFamily()
                    }
                }
            });
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    #endregion

    #region CheckAndMutate errors

    [Fact]
    public async Task CheckAndMutate_empty_mutations_throws()
    {
        // SDK validates empty mutations client-side: ArgumentException("There must be at least one mutation.")
        var act = () => Client.CheckAndMutateRowAsync(TN, "err-cam",
            RowFilters.PassAllFilter(),
            trueMutations: Array.Empty<Mutation>(),
            falseMutations: Array.Empty<Mutation>());
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CheckAndMutate_nonexistent_table_throws()
    {
        var fakeTableName = new TableName(
            _fixture.GetTableName(Table).ProjectId,
            _fixture.GetTableName(Table).InstanceId,
            "nonexistent_table_xyz");
        var act = () => Client.CheckAndMutateRowAsync(fakeTableName, "err-cam-tbl",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)) });
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    #endregion

    #region ReadModifyWrite errors

    [Fact]
    public async Task ReadModifyWrite_nonexistent_table_throws()
    {
        var fakeTableName = new TableName(
            _fixture.GetTableName(Table).ProjectId,
            _fixture.GetTableName(Table).InstanceId,
            "nonexistent_table_xyz");
        var act = () => Client.ReadModifyWriteRowAsync(fakeTableName, "err-rmw-tbl",
            ReadModifyWriteRules.Append(CF, "c", "val"));
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    // Go emulator divergence: returns StatusCode.Unknown instead of InvalidArgument for non-existent family.
    // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.Bigtable.ReadModifyWriteRow
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task ReadModifyWrite_nonexistent_family_throws()
    {
        var act = () => Client.ReadModifyWriteRowAsync(TN, "err-rmw-fam",
            ReadModifyWriteRules.Append("nonexistent_family", "c", "val"));
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    #endregion

    #region MutateRows errors

    [Fact]
    public async Task MutateRows_nonexistent_table_throws()
    {
        var fakeTableName = new TableName(
            _fixture.GetTableName(Table).ProjectId,
            _fixture.GetTableName(Table).InstanceId,
            "nonexistent_table_xyz");
        var entries = new[]
        {
            Mutations.CreateEntry("err-batch", Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)))
        };
        var act = () => Client.MutateRowsAsync(fakeTableName, entries);
        await act.Should().ThrowAsync<RpcException>();
    }

    #endregion

    #region Concurrent error isolation

    [Fact]
    public async Task Error_on_one_row_does_not_affect_other_rows()
    {
        // Write a valid row first
        await Client.MutateRowAsync(TN, "err-iso-ok",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));

        // Try writing to invalid table (should fail)
        var fakeTableName = new TableName(
            _fixture.GetTableName(Table).ProjectId,
            _fixture.GetTableName(Table).InstanceId,
            "nonexistent_table_xyz");
        try
        {
            await Client.MutateRowAsync(fakeTableName, "err-iso-bad",
                Mutations.SetCell(CF, "c", "v2", new BigtableVersion(1000)));
        }
        catch (RpcException) { }

        // The valid row should still be readable
        var row = await Client.ReadRowAsync(TN, "err-iso-ok");
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v1");
    }

    #endregion

    #region Timestamp validation

    // Go emulator divergence: returns StatusCode.Unknown instead of InvalidArgument for non-ms-aligned timestamps.
    // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.Mutation.SetCell
    //   "The timestamp must be a microsecond value with at most millisecond granularity."
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task Timestamp_not_ms_aligned_throws()
    {
        // Ref: "timestamp_micros must be millisecond-aligned (% 1000 == 0)"
        var request = new MutateRowRequest
        {
            TableName = TN.ToString(),
            RowKey = ByteString.CopyFromUtf8("err-ts-align"),
            Mutations =
            {
                new Mutation
                {
                    SetCell = new Mutation.Types.SetCell
                    {
                        FamilyName = CF,
                        ColumnQualifier = ByteString.CopyFromUtf8("c"),
                        Value = ByteString.CopyFromUtf8("v"),
                        TimestampMicros = 1001, // not ms-aligned
                    }
                }
            }
        };
        var act = () => _fixture.ServiceApiClient.MutateRowAsync(request);
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // Go emulator divergence: returns StatusCode.Unknown instead of InvalidArgument for negative timestamps.
    // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.Mutation.SetCell
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task Timestamp_negative_below_minus_one_throws()
    {
        var request = new MutateRowRequest
        {
            TableName = TN.ToString(),
            RowKey = ByteString.CopyFromUtf8("err-ts-neg"),
            Mutations =
            {
                new Mutation
                {
                    SetCell = new Mutation.Types.SetCell
                    {
                        FamilyName = CF,
                        ColumnQualifier = ByteString.CopyFromUtf8("c"),
                        Value = ByteString.CopyFromUtf8("v"),
                        TimestampMicros = -2, // invalid
                    }
                }
            }
        };
        var act = () => _fixture.ServiceApiClient.MutateRowAsync(request);
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    #endregion

    #region SampleRowKeys

    [Fact]
    public async Task SampleRowKeys_returns_response()
    {
        // Ref: SampleRowKeys returns a sample of row keys
        var response = _fixture.ServiceApiClient.SampleRowKeys(new SampleRowKeysRequest
        {
            TableName = TN.ToString()
        });
        var results = new List<SampleRowKeysResponse>();
        var e = response.GetResponseStream().GetAsyncEnumerator(default);
        while (await e.MoveNextAsync()) results.Add(e.Current);
        results.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SampleRowKeys_nonexistent_table_throws()
    {
        var act = async () =>
        {
            var fakeName = _fixture.InstanceName + "/tables/nonexistent_table_xyz";
            var response = _fixture.ServiceApiClient.SampleRowKeys(new SampleRowKeysRequest
            {
                TableName = fakeName
            });
            var e = response.GetResponseStream().GetAsyncEnumerator(default);
            while (await e.MoveNextAsync()) { }
        };
        await act.Should().ThrowAsync<RpcException>();
    }

    #endregion
}

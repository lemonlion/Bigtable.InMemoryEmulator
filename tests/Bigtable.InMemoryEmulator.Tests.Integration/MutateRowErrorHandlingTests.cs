using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for MutateRow error handling: invalid family, missing row key,
/// invalid timestamps, and boundary conditions.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutateRowErrorHandlingTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "mut-err";

    public MutateRowErrorHandlingTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private BigtableServiceApiClient Api => _fixture.ServiceApiClient;
    private TableName TN => _fixture.GetTableName(Table);

    #region Invalid family

    [Fact]
    public async Task SetCell_nonexistent_family_throws()
    {
        var act = () => Client.MutateRowAsync(TN, "me-badfam",
            Mutations.SetCell("nonexistent_fam", "c", "v", new BigtableVersion(1000)));
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    [Fact]
    public async Task DeleteFromFamily_nonexistent_throws()
    {
        var act = () => Client.MutateRowAsync(TN, "me-delfam",
            Mutations.DeleteFromFamily("nonexistent_fam"));
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    [Fact]
    public async Task DeleteFromColumn_nonexistent_family_throws()
    {
        var act = () => Client.MutateRowAsync(TN, "me-delcol",
            Mutations.DeleteFromColumn("nonexistent_fam", "c"));
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    #endregion

    #region Invalid table

    [Fact]
    public async Task Write_to_nonexistent_table_throws()
    {
        var fakeTn = _fixture.GetTableName("nonexistent-table-mut");
        var act = () => Client.MutateRowAsync(fakeTn, "r1",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await act.Should().ThrowAsync<Grpc.Core.RpcException>()
            .Where(e => e.StatusCode == Grpc.Core.StatusCode.NotFound);
    }

    #endregion

    #region Valid edge cases

    [Fact]
    public async Task SetCell_with_empty_value_succeeds()
    {
        await Client.MutateRowAsync(TN, "me-emptyval",
            Mutations.SetCell(CF, "c", "", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "me-emptyval");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().BeEmpty();
    }

    [Fact]
    public async Task SetCell_with_empty_qualifier_succeeds()
    {
        await Client.MutateRowAsync(TN, "me-emptyqual",
            Mutations.SetCell(CF, "", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "me-emptyqual");
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().BeEmpty();
    }

    [Fact]
    public async Task Multiple_mutations_same_cell()
    {
        await Client.MutateRowAsync(TN, "me-multmut",
            Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "second", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "me-multmut");
        // Last write wins at same timestamp
        row!.Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task SetCell_server_timestamp()
    {
        await Client.MutateRowAsync(TN, "me-srvts",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(-1)));
        var row = await Client.ReadRowAsync(TN, "me-srvts");
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().BeGreaterThan(0);
    }

    #endregion

    #region Raw API error cases

    [Fact]
    public async Task Raw_empty_mutations_list_throws()
    {
        var request = new MutateRowRequest
        {
            TableNameAsTableName = TN,
            RowKey = ByteString.CopyFromUtf8("me-nomut")
        };
        var act = () => Api.MutateRowAsync(request);
        await act.Should().ThrowAsync<Grpc.Core.RpcException>()
            .Where(e => e.StatusCode == Grpc.Core.StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task Raw_empty_row_key_throws()
    {
        var request = new MutateRowRequest
        {
            TableNameAsTableName = TN,
            RowKey = ByteString.Empty,
            Mutations = { new Mutation { SetCell = new Mutation.Types.SetCell
            {
                FamilyName = CF,
                ColumnQualifier = ByteString.CopyFromUtf8("c"),
                Value = ByteString.CopyFromUtf8("v"),
                TimestampMicros = 1_000_000
            }}}
        };
        var act = () => Api.MutateRowAsync(request);
        await act.Should().ThrowAsync<Grpc.Core.RpcException>()
            .Where(e => e.StatusCode == Grpc.Core.StatusCode.InvalidArgument);
    }

    #endregion

    #region Successful mutations after errors

    [Fact]
    public async Task Table_still_usable_after_error()
    {
        // Attempt invalid mutation
        try { await Client.MutateRowAsync(TN, "me-recover", Mutations.SetCell("bad_fam", "c", "v", new BigtableVersion(1000))); }
        catch { /* expected */ }
        // Valid mutation should still work
        await Client.MutateRowAsync(TN, "me-recover",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "me-recover");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Other_rows_unaffected_by_error()
    {
        await Client.MutateRowAsync(TN, "me-safe",
            Mutations.SetCell(CF, "c", "safe", new BigtableVersion(1000)));
        // Error on different row
        try { await Client.MutateRowAsync(TN, "me-err", Mutations.SetCell("bad_fam", "c", "v", new BigtableVersion(1000))); }
        catch { /* expected */ }
        // Original row should be fine
        var row = await Client.ReadRowAsync(TN, "me-safe");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("safe");
    }

    #endregion
}

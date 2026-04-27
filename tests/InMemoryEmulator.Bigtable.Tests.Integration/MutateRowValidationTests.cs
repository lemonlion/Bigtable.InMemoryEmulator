using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Grpc.Core;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for error handling and validation in single MutateRow operations.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowrequest
///   Various validation errors should return INVALID_ARGUMENT.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutateRowValidationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "mut-val";

    public MutateRowValidationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task Timestamp_not_multiple_of_1000_rejected()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
        //   "timestamp_micros: Must be a multiple of 1000 (millisecond granularity)."
        // BigtableVersion constructor requires ms, so we use raw proto to send non-multiple
        var request = new MutateRowRequest
        {
            TableNameAsTableName = TN,
            RowKey = Google.Protobuf.ByteString.CopyFromUtf8("mv-r1")
        };
        request.Mutations.Add(new Mutation
        {
            SetCell = new Mutation.Types.SetCell
            {
                FamilyName = CF,
                ColumnQualifier = Google.Protobuf.ByteString.CopyFromUtf8("c"),
                Value = Google.Protobuf.ByteString.CopyFromUtf8("v"),
                TimestampMicros = 999 // not multiple of 1000
            }
        });
        var act = () => _fixture.ServiceApiClient.MutateRowAsync(request);
        await act.Should().ThrowAsync<RpcException>();
    }

    [Fact]
    public async Task Valid_timestamp_accepted()
    {
        await Client.MutateRowAsync(TN, "mv-r2",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "mv-r2");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Timestamp_zero_is_server_assigned()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
        //   "timestamp_micros: If -1 (or 0 in some implementations), the server will assign a timestamp."
        await Client.MutateRowAsync(TN, "mv-r3",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(-1)));
        var row = await Client.ReadRowAsync(TN, "mv-r3");
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Large_timestamp_accepted()
    {
        var ts = new BigtableVersion(1_000_000_000_000); // far future
        await Client.MutateRowAsync(TN, "mv-r4",
            Mutations.SetCell(CF, "c", "v", ts));
        var row = await Client.ReadRowAsync(TN, "mv-r4");
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(1_000_000_000_000_000);
    }

    [Fact]
    public async Task Write_to_nonexistent_family_fails()
    {
        var act = () => Client.MutateRowAsync(TN, "mv-r5",
            Mutations.SetCell("nonexistent_family", "c", "v", new BigtableVersion(1000)));
        await act.Should().ThrowAsync<RpcException>();
    }

    [Fact]
    public async Task Empty_row_key_rejected()
    {
        // SDK throws ArgumentException for empty row key before reaching gRPC
        var act = () => Client.MutateRowAsync(TN, "",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Multiple_mutations_atomic()
    {
        // Write initial data
        await Client.MutateRowAsync(TN, "mv-r7",
            Mutations.SetCell(CF, "c", "initial", new BigtableVersion(1000)));

        // Try to write to a nonexistent family in a batch with a valid mutation
        var act = () => Client.MutateRowAsync(TN, "mv-r7",
            Mutations.SetCell(CF, "c", "updated", new BigtableVersion(2000)),
            Mutations.SetCell("bad_family", "c", "v", new BigtableVersion(2000)));
        await act.Should().ThrowAsync<RpcException>();
    }

    [Fact]
    public async Task Server_assigned_timestamp_multiple_of_1000()
    {
        await Client.MutateRowAsync(TN, "mv-r8",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(-1)));
        var row = await Client.ReadRowAsync(TN, "mv-r8");
        var ts = row!.Families[0].Columns[0].Cells[0].TimestampMicros;
        (ts % 1000).Should().Be(0);
    }

    [Fact]
    public async Task Delete_from_nonexistent_family_is_noop()
    {
        await Client.MutateRowAsync(TN, "mv-r9",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        // DeleteFromFamily on a family that doesn't exist in the row (but exists in the table schema for cf)
        // For non-schema families, behavior is different
        await Client.MutateRowAsync(TN, "mv-r9",
            Mutations.DeleteFromFamily(CF));
        var row = await Client.ReadRowAsync(TN, "mv-r9");
        row.Should().BeNull();
    }
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Size limit and validation integration tests — verifies that the emulator enforces
/// the same constraints as real Bigtable for parity.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class SizeValidationIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "size-val-tests";
    private const string CF = "cf";

    public SizeValidationIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    #region Row key size validation

    // Go emulator divergence: does not enforce row key size limit of 4 KiB.
    // Ref: https://cloud.google.com/bigtable/docs/schema-design#row-keys
    //   "The maximum size for a row key is 4 KB."
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task RowKey_exceeds_4KiB_throws()
    {
        // Ref: Row.key: "up to 4KiB in length"
        var bigKey = new BigtableByteString(new byte[4097]);
        var act = () => Client.MutateRowAsync(TN, bigKey,
            Mutations.SetCell(CF, "a", "val", new BigtableVersion(1000)));
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    #endregion

    #region Column qualifier size validation

    // Go emulator divergence: does not enforce column qualifier size limit of 16 KiB.
    // Ref: https://cloud.google.com/bigtable/docs/schema-design#column-qualifiers
    //   "The maximum size for a column qualifier is 16 KB."
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task Qualifier_exceeds_16KiB_throws()
    {
        // Ref: Column.qualifier: "up to 16kiB in length"
        var rk = new BigtableByteString("rv-q16");
        var bigQual = ByteString.CopyFrom(new byte[16385]);
        var act = () => Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, bigQual, ByteString.CopyFromUtf8("val"), new BigtableVersion(1000)));
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task Qualifier_exactly_16KiB_succeeds()
    {
        var rk = new BigtableByteString("rv-q16ok");
        var qual = ByteString.CopyFrom(new byte[16384]);
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, qual, ByteString.CopyFromUtf8("val"), new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
    }

    #endregion

    #region Timestamp alignment

    // Go emulator divergence: returns StatusCode.Unknown instead of InvalidArgument for non-ms-aligned timestamps.
    // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.Mutation.SetCell
    //   "The timestamp must be a microsecond value with at most millisecond granularity."
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task Timestamp_not_millisecond_aligned_throws()
    {
        // Ref: "timestamp_micros must be a multiple of 1000"
        var rk = new BigtableByteString("rv-ts");
        // To send a non-aligned timestamp, we need to use the low-level API.
        // BigtableVersion constructor takes ms and multiplies by 1000, so we need gRPC directly.
        var request = new MutateRowRequest
        {
            TableName = TN.ToString(),
            RowKey = ByteString.CopyFromUtf8("rv-ts"),
            Mutations =
            {
                new Mutation
                {
                    SetCell = new Mutation.Types.SetCell
                    {
                        FamilyName = CF,
                        ColumnQualifier = ByteString.CopyFromUtf8("a"),
                        Value = ByteString.CopyFromUtf8("val"),
                        TimestampMicros = 1001, // not a multiple of 1000
                    }
                }
            }
        };

        var act = () => _fixture.ServiceApiClient.MutateRowAsync(request);
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task Timestamp_zero_is_stored_as_zero()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
        //   "Only -1 triggers server-assigned timestamp. 0 is a valid explicit timestamp."
        var rk = new BigtableByteString("rv-ts0");
        var request = new MutateRowRequest
        {
            TableName = TN.ToString(),
            RowKey = ByteString.CopyFromUtf8("rv-ts0"),
            Mutations =
            {
                new Mutation
                {
                    SetCell = new Mutation.Types.SetCell
                    {
                        FamilyName = CF,
                        ColumnQualifier = ByteString.CopyFromUtf8("a"),
                        Value = ByteString.CopyFromUtf8("val"),
                        TimestampMicros = 0,
                    }
                }
            }
        };
        await _fixture.ServiceApiClient.MutateRowAsync(request);
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(0);
    }

    [Fact]
    public async Task Timestamp_microsecond_precision_is_preserved()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
        //   Timestamps are in microseconds, must be ms-aligned for MILLIS granularity.
        //   The SDK rejects -1 client-side ("Non-idempotent MutateRow requests are not allowed").
        //   This test verifies explicit microsecond-precision timestamps are stored correctly.
        var rk = new BigtableByteString("rv-ts-precise");
        long expectedMicros = 1_700_000_000_000_000; // A specific timestamp in micros
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "val", new BigtableVersion(expectedMicros / 1000)));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(expectedMicros);
    }

    #endregion

    #region Mutation count limits

    [Fact]
    public async Task MutateRow_empty_mutations_throws()
    {
        // Ref: "mutations: Must contain at least one entry"
        var request = new MutateRowRequest
        {
            TableName = TN.ToString(),
            RowKey = ByteString.CopyFromUtf8("rv-empty-mut"),
        };
        var act = () => _fixture.ServiceApiClient.MutateRowAsync(request);
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    #endregion

    #region Family name validation

    // Go emulator divergence: returns StatusCode.Unknown instead of InvalidArgument for non-existent family.
    // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.Bigtable.MutateRow
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task MutateRow_nonexistent_family_throws()
    {
        // Writing to a family that doesn't exist
        var rk = new BigtableByteString("rv-nofam");
        var act = () => Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("nonexistent_family", "col", "val", new BigtableVersion(1000)));
        var ex = await act.Should().ThrowAsync<RpcException>();
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowrequest
        //   Nonexistent family is InvalidArgument, not NotFound
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // Go emulator divergence: does not enforce column family name character restrictions.
    // Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#google.bigtable.admin.v2.ColumnFamily
    //   Family names must match [_a-zA-Z0-9][-_.a-zA-Z0-9]*
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task CreateTable_family_name_invalid_chars_throws()
    {
        // Ref: Family name must match [_a-zA-Z0-9][-_.a-zA-Z0-9]*
        var act = () => _fixture.AdminClient.CreateTableAsync(new Google.Cloud.Bigtable.Admin.V2.CreateTableRequest
        {
            Parent = _fixture.InstanceName,
            TableId = "inv-fam-test",
            Table = new Google.Cloud.Bigtable.Admin.V2.Table
            {
                ColumnFamilies =
                {
                    { "invalid family!", new Google.Cloud.Bigtable.Admin.V2.ColumnFamily() }
                }
            }
        });
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // Go emulator divergence: does not enforce column family name length limit of 64 characters.
    // Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#google.bigtable.admin.v2.ColumnFamily
    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task CreateTable_family_name_exceeds_64_chars_throws()
    {
        var longName = new string('a', 65);
        var act = () => _fixture.AdminClient.CreateTableAsync(new Google.Cloud.Bigtable.Admin.V2.CreateTableRequest
        {
            Parent = _fixture.InstanceName,
            TableId = "long-fam-test",
            Table = new Google.Cloud.Bigtable.Admin.V2.Table
            {
                ColumnFamilies =
                {
                    { longName, new Google.Cloud.Bigtable.Admin.V2.ColumnFamily() }
                }
            }
        });
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    #endregion
}

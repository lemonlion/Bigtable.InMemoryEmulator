using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for mutation validation: empty mutations, count limits, nonexistent table.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowrequest
///   "mutations: Required. At least one mutation must be specified."
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowsrequest
///   "entries: Required. At least one entry must be specified."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutationValidationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public MutationValidationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync("mut-val", new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private BigtableServiceApiClient ServiceApi => _fixture.ServiceApiClient;
    private TableName TN => _fixture.GetTableName("mut-val");

    #region Empty mutations

    [Fact]
    public async Task MutateRow_empty_mutations_throws()
    {
        var request = new MutateRowRequest
        {
            TableNameAsTableName = TN,
            RowKey = ByteString.CopyFromUtf8("empty-mut-row"),
        };
        // No mutations added
        var act = () => ServiceApi.MutateRowAsync(request);
        await act.Should().ThrowAsync<RpcException>();
    }

    [Fact]
    public async Task MutateRows_empty_entries_throws()
    {
        var request = new MutateRowsRequest
        {
            TableNameAsTableName = TN,
        };
        // No entries added
        var act = async () =>
        {
            var stream = ServiceApi.MutateRows(request);
            await foreach (var _ in stream.GetResponseStream()) { }
        };
        await act.Should().ThrowAsync<RpcException>();
    }

    [Fact]
    public async Task MutateRows_entry_with_empty_mutations_fails()
    {
        var request = new MutateRowsRequest
        {
            TableNameAsTableName = TN,
            Entries =
            {
                new MutateRowsRequest.Types.Entry
                {
                    RowKey = ByteString.CopyFromUtf8("no-muts"),
                    // No mutations
                }
            }
        };
        var act = async () =>
        {
            var stream = ServiceApi.MutateRows(request);
            await foreach (var _ in stream.GetResponseStream()) { }
        };
        await act.Should().ThrowAsync<RpcException>();
    }

    #endregion

    #region Empty row key

    [Fact]
    public async Task MutateRow_empty_row_key_throws()
    {
        var request = new MutateRowRequest
        {
            TableNameAsTableName = TN,
            RowKey = ByteString.Empty,
        };
        request.Mutations.Add(new Mutation
        {
            SetCell = new Mutation.Types.SetCell
            {
                FamilyName = CF,
                ColumnQualifier = ByteString.CopyFromUtf8("c"),
                Value = ByteString.CopyFromUtf8("v"),
                TimestampMicros = 1_000_000
            }
        });
        var act = () => ServiceApi.MutateRowAsync(request);
        await act.Should().ThrowAsync<RpcException>();
    }

    #endregion

    #region Nonexistent table

    [Fact]
    public async Task MutateRow_nonexistent_table_throws_NotFound()
    {
        var fakeTn = _fixture.GetTableName("no-such-table");
        var act = () => Client.MutateRowAsync(fakeTn, "r1",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task ReadRow_nonexistent_table_throws_NotFound()
    {
        var fakeTn = _fixture.GetTableName("no-such-table");
        var act = () => Client.ReadRowAsync(fakeTn, "r1");
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    #endregion

    #region Row key too large

    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task Row_key_over_4KB_throws()
    {
        // Ref: Row key max size = 4 KiB
        var bigKey = new string('x', 4097); // > 4096 bytes
        var request = new MutateRowRequest
        {
            TableNameAsTableName = TN,
            RowKey = ByteString.CopyFromUtf8(bigKey),
        };
        request.Mutations.Add(new Mutation
        {
            SetCell = new Mutation.Types.SetCell
            {
                FamilyName = CF,
                ColumnQualifier = ByteString.CopyFromUtf8("c"),
                Value = ByteString.CopyFromUtf8("v"),
                TimestampMicros = 1_000_000
            }
        });
        var act = () => ServiceApi.MutateRowAsync(request);
        await act.Should().ThrowAsync<RpcException>();
    }

    [Fact]
    public async Task Row_key_exactly_4KB_succeeds()
    {
        // Exactly 4096 bytes should be valid
        var exactKey = new string('x', 4096);
        var request = new MutateRowRequest
        {
            TableNameAsTableName = TN,
            RowKey = ByteString.CopyFromUtf8(exactKey),
        };
        request.Mutations.Add(new Mutation
        {
            SetCell = new Mutation.Types.SetCell
            {
                FamilyName = CF,
                ColumnQualifier = ByteString.CopyFromUtf8("c"),
                Value = ByteString.CopyFromUtf8("v"),
                TimestampMicros = 1_000_000
            }
        });
        await ServiceApi.MutateRowAsync(request);
    }

    #endregion

    #region Multiple mutations in single request

    [Fact]
    public async Task Multiple_setcell_mutations_same_row()
    {
        await Client.MutateRowAsync(TN, "multi-mut",
            Mutations.SetCell(CF, "c1", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c2", "v2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c3", "v3", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "multi-mut");
        row!.Families[0].Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task Set_then_delete_in_same_request()
    {
        // Set then delete within same mutation list should result in deletion
        await Client.MutateRowAsync(TN, "set-del-atomic",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "set-del-atomic",
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)),
            Mutations.DeleteFromRow());

        var row = await Client.ReadRowAsync(TN, "set-del-atomic");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Delete_then_set_in_same_request()
    {
        await Client.MutateRowAsync(TN, "del-set",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));

        // Delete then set should result in the set value
        await Client.MutateRowAsync(TN, "del-set",
            Mutations.DeleteFromRow(),
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, "del-set");
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    #endregion

    #region Batch mutation per-entry status

    [Fact]
    public async Task Batch_mixed_valid_and_invalid_family()
    {
        // One entry targets an existing family, another targets a non-existent one.
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowsrequest
        //   "Each individual row is mutated atomically as in MutateRow, but the entire batch is not executed atomically."
        // Per-entry failures in MutateRows are returned as per-entry statuses, not RPC-level errors.
        var request = new MutateRowsRequest
        {
            TableNameAsTableName = TN,
            Entries =
            {
                new MutateRowsRequest.Types.Entry
                {
                    RowKey = ByteString.CopyFromUtf8("batch-good"),
                    Mutations = { new Mutation { SetCell = new Mutation.Types.SetCell
                    {
                        FamilyName = CF,
                        ColumnQualifier = ByteString.CopyFromUtf8("c"),
                        Value = ByteString.CopyFromUtf8("yes"),
                        TimestampMicros = 1_000_000
                    }}}
                },
                new MutateRowsRequest.Types.Entry
                {
                    RowKey = ByteString.CopyFromUtf8("batch-bad"),
                    Mutations = { new Mutation { SetCell = new Mutation.Types.SetCell
                    {
                        FamilyName = "nofamily",
                        ColumnQualifier = ByteString.CopyFromUtf8("c"),
                        Value = ByteString.CopyFromUtf8("no"),
                        TimestampMicros = 1_000_000
                    }}}
                }
            }
        };

        var stream = ServiceApi.MutateRows(request);
        var responses = new List<MutateRowsResponse>();
        await foreach (var resp in stream.GetResponseStream())
            responses.Add(resp);

        // Should have one response with 2 entries; entry 0 OK, entry 1 error
        var entries = responses.SelectMany(r => r.Entries).OrderBy(e => e.Index).ToList();
        entries.Should().HaveCount(2);
        entries[0].Status.Code.Should().Be(0); // OK
        entries[1].Status.Code.Should().NotBe(0); // error (InvalidArgument)
    }

    #endregion

    #region CheckAndMutate edge cases

    [Fact]
    public async Task CaM_no_true_or_false_mutations_throws()
    {
        // Check-and-mutate with no mutations at all
        var act = () => Client.CheckAndMutateRowAsync(TN, "cam-empty",
            RowFilters.PassAllFilter(),
            trueMutations: null,
            falseMutations: null);
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task CaM_with_only_false_mutations()
    {
        // Write a row
        await Client.MutateRowAsync(TN, "cam-false-only",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));

        // CaM with predicate that matches, but only false mutations provided
        // Should match (predicate true) → no false mutations to apply → no change
        var response = await Client.CheckAndMutateRowAsync(TN, "cam-false-only",
            RowFilters.ValueRegex("nomatch"),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "c", "updated", new BigtableVersion(2000)) });

        // Predicate didn't match ("v1" ≠ "nomatch"), so false mutations apply
        response.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, "cam-false-only");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("updated");
    }

    [Fact]
    public async Task CaM_on_nonexistent_row()
    {
        var response = await Client.CheckAndMutateRowAsync(TN, "cam-norow",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "c", "created", new BigtableVersion(1000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "c", "fallback", new BigtableVersion(1000)) });

        // Row doesn't exist, so predicate produces no cells → false
        response.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, "cam-norow");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("fallback");
    }

    [Fact]
    public async Task CaM_without_predicate_filter()
    {
        // Ref: If no predicate filter is set, it always evaluates to true
        await Client.MutateRowAsync(TN, "cam-nopred",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));

        var response = await Client.CheckAndMutateRowAsync(TN, "cam-nopred",
            predicateFilter: null,
            trueMutations: new[] { Mutations.SetCell(CF, "c", "updated", new BigtableVersion(2000)) },
            falseMutations: null);

        // null predicate = always true
        response.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, "cam-nopred");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("updated");
    }

    #endregion

    #region ReadModifyWrite multiple rules

    [Fact]
    public async Task RMW_multiple_increment_rules()
    {
        // First create the row
        await Client.MutateRowAsync(TN, "rmw-multi",
            Mutations.SetCell(CF, "counter1", BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(10L)), new BigtableVersion(1000)),
            Mutations.SetCell(CF, "counter2", BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(20L)), new BigtableVersion(1000)));

        var response = await Client.ReadModifyWriteRowAsync(TN, "rmw-multi",
            ReadModifyWriteRules.Increment(CF, "counter1", 5),
            ReadModifyWriteRules.Increment(CF, "counter2", 10));

        var fam = response.Row.Families.First(f => f.Name == CF);
        var col1 = fam.Columns.First(c => c.Qualifier.ToStringUtf8() == "counter1");
        var col2 = fam.Columns.First(c => c.Qualifier.ToStringUtf8() == "counter2");

        System.Net.IPAddress.NetworkToHostOrder(BitConverter.ToInt64(col1.Cells[0].Value.ToByteArray())).Should().Be(15);
        System.Net.IPAddress.NetworkToHostOrder(BitConverter.ToInt64(col2.Cells[0].Value.ToByteArray())).Should().Be(30);
    }

    [Fact]
    public async Task RMW_multiple_append_rules()
    {
        await Client.MutateRowAsync(TN, "rmw-app-multi",
            Mutations.SetCell(CF, "log", "start", new BigtableVersion(1000)));

        var response = await Client.ReadModifyWriteRowAsync(TN, "rmw-app-multi",
            ReadModifyWriteRules.Append(CF, "log", "-mid"),
            ReadModifyWriteRules.Append(CF, "log", "-end"));

        var val = response.Row.Families.First(f => f.Name == CF)
            .Columns.First(c => c.Qualifier.ToStringUtf8() == "log")
            .Cells[0].Value.ToStringUtf8();
        val.Should().Be("start-mid-end");
    }

    [Fact]
    public async Task RMW_on_nonexistent_row_creates_it()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "rmw-create",
            ReadModifyWriteRules.Append(CF, "data", "hello"));

        response.Row.Should().NotBeNull();
        var val = response.Row.Families.First(f => f.Name == CF)
            .Columns.First(c => c.Qualifier.ToStringUtf8() == "data")
            .Cells[0].Value.ToStringUtf8();
        val.Should().Be("hello");
    }

    [Fact]
    public async Task RMW_increment_on_nonexistent_cell_starts_at_zero()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "rmw-inc-new",
            ReadModifyWriteRules.Increment(CF, "new-counter", 42));

        var val = response.Row.Families.First(f => f.Name == CF)
            .Columns.First(c => c.Qualifier.ToStringUtf8() == "new-counter")
            .Cells[0].Value;
        System.Net.IPAddress.NetworkToHostOrder(BitConverter.ToInt64(val.ToByteArray())).Should().Be(42);
    }

    #endregion
}

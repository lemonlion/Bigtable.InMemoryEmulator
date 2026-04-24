using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// ReadModifyWriteRow and CheckAndMutateRow edge case integration tests — append/increment
/// semantics, concurrent atomic operations, complex predicate patterns, multi-rule operations.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.ReadModifyWriteRowRequest
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.CheckAndMutateRowRequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class AtomicOperationEdgeCaseIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "atomic-edge-tests";
    private const string CF = "cf";

    public AtomicOperationEdgeCaseIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    #region ReadModifyWriteRow — Append

    [Fact]
    public async Task Append_to_new_cell_creates_value()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.ReadModifyWriteRule
        //   "append_value: … If the cell is not present, this value is stored as a new cell."
        var result = await Client.ReadModifyWriteRowAsync(TN, "rmw-append-new",
            ReadModifyWriteRules.Append(CF, "c", "hello"));
        result.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("hello");
    }

    [Fact]
    public async Task Append_to_existing_cell_concatenates()
    {
        await Client.MutateRowAsync(TN, "rmw-append-cat",
            Mutations.SetCell(CF, "c", "hello", new BigtableVersion(1000)));
        var result = await Client.ReadModifyWriteRowAsync(TN, "rmw-append-cat",
            ReadModifyWriteRules.Append(CF, "c", "-world"));
        result.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("hello-world");
    }

    [Fact]
    public async Task Append_empty_bytes_is_noop()
    {
        await Client.MutateRowAsync(TN, "rmw-append-empty",
            Mutations.SetCell(CF, "c", "original", new BigtableVersion(1000)));
        var result = await Client.ReadModifyWriteRowAsync(TN, "rmw-append-empty",
            ReadModifyWriteRules.Append(CF, "c", ByteString.Empty));
        result.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("original");
    }

    [Fact]
    public async Task Append_multiple_rules_to_different_columns()
    {
        var result = await Client.ReadModifyWriteRowAsync(TN, "rmw-multi-col",
            ReadModifyWriteRules.Append(CF, "a", "va"),
            ReadModifyWriteRules.Append(CF, "b", "vb"),
            ReadModifyWriteRules.Append(CF, "c", "vc"));

        var cols = result.Row.Families[0].Columns.OrderBy(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().HaveCount(3);
        cols[0].Cells[0].Value.ToStringUtf8().Should().Be("va");
        cols[1].Cells[0].Value.ToStringUtf8().Should().Be("vb");
        cols[2].Cells[0].Value.ToStringUtf8().Should().Be("vc");
    }

    [Fact]
    public async Task Append_multiple_rules_to_same_column()
    {
        // Multiple append rules targeting the same column in one call
        var result = await Client.ReadModifyWriteRowAsync(TN, "rmw-multi-append",
            ReadModifyWriteRules.Append(CF, "c", "aaa"),
            ReadModifyWriteRules.Append(CF, "c", "bbb"));
        // Both appends should be applied sequentially
        result.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("aaabbb");
    }

    [Fact]
    public async Task Append_to_different_families()
    {
        var result = await Client.ReadModifyWriteRowAsync(TN, "rmw-multi-fam",
            ReadModifyWriteRules.Append(CF, "c", "v1"),
            ReadModifyWriteRules.Append("cf2", "c", "v2"));
        result.Row.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Append_binary_data()
    {
        var binary = new byte[] { 0x00, 0x01, 0xFF, 0xFE };
        var result = await Client.ReadModifyWriteRowAsync(TN, "rmw-append-bin",
            ReadModifyWriteRules.Append(CF, "c", ByteString.CopyFrom(binary)));
        result.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray().Should().Equal(binary);
    }

    #endregion

    #region ReadModifyWriteRow — Increment

    [Fact]
    public async Task Increment_new_cell_starts_at_zero()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.ReadModifyWriteRule
        //   "increment_amount: … If the cell is not present, this value is stored as a new cell (as a 64-bit big-endian signed integer)."
        var result = await Client.ReadModifyWriteRowAsync(TN, "rmw-incr-new",
            ReadModifyWriteRules.Increment(CF, "c", 42));
        var bytes = result.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray();
        var value = ReadBigEndianInt64(bytes);
        value.Should().Be(42);
    }

    [Fact]
    public async Task Increment_accumulates()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmw-incr-acc",
            ReadModifyWriteRules.Increment(CF, "c", 10));
        await Client.ReadModifyWriteRowAsync(TN, "rmw-incr-acc",
            ReadModifyWriteRules.Increment(CF, "c", 20));
        var result = await Client.ReadModifyWriteRowAsync(TN, "rmw-incr-acc",
            ReadModifyWriteRules.Increment(CF, "c", 30));

        var value = ReadBigEndianInt64(result.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray());
        value.Should().Be(60);
    }

    [Fact]
    public async Task Increment_negative_value()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmw-incr-neg",
            ReadModifyWriteRules.Increment(CF, "c", 100));
        var result = await Client.ReadModifyWriteRowAsync(TN, "rmw-incr-neg",
            ReadModifyWriteRules.Increment(CF, "c", -30));
        var value = ReadBigEndianInt64(result.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray());
        value.Should().Be(70);
    }

    [Fact]
    public async Task Increment_by_zero()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmw-incr-zero",
            ReadModifyWriteRules.Increment(CF, "c", 42));
        var result = await Client.ReadModifyWriteRowAsync(TN, "rmw-incr-zero",
            ReadModifyWriteRules.Increment(CF, "c", 0));
        var value = ReadBigEndianInt64(result.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray());
        value.Should().Be(42);
    }

    [Fact]
    public async Task Increment_multiple_columns()
    {
        var result = await Client.ReadModifyWriteRowAsync(TN, "rmw-incr-mcol",
            ReadModifyWriteRules.Increment(CF, "a", 1),
            ReadModifyWriteRules.Increment(CF, "b", 2),
            ReadModifyWriteRules.Increment(CF, "c", 3));

        var cols = result.Row.Families[0].Columns.OrderBy(c => c.Qualifier.ToStringUtf8()).ToList();
        ReadBigEndianInt64(cols[0].Cells[0].Value.ToByteArray()).Should().Be(1);
        ReadBigEndianInt64(cols[1].Cells[0].Value.ToByteArray()).Should().Be(2);
        ReadBigEndianInt64(cols[2].Cells[0].Value.ToByteArray()).Should().Be(3);
    }

    [Fact]
    public async Task Increment_and_append_different_columns()
    {
        var result = await Client.ReadModifyWriteRowAsync(TN, "rmw-mix",
            ReadModifyWriteRules.Increment(CF, "counter", 1),
            ReadModifyWriteRules.Append(CF, "log", "event-1"));

        var counter = result.Row.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "counter");
        var log = result.Row.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "log");
        ReadBigEndianInt64(counter.Cells[0].Value.ToByteArray()).Should().Be(1);
        log.Cells[0].Value.ToStringUtf8().Should().Be("event-1");
    }

    [Fact]
    public async Task ReadModifyWrite_returns_modified_row()
    {
        // Ref: "The modified cells are returned in the response regardless of whether any filter is applied."
        await Client.MutateRowAsync(TN, "rmw-return",
            Mutations.SetCell(CF, "existing", "val", new BigtableVersion(1000)));

        var result = await Client.ReadModifyWriteRowAsync(TN, "rmw-return",
            ReadModifyWriteRules.Append(CF, "new", "appended"));

        // Response should contain only the modified cell, not existing ones
        var cols = result.Row.Families[0].Columns;
        cols.Should().Contain(c => c.Qualifier.ToStringUtf8() == "new");
    }

    #endregion

    #region CheckAndMutateRow — Predicate patterns

    [Fact]
    public async Task CheckAndMutate_no_predicate_checks_row_existence()
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#google.bigtable.v2.CheckAndMutateRowRequest
        //   "If predicate_filter is not set, the check returns true if the row exists."
        await Client.MutateRowAsync(TN, "cam-nofilter",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var response = await Client.CheckAndMutateRowAsync(TN, "cam-nofilter",
            predicateFilter: null,
            trueMutations: new[] { Mutations.SetCell(CF, "matched", "yes", new BigtableVersion(2000)) },
            falseMutations: null);
        response.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAndMutate_no_predicate_nonexistent_row_returns_false()
    {
        var response = await Client.CheckAndMutateRowAsync(TN, "cam-nopred-norow",
            predicateFilter: null,
            trueMutations: new[] { Mutations.SetCell(CF, "a", "t", new BigtableVersion(1000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "a", "f", new BigtableVersion(1000)) });
        response.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, "cam-nopred-norow");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("f");
    }

    [Fact]
    public async Task CheckAndMutate_with_timestamp_range_predicate()
    {
        await Client.MutateRowAsync(TN, "cam-tsrange",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(5000)));

        // Predicate: cell exists in timestamp range [4000ms, 6000ms)
        var tsFilter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                StartTimestampMicros = 4_000_000,
                EndTimestampMicros = 6_000_000,
            }
        };
        var response = await Client.CheckAndMutateRowAsync(TN, "cam-tsrange",
            tsFilter,
            trueMutations: new[] { Mutations.SetCell(CF, "result", "found", new BigtableVersion(7000)) });
        response.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAndMutate_with_family_filter_predicate()
    {
        await Client.MutateRowAsync(TN, "cam-famfilt",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));

        // Predicate: cell in cf2 → false (only cf exists)
        var response = await Client.CheckAndMutateRowAsync(TN, "cam-famfilt",
            RowFilters.FamilyNameExact("cf2"),
            trueMutations: new[] { Mutations.SetCell(CF, "t", "yes", new BigtableVersion(2000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "f", "no", new BigtableVersion(2000)) });
        response.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAndMutate_true_branch_multiple_mutations()
    {
        await Client.MutateRowAsync(TN, "cam-multi-true",
            Mutations.SetCell(CF, "flag", "on", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, "cam-multi-true",
            RowFilters.PassAllFilter(),
            trueMutations: new[]
            {
                Mutations.SetCell(CF, "a", "va", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "b", "vb", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "c", "vc", new BigtableVersion(2000)),
            });

        var row = await Client.ReadRowAsync(TN, "cam-multi-true");
        row!.Families[0].Columns.Should().HaveCount(4); // flag + a + b + c
    }

    [Fact]
    public async Task CheckAndMutate_true_branch_with_delete_from_row()
    {
        await Client.MutateRowAsync(TN, "cam-del-row",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var response = await Client.CheckAndMutateRowAsync(TN, "cam-del-row",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.DeleteFromRow() });
        response.PredicateMatched.Should().BeTrue();

        var row = await Client.ReadRowAsync(TN, "cam-del-row");
        row.Should().BeNull();
    }

    [Fact]
    public async Task CheckAndMutate_false_branch_creates_row()
    {
        // Row doesn't exist → false branch creates it
        var response = await Client.CheckAndMutateRowAsync(TN, "cam-false-create",
            RowFilters.PassAllFilter(), // no cells → no match → false
            trueMutations: null,
            falseMutations: new[]
            {
                Mutations.SetCell(CF, "status", "initialized", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "version", "1", new BigtableVersion(1000)),
            });
        response.PredicateMatched.Should().BeFalse();

        var row = await Client.ReadRowAsync(TN, "cam-false-create");
        row!.Families[0].Columns.Should().HaveCount(2);
    }

    [Fact]
    public async Task CheckAndMutate_interleave_predicate()
    {
        await Client.MutateRowAsync(TN, "cam-interleave",
            Mutations.SetCell(CF, "a", "va", new BigtableVersion(1000)));

        // Predicate: interleave of column "a" and column "nonexistent"
        // Column "a" exists → predicate matches
        var response = await Client.CheckAndMutateRowAsync(TN, "cam-interleave",
            RowFilters.Interleave(
                RowFilters.ColumnQualifierExact("a"),
                RowFilters.ColumnQualifierExact("nonexistent")),
            trueMutations: new[] { Mutations.SetCell(CF, "result", "matched", new BigtableVersion(2000)) });
        response.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAndMutate_block_all_predicate_always_false()
    {
        await Client.MutateRowAsync(TN, "cam-blockall",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var response = await Client.CheckAndMutateRowAsync(TN, "cam-blockall",
            RowFilters.BlockAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "t", "yes", new BigtableVersion(2000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "f", "no", new BigtableVersion(2000)) });
        response.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAndMutate_pass_all_predicate_on_existing_row_is_true()
    {
        await Client.MutateRowAsync(TN, "cam-passall",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var response = await Client.CheckAndMutateRowAsync(TN, "cam-passall",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "t", "yes", new BigtableVersion(2000)) });
        response.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAndMutate_preserves_existing_data()
    {
        await Client.MutateRowAsync(TN, "cam-preserve",
            Mutations.SetCell(CF, "existing", "keep-me", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, "cam-preserve",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "new", "added", new BigtableVersion(2000)) });

        var row = await Client.ReadRowAsync(TN, "cam-preserve");
        row!.Families[0].Columns.Should().HaveCount(2);
        row.Families[0].Columns.Should().Contain(c => c.Qualifier.ToStringUtf8() == "existing");
        row.Families[0].Columns.Should().Contain(c => c.Qualifier.ToStringUtf8() == "new");
    }

    [Fact]
    public async Task CheckAndMutate_cross_family_mutation()
    {
        await Client.MutateRowAsync(TN, "cam-xfam",
            Mutations.SetCell(CF, "flag", "set", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, "cam-xfam",
            RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ColumnQualifierExact("flag")),
            trueMutations: new[] { Mutations.SetCell("cf2", "result", "done", new BigtableVersion(2000)) });

        var row = await Client.ReadRowAsync(TN, "cam-xfam");
        row!.Families.Should().HaveCount(2);
        row.Families.Should().Contain(f => f.Name == "cf2");
    }

    #endregion

    #region Sequential atomic operations

    [Fact]
    public async Task CheckAndMutate_twice_on_same_row()
    {
        // First CAM creates the row
        await Client.CheckAndMutateRowAsync(TN, "cam-twice",
            RowFilters.PassAllFilter(),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "step", "1", new BigtableVersion(1000)) });

        // Second CAM sees the row and applies true branch
        var response = await Client.CheckAndMutateRowAsync(TN, "cam-twice",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "step", "2", new BigtableVersion(2000)) },
            falseMutations: null);

        response.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, "cam-twice");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("2");
    }

    [Fact]
    public async Task ReadModifyWrite_then_read_is_consistent()
    {
        await Client.ReadModifyWriteRowAsync(TN, "rmw-consist",
            ReadModifyWriteRules.Append(CF, "c", "first "));
        await Client.ReadModifyWriteRowAsync(TN, "rmw-consist",
            ReadModifyWriteRules.Append(CF, "c", "second"));

        var row = await Client.ReadRowAsync(TN, "rmw-consist");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("first second");
    }

    #endregion

    private static long ReadBigEndianInt64(byte[] bytes)
    {
        if (bytes.Length != 8) return 0;
        if (BitConverter.IsLittleEndian)
        {
            var reversed = bytes.Reverse().ToArray();
            return BitConverter.ToInt64(reversed, 0);
        }
        return BitConverter.ToInt64(bytes, 0);
    }
}

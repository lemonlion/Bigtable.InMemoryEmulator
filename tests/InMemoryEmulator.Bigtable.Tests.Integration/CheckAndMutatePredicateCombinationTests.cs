using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for CheckAndMutate with various predicate filter combinations
/// including nested chains, interleaves, and edge-case predicates.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#checkandmutaterowrequest
///   "predicateFilter: applied to the row, if specified. If the filter returns any cells,
///    the true_mutations are applied, false_mutations otherwise."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CheckAndMutatePredicateCombinationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public CheckAndMutatePredicateCombinationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync("cam-pred", new[] { CF, CF2 });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("cam-pred");

    #region Null/absent predicate

    [Fact]
    public async Task No_predicate_on_existing_row_matches_true()
    {
        // Ref: If no predicate_filter is provided, the check will pass if the row exists
        await Client.MutateRowAsync(TN, "cam-np1",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cam-np1",
            predicateFilter: null,
            trueMutations: new[] { Mutations.SetCell(CF, "flag", "true", new BigtableVersion(2000)) },
            falseMutations: null);
        resp.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task No_predicate_on_nonexistent_row_matches_false()
    {
        var resp = await Client.CheckAndMutateRowAsync(TN, "cam-np2",
            predicateFilter: null,
            trueMutations: new[] { Mutations.SetCell(CF, "flag", "true", new BigtableVersion(2000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "flag", "false", new BigtableVersion(2000)) });
        resp.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task No_predicate_no_true_mutations_on_existing_row()
    {
        await Client.MutateRowAsync(TN, "cam-np3",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cam-np3",
            predicateFilter: null,
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "flag", "f", new BigtableVersion(2000)) });
        resp.PredicateMatched.Should().BeTrue();
        // No true mutations → nothing changes
        var row = await Client.ReadRowAsync(TN, "cam-np3");
        row!.Families.SelectMany(f => f.Columns).Any(c => c.Qualifier.ToStringUtf8() == "flag").Should().BeFalse();
    }

    #endregion

    #region Chain predicates

    [Fact]
    public async Task Chain_family_and_value_matches()
    {
        await Client.MutateRowAsync(TN, "cam-ch1",
            Mutations.SetCell(CF, "status", "active", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "other", "x", new BigtableVersion(1000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cam-ch1",
            RowFilters.Chain(RowFilters.FamilyNameRegex(CF), RowFilters.ValueRegex("active")),
            trueMutations: new[] { Mutations.SetCell(CF, "matched", "yes", new BigtableVersion(2000)) },
            falseMutations: null);
        resp.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Chain_family_and_value_no_match()
    {
        await Client.MutateRowAsync(TN, "cam-ch2",
            Mutations.SetCell(CF, "status", "inactive", new BigtableVersion(1000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cam-ch2",
            RowFilters.Chain(RowFilters.FamilyNameRegex(CF), RowFilters.ValueRegex("active")),
            trueMutations: new[] { Mutations.SetCell(CF, "matched", "yes", new BigtableVersion(2000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "matched", "no", new BigtableVersion(2000)) });
        resp.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task Triple_chain_qualifier_family_value()
    {
        await Client.MutateRowAsync(TN, "cam-ch3",
            Mutations.SetCell(CF, "role", "admin", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "name", "bob", new BigtableVersion(1000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cam-ch3",
            RowFilters.Chain(
                RowFilters.FamilyNameRegex(CF),
                RowFilters.ColumnQualifierExact("role"),
                RowFilters.ValueRegex("admin")),
            trueMutations: new[] { Mutations.SetCell(CF, "admin", "true", new BigtableVersion(2000)) },
            falseMutations: null);
        resp.PredicateMatched.Should().BeTrue();
    }

    #endregion

    #region Interleave predicates

    [Fact]
    public async Task Interleave_matches_if_either_branch_matches()
    {
        await Client.MutateRowAsync(TN, "cam-il1",
            Mutations.SetCell(CF, "status", "pending", new BigtableVersion(1000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cam-il1",
            RowFilters.Interleave(
                RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueRegex("active")),
                RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueRegex("pending"))),
            trueMutations: new[] { Mutations.SetCell(CF, "found", "yes", new BigtableVersion(2000)) },
            falseMutations: null);
        resp.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Interleave_no_match_when_neither_branch_matches()
    {
        await Client.MutateRowAsync(TN, "cam-il2",
            Mutations.SetCell(CF, "status", "unknown", new BigtableVersion(1000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cam-il2",
            RowFilters.Interleave(
                RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueRegex("active")),
                RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueRegex("pending"))),
            trueMutations: new[] { Mutations.SetCell(CF, "found", "yes", new BigtableVersion(2000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "found", "no", new BigtableVersion(2000)) });
        resp.PredicateMatched.Should().BeFalse();
    }

    #endregion

    #region Condition predicates

    [Fact]
    public async Task Condition_filter_as_predicate()
    {
        await Client.MutateRowAsync(TN, "cam-cond1",
            Mutations.SetCell(CF, "level", "high", new BigtableVersion(1000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cam-cond1",
            RowFilters.Condition(
                RowFilters.Chain(RowFilters.ColumnQualifierExact("level"), RowFilters.ValueRegex("high")),
                RowFilters.PassAllFilter(),
                RowFilters.BlockAllFilter()),
            trueMutations: new[] { Mutations.SetCell(CF, "checked", "1", new BigtableVersion(2000)) },
            falseMutations: null);
        resp.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Condition_filter_false_branch_blocks()
    {
        await Client.MutateRowAsync(TN, "cam-cond2",
            Mutations.SetCell(CF, "level", "low", new BigtableVersion(1000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cam-cond2",
            RowFilters.Condition(
                RowFilters.Chain(RowFilters.ColumnQualifierExact("level"), RowFilters.ValueRegex("high")),
                RowFilters.PassAllFilter(),
                RowFilters.BlockAllFilter()),
            trueMutations: new[] { Mutations.SetCell(CF, "checked", "1", new BigtableVersion(2000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "checked", "0", new BigtableVersion(2000)) });
        resp.PredicateMatched.Should().BeFalse();
    }

    #endregion

    #region Block/Pass all predicates

    [Fact]
    public async Task BlockAll_always_false()
    {
        await Client.MutateRowAsync(TN, "cam-block",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cam-block",
            RowFilters.BlockAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "f", "t", new BigtableVersion(2000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "f", "f", new BigtableVersion(2000)) });
        resp.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task PassAll_on_existing_row_true()
    {
        await Client.MutateRowAsync(TN, "cam-pass",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cam-pass",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "f", "t", new BigtableVersion(2000)) },
            falseMutations: null);
        resp.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task PassAll_on_empty_row_false()
    {
        var resp = await Client.CheckAndMutateRowAsync(TN, "cam-pass-empty",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "f", "t", new BigtableVersion(2000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "f", "f", new BigtableVersion(2000)) });
        resp.PredicateMatched.Should().BeFalse();
    }

    #endregion

    #region Cross-family predicates

    [Fact]
    public async Task Predicate_checks_cf2_mutates_cf1()
    {
        await Client.MutateRowAsync(TN, "cam-xf1",
            Mutations.SetCell(CF2, "trigger", "go", new BigtableVersion(1000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cam-xf1",
            RowFilters.Chain(RowFilters.FamilyNameRegex(CF2), RowFilters.ValueRegex("go")),
            trueMutations: new[] { Mutations.SetCell(CF, "done", "yes", new BigtableVersion(2000)) },
            falseMutations: null);
        resp.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, "cam-xf1");
        row!.Families.First(f => f.Name == CF).Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("yes");
    }

    [Fact]
    public async Task Predicate_on_family_that_doesnt_exist()
    {
        await Client.MutateRowAsync(TN, "cam-xf2",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cam-xf2",
            RowFilters.Chain(RowFilters.FamilyNameRegex(CF2), RowFilters.ValueRegex("anything")),
            trueMutations: new[] { Mutations.SetCell(CF, "f", "t", new BigtableVersion(2000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "f", "f", new BigtableVersion(2000)) });
        resp.PredicateMatched.Should().BeFalse();
    }

    #endregion

    #region CaM with delete mutations

    [Fact]
    public async Task True_branch_deletes_column()
    {
        await Client.MutateRowAsync(TN, "cam-del1",
            Mutations.SetCell(CF, "keep", "yes", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "remove", "bye", new BigtableVersion(1000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cam-del1",
            RowFilters.Chain(RowFilters.ColumnQualifierExact("keep"), RowFilters.ValueRegex("yes")),
            trueMutations: new[] { Mutations.DeleteFromColumn(CF, "remove") },
            falseMutations: null);
        resp.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, "cam-del1");
        row!.Families[0].Columns.Should().ContainSingle();
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("keep");
    }

    [Fact]
    public async Task False_branch_deletes_entire_row()
    {
        await Client.MutateRowAsync(TN, "cam-del2",
            Mutations.SetCell(CF, "status", "inactive", new BigtableVersion(1000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cam-del2",
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueRegex("active")),
            trueMutations: null,
            falseMutations: new[] { Mutations.DeleteFromRow() });
        resp.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, "cam-del2");
        row.Should().BeNull();
    }

    [Fact]
    public async Task CaM_with_multiple_true_mutations()
    {
        await Client.MutateRowAsync(TN, "cam-multi",
            Mutations.SetCell(CF, "trigger", "go", new BigtableVersion(1000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cam-multi",
            RowFilters.Chain(RowFilters.ColumnQualifierExact("trigger"), RowFilters.ValueRegex("go")),
            trueMutations: new[]
            {
                Mutations.SetCell(CF, "a", "1", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "b", "2", new BigtableVersion(2000)),
                Mutations.SetCell(CF2, "c", "3", new BigtableVersion(2000))
            },
            falseMutations: null);
        resp.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, "cam-multi");
        row!.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task CaM_idempotent_repeated_call()
    {
        await Client.MutateRowAsync(TN, "cam-idem",
            Mutations.SetCell(CF, "counter", "0", new BigtableVersion(1000)));
        // First call should match and set
        await Client.CheckAndMutateRowAsync(TN, "cam-idem",
            RowFilters.Chain(RowFilters.ColumnQualifierExact("counter"), RowFilters.CellsPerColumnLimit(1), RowFilters.ValueRegex("0")),
            trueMutations: new[] { Mutations.SetCell(CF, "counter", "1", new BigtableVersion(2000)) },
            falseMutations: null);
        // Second call with same predicate should not match (latest value is now "1")
        var resp2 = await Client.CheckAndMutateRowAsync(TN, "cam-idem",
            RowFilters.Chain(RowFilters.ColumnQualifierExact("counter"), RowFilters.CellsPerColumnLimit(1), RowFilters.ValueRegex("0")),
            trueMutations: new[] { Mutations.SetCell(CF, "counter", "2", new BigtableVersion(3000)) },
            falseMutations: null);
        resp2.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, "cam-idem");
        row!.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "counter")
            .Cells[0].Value.ToStringUtf8().Should().Be("1");
    }

    #endregion

    #region Timestamp predicates

    [Fact]
    public async Task Predicate_with_timestamp_range()
    {
        await Client.MutateRowAsync(TN, "cam-ts1",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(5000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cam-ts1",
            RowFilters.Chain(
                new RowFilter { TimestampRangeFilter = new TimestampRange { StartTimestampMicros = 4_000_000, EndTimestampMicros = 6_000_000 } },
                RowFilters.ValueRegex("new")),
            trueMutations: new[] { Mutations.SetCell(CF, "confirmed", "yes", new BigtableVersion(6000)) },
            falseMutations: null);
        resp.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Predicate_timestamp_range_excludes_old()
    {
        await Client.MutateRowAsync(TN, "cam-ts2",
            Mutations.SetCell(CF, "c", "val", new BigtableVersion(1000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cam-ts2",
            new RowFilter { TimestampRangeFilter = new TimestampRange { StartTimestampMicros = 2_000_000, EndTimestampMicros = 5_000_000 } },
            trueMutations: new[] { Mutations.SetCell(CF, "f", "t", new BigtableVersion(5000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "f", "f", new BigtableVersion(5000)) });
        resp.PredicateMatched.Should().BeFalse();
    }

    #endregion

    #region CellsPerRow/Column limit predicates

    [Fact]
    public async Task Predicate_cells_per_column_limit()
    {
        await Client.MutateRowAsync(TN, "cam-cpl",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cam-cpl",
            RowFilters.Chain(
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueRegex("v3")),
            trueMutations: new[] { Mutations.SetCell(CF, "latest", "v3", new BigtableVersion(4000)) },
            falseMutations: null);
        resp.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Predicate_cells_per_row_limit()
    {
        await Client.MutateRowAsync(TN, "cam-crl",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cam-crl",
            RowFilters.Chain(
                RowFilters.CellsPerRowLimit(2),
                RowFilters.ValueRegex("3")),
            trueMutations: new[] { Mutations.SetCell(CF, "f", "t", new BigtableVersion(2000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "f", "f", new BigtableVersion(2000)) });
        // CellsPerRowLimit(2) returns first 2 cells: a=1, b=2; "3" not in those so false
        resp.PredicateMatched.Should().BeFalse();
    }

    #endregion
}

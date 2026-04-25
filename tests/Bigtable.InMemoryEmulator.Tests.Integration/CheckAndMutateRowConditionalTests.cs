using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Comprehensive conditional mutation tests for CheckAndMutateRow,
/// covering all predicate types, branch combinations, and edge cases.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#checkandmutaterowrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CheckAndMutateRowConditionalTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "cam-cond-tests";
    private const string CF = "cf";

    public CheckAndMutateRowConditionalTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#checkandmutaterowrequest
    //   "If the filter returns any output, the true_mutations are applied; otherwise false_mutations."
    [Fact]
    public async Task True_branch_fires_when_cell_exists()
    {
        var rk = new BigtableByteString("cam-c-exists");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "val", new BigtableVersion(1000)));

        var resp = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "result", "true", new BigtableVersion(2000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "result", "false", new BigtableVersion(2000)) });

        resp.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.SelectMany(f => f.Columns)
            .First(c => c.Qualifier.ToStringUtf8() == "result")
            .Cells[0].Value.ToStringUtf8().Should().Be("true");
    }

    [Fact]
    public async Task False_branch_fires_when_cell_does_not_exist()
    {
        var rk = new BigtableByteString("cam-c-noexist");

        var resp = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "r", "true", new BigtableVersion(1000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "r", "false", new BigtableVersion(1000)) });

        resp.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("false");
    }

    [Fact]
    public async Task BlockAll_predicate_always_fires_false_branch()
    {
        var rk = new BigtableByteString("cam-c-block");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "val", new BigtableVersion(1000)));

        var resp = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.BlockAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "r", "true", new BigtableVersion(2000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "r", "false", new BigtableVersion(2000)) });

        resp.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task PassAll_predicate_fires_true_when_row_exists()
    {
        var rk = new BigtableByteString("cam-c-pass");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "val", new BigtableVersion(1000)));

        var resp = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "r", "true", new BigtableVersion(2000)) });

        resp.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task ValueExact_match_fires_true()
    {
        var rk = new BigtableByteString("cam-c-vexact");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "status", "active", new BigtableVersion(1000)));

        var resp = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.ValueExact("active"),
            trueMutations: new[] { Mutations.SetCell(CF, "r", "matched", new BigtableVersion(2000)) });

        resp.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task ValueExact_mismatch_fires_false()
    {
        var rk = new BigtableByteString("cam-c-vmiss");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "status", "inactive", new BigtableVersion(1000)));

        var resp = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.ValueExact("active"),
            trueMutations: new[] { Mutations.SetCell(CF, "r", "true", new BigtableVersion(2000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "r", "false", new BigtableVersion(2000)) });

        resp.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task ColumnQualifier_predicate_matches_specific_column()
    {
        var rk = new BigtableByteString("cam-c-colp");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "target", "val", new BigtableVersion(1000)));

        var resp = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.ColumnQualifierExact("target"),
            trueMutations: new[] { Mutations.SetCell(CF, "r", "found", new BigtableVersion(2000)) });

        resp.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task ColumnQualifier_predicate_no_match_fires_false()
    {
        var rk = new BigtableByteString("cam-c-colnm");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "other", "val", new BigtableVersion(1000)));

        var resp = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.ColumnQualifierExact("target"),
            trueMutations: new[] { Mutations.SetCell(CF, "r", "true", new BigtableVersion(2000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "r", "false", new BigtableVersion(2000)) });

        resp.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task Chain_predicate_column_and_value()
    {
        var rk = new BigtableByteString("cam-c-chain");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "flag", "on", new BigtableVersion(1000)));

        var resp = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("flag"),
                RowFilters.ValueExact("on")),
            trueMutations: new[] { Mutations.SetCell(CF, "r", "matched", new BigtableVersion(2000)) });

        resp.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Chain_predicate_partial_match_fires_false()
    {
        var rk = new BigtableByteString("cam-c-chainf");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "flag", "off", new BigtableVersion(1000)));

        var resp = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("flag"),
                RowFilters.ValueExact("on")),
            trueMutations: new[] { Mutations.SetCell(CF, "r", "true", new BigtableVersion(2000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "r", "false", new BigtableVersion(2000)) });

        resp.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task Interleave_predicate_matches_any_branch()
    {
        var rk = new BigtableByteString("cam-c-intlv");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "beta", new BigtableVersion(1000)));

        var resp = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.Interleave(
                RowFilters.ValueExact("alpha"),
                RowFilters.ValueExact("beta")),
            trueMutations: new[] { Mutations.SetCell(CF, "r", "found", new BigtableVersion(2000)) });

        resp.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task True_mutations_can_delete()
    {
        var rk = new BigtableByteString("cam-c-tdel");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "val", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.DeleteFromRow() });

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().BeNull();
    }

    [Fact]
    public async Task False_mutations_can_create_row()
    {
        var rk = new BigtableByteString("cam-c-fcreate");

        await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.PassAllFilter(),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "col", "created", new BigtableVersion(1000)) });

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("created");
    }

    [Fact]
    public async Task Multiple_true_mutations_applied_atomically()
    {
        var rk = new BigtableByteString("cam-c-multi");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "val", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.PassAllFilter(),
            trueMutations: new[]
            {
                Mutations.SetCell(CF, "a", "1", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "b", "2", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "c", "3", new BigtableVersion(2000)),
            });

        var row = await Client.ReadRowAsync(TN, rk);
        var cols = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("a");
        cols.Should().Contain("b");
        cols.Should().Contain("c");
    }

    [Fact]
    public async Task Predicate_with_cells_per_column_limit()
    {
        var rk = new BigtableByteString("cam-c-cpcl");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v2", new BigtableVersion(2000)));

        // CellsPerColumnLimit(1) + ValueExact("v2") should match only the latest cell
        var resp = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.Chain(
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueExact("v2")),
            trueMutations: new[] { Mutations.SetCell(CF, "r", "latest", new BigtableVersion(3000)) });

        resp.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Predicate_with_timestamp_range()
    {
        var rk = new BigtableByteString("cam-c-tsrange");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "new", new BigtableVersion(5000)));

        // Ref: TimestampRange start_timestamp_micros is inclusive, end is exclusive
        var resp = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.TimestampRange(
                new DateTime(1970, 1, 1, 0, 0, 4, DateTimeKind.Utc),
                new DateTime(1970, 1, 1, 0, 0, 6, DateTimeKind.Utc)),
            trueMutations: new[] { Mutations.SetCell(CF, "r", "found-new", new BigtableVersion(7000)) });

        resp.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Predicate_with_family_name_regex()
    {
        var rk = new BigtableByteString("cam-c-famreg");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("cf2", "col", "val", new BigtableVersion(1000)));

        var resp = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.FamilyNameRegex("cf2"),
            trueMutations: new[] { Mutations.SetCell(CF, "r", "found", new BigtableVersion(2000)) });

        resp.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Predicate_with_family_regex_no_match()
    {
        var rk = new BigtableByteString("cam-c-famrn");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "val", new BigtableVersion(1000)));

        var resp = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.FamilyNameRegex("nonexistent"),
            trueMutations: new[] { Mutations.SetCell(CF, "r", "t", new BigtableVersion(2000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "r", "f", new BigtableVersion(2000)) });

        resp.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task True_branch_with_delete_from_column()
    {
        var rk = new BigtableByteString("cam-c-delcol");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "keep", "yes", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "remove", "gone", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.ColumnQualifierExact("remove"),
            trueMutations: new[] { Mutations.DeleteFromColumn(CF, "remove") });

        var row = await Client.ReadRowAsync(TN, rk);
        var cols = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("keep");
        cols.Should().NotContain("remove");
    }

    [Fact]
    public async Task True_branch_with_delete_from_family()
    {
        var rk = new BigtableByteString("cam-c-delfam");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("cf2", "b", "2", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.DeleteFromFamily("cf2") });

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.Should().HaveCount(1);
        row.Families[0].Name.Should().Be(CF);
    }

    [Fact]
    public async Task Repeated_check_and_mutate_idempotent_result()
    {
        var rk = new BigtableByteString("cam-c-idem");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "counter", "0", new BigtableVersion(1000)));

        // First call: value is "0", set to "1"
        var resp1 = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.Chain(RowFilters.ColumnQualifierExact("counter"), RowFilters.ValueExact("0")),
            trueMutations: new[] { Mutations.SetCell(CF, "counter", "1", new BigtableVersion(2000)) });

        // Second call: value is now "1", not "0" → false branch
        // Must use CellsPerColumnLimit(1) to only check latest version, since old "0" cell still exists
        var resp2 = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.Chain(RowFilters.ColumnQualifierExact("counter"), RowFilters.CellsPerColumnLimit(1), RowFilters.ValueExact("0")),
            trueMutations: new[] { Mutations.SetCell(CF, "counter", "2", new BigtableVersion(3000)) },
            falseMutations: null);

        resp1.PredicateMatched.Should().BeTrue();
        resp2.PredicateMatched.Should().BeFalse();

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "counter")
            .Cells[0].Value.ToStringUtf8().Should().Be("1");
    }

    [Fact]
    public async Task Condition_filter_as_predicate()
    {
        var rk = new BigtableByteString("cam-c-condp");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "val", new BigtableVersion(1000)));

        // Condition filter: if value matches "val" → pass, else block
        var resp = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.Condition(
                RowFilters.ValueExact("val"),
                RowFilters.PassAllFilter(),
                RowFilters.BlockAllFilter()),
            trueMutations: new[] { Mutations.SetCell(CF, "r", "ok", new BigtableVersion(2000)) });

        resp.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task True_and_false_both_null_throws()
    {
        // Ref: SDK client-side validation requires at least one mutation
        var rk = new BigtableByteString("cam-c-noop");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "val", new BigtableVersion(1000)));

        var act = () => Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.PassAllFilter(),
            trueMutations: null, falseMutations: null);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Predicate_on_multiple_cells_any_match_triggers_true()
    {
        var rk = new BigtableByteString("cam-c-anymatch");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "foo", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "b", "bar", new BigtableVersion(1000)));

        // ValueExact("bar") matches cell "b" → predicate returns output → true
        var resp = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.ValueExact("bar"),
            trueMutations: new[] { Mutations.SetCell(CF, "r", "yes", new BigtableVersion(2000)) });

        resp.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task True_branch_mutation_to_different_family()
    {
        var rk = new BigtableByteString("cam-c-difffam");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "val", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell("cf2", "result", "ok", new BigtableVersion(2000)) });

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.Should().HaveCount(2);
        row.Families.Should().Contain(f => f.Name == "cf2");
    }

    [Fact]
    public async Task Value_regex_predicate_partial_match()
    {
        var rk = new BigtableByteString("cam-c-partial");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "msg", "hello-world-2024", new BigtableVersion(1000)));

        var resp = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.ValueRegex("hello.*2024"),
            trueMutations: new[] { Mutations.SetCell(CF, "r", "matched", new BigtableVersion(2000)) });

        resp.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Strip_value_transformer_in_predicate_still_matches()
    {
        var rk = new BigtableByteString("cam-c-strip");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "val", new BigtableVersion(1000)));

        // StripValueTransformer still outputs cells (with empty values), so predicate matches
        var resp = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.StripValueTransformer(),
            trueMutations: new[] { Mutations.SetCell(CF, "r", "found", new BigtableVersion(2000)) });

        resp.PredicateMatched.Should().BeTrue();
    }
}

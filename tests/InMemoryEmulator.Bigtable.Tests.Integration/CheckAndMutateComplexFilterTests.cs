using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for CheckAndMutateRow with complex filter predicates and multiple mutations.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#checkandmutaterowrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CheckAndMutateComplexFilterTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "cam-cf-tests";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public CheckAndMutateComplexFilterTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Predicate_matches_true_branch_executed()
    {
        await Client.MutateRowAsync(TN, "cam-cf-t",
            Mutations.SetCell(CF, "c", "target", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-cf-t",
            RowFilters.ValueExact("target"),
            trueMutations: new[] { Mutations.SetCell(CF, "status", "matched", new BigtableVersion(2000)) });

        result.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, "cam-cf-t");
        row!.Families[0].Columns.Should().Contain(c => c.Qualifier.ToStringUtf8() == "status");
    }

    [Fact]
    public async Task Predicate_no_match_false_branch_executed()
    {
        await Client.MutateRowAsync(TN, "cam-cf-f",
            Mutations.SetCell(CF, "c", "other", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-cf-f",
            RowFilters.ValueExact("not-present"),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "status", "not-matched", new BigtableVersion(2000)) });

        result.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, "cam-cf-f");
        var statusCol = row!.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "status");
        statusCol.Cells[0].Value.ToStringUtf8().Should().Be("not-matched");
    }

    [Fact]
    public async Task Chain_predicate_filter()
    {
        await Client.MutateRowAsync(TN, "cam-cf-chain",
            Mutations.SetCell(CF, "type", "premium", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "score", "high", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-cf-chain",
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("type"),
                RowFilters.ValueExact("premium")),
            trueMutations: new[] { Mutations.SetCell(CF, "tier", "gold", new BigtableVersion(2000)) });

        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Interleave_predicate_filter()
    {
        await Client.MutateRowAsync(TN, "cam-cf-intlv",
            Mutations.SetCell(CF, "a", "aval", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-cf-intlv",
            RowFilters.Interleave(
                RowFilters.ValueExact("aval"),
                RowFilters.ValueExact("bval")),
            trueMutations: new[] { Mutations.SetCell(CF, "found", "yes", new BigtableVersion(2000)) });

        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task True_mutations_delete_column()
    {
        await Client.MutateRowAsync(TN, "cam-cf-del",
            Mutations.SetCell(CF, "temp", "temp-val", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "keep", "keep-val", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, "cam-cf-del",
            RowFilters.ColumnQualifierExact("temp"),
            trueMutations: new[] { Mutations.DeleteFromColumn(CF, "temp") });

        var row = await Client.ReadRowAsync(TN, "cam-cf-del");
        row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8())
            .Should().ContainSingle("keep");
    }

    [Fact]
    public async Task True_mutations_delete_family()
    {
        await Client.MutateRowAsync(TN, "cam-cf-delf",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "v2", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, "cam-cf-delf",
            RowFilters.FamilyNameExact(CF),
            trueMutations: new[] { Mutations.DeleteFromFamily(CF) });

        var row = await Client.ReadRowAsync(TN, "cam-cf-delf");
        row!.Families.Should().HaveCount(1);
        row.Families[0].Name.Should().Be("cf2");
    }

    [Fact]
    public async Task True_mutations_multiple_sets()
    {
        await Client.MutateRowAsync(TN, "cam-cf-mset",
            Mutations.SetCell(CF, "trigger", "go", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, "cam-cf-mset",
            RowFilters.ValueExact("go"),
            trueMutations: new[]
            {
                Mutations.SetCell(CF, "result1", "r1", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "result2", "r2", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "result3", "r3", new BigtableVersion(2000))
            });

        var row = await Client.ReadRowAsync(TN, "cam-cf-mset");
        var cols = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("result1");
        cols.Should().Contain("result2");
        cols.Should().Contain("result3");
    }

    [Fact]
    public async Task False_mutations_multiple_sets()
    {
        await Client.MutateRowAsync(TN, "cam-cf-fmset",
            Mutations.SetCell(CF, "check", "wrong", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, "cam-cf-fmset",
            RowFilters.ValueExact("right"),
            trueMutations: null,
            falseMutations: new[]
            {
                Mutations.SetCell(CF, "fallback1", "fb1", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "fallback2", "fb2", new BigtableVersion(2000))
            });

        var row = await Client.ReadRowAsync(TN, "cam-cf-fmset");
        var cols = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("fallback1");
        cols.Should().Contain("fallback2");
    }

    [Fact]
    public async Task Both_true_and_false_mutations_provided_true_wins()
    {
        await Client.MutateRowAsync(TN, "cam-cf-both",
            Mutations.SetCell(CF, "c", "match-me", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-cf-both",
            RowFilters.ValueExact("match-me"),
            trueMutations: new[] { Mutations.SetCell(CF, "branch", "true", new BigtableVersion(2000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "branch", "false", new BigtableVersion(2000)) });

        result.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, "cam-cf-both");
        var branchCol = row!.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "branch");
        branchCol.Cells[0].Value.ToStringUtf8().Should().Be("true");
    }

    [Fact]
    public async Task Predicate_on_nonexistent_row_false_branch()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-cf-norow",
            RowFilters.PassAllFilter(),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "created", "yes", new BigtableVersion(1000)) });

        result.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, "cam-cf-norow");
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("yes");
    }

    [Fact]
    public async Task CellsPerColumnLimit_predicate()
    {
        await Client.MutateRowAsync(TN, "cam-cf-cpcl",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-cf-cpcl",
            RowFilters.Chain(
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueExact("v2")),
            trueMutations: new[] { Mutations.SetCell(CF, "latest", "is-v2", new BigtableVersion(3000)) });

        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Family_name_predicate_cross_family()
    {
        await Client.MutateRowAsync(TN, "cam-cf-xfam",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "b", "v2", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-cf-xfam",
            RowFilters.FamilyNameExact("cf2"),
            trueMutations: new[] { Mutations.SetCell(CF, "found-cf2", "yes", new BigtableVersion(2000)) });

        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Condition_filter_as_predicate()
    {
        await Client.MutateRowAsync(TN, "cam-cf-cond",
            Mutations.SetCell(CF, "x", "high", new BigtableVersion(1000)));

        // Condition: if value "high" exists → pass through, else block
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-cf-cond",
            RowFilters.Condition(
                RowFilters.ValueExact("high"),
                RowFilters.PassAllFilter(),
                RowFilters.BlockAllFilter()),
            trueMutations: new[] { Mutations.SetCell(CF, "validated", "yes", new BigtableVersion(2000)) });

        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task StripValue_predicate_still_matches_if_cells_present()
    {
        await Client.MutateRowAsync(TN, "cam-cf-strip",
            Mutations.SetCell(CF, "c", "any-value", new BigtableVersion(1000)));

        // StripValue removes values but cells still pass through
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-cf-strip",
            RowFilters.StripValueTransformer(),
            trueMutations: new[] { Mutations.SetCell(CF, "stripped", "yes", new BigtableVersion(2000)) });

        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task ColumnRange_predicate()
    {
        await Client.MutateRowAsync(TN, "cam-cf-colr",
            Mutations.SetCell(CF, "alpha", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "beta", "v2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "gamma", "v3", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-cf-colr",
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "beta", "beta")),
            trueMutations: new[] { Mutations.SetCell(CF, "found-beta", "yes", new BigtableVersion(2000)) });

        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task ValueRange_predicate()
    {
        await Client.MutateRowAsync(TN, "cam-cf-valr",
            Mutations.SetCell(CF, "score", "50", new BigtableVersion(1000)));

        // "50" lexicographically is between "40" and "60"
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-cf-valr",
            RowFilters.ValueRange(ValueRange.Closed("40", "60")),
            trueMutations: new[] { Mutations.SetCell(CF, "in-range", "yes", new BigtableVersion(2000)) });

        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_from_row_as_true_mutation()
    {
        await Client.MutateRowAsync(TN, "cam-cf-delrow",
            Mutations.SetCell(CF, "c", "deleteme", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, "cam-cf-delrow",
            RowFilters.ValueExact("deleteme"),
            trueMutations: new[] { Mutations.DeleteFromRow() });

        var row = await Client.ReadRowAsync(TN, "cam-cf-delrow");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Multiple_true_mutations_set_and_delete()
    {
        await Client.MutateRowAsync(TN, "cam-cf-mtsd",
            Mutations.SetCell(CF, "old", "old-val", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "trigger", "go", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, "cam-cf-mtsd",
            RowFilters.ValueExact("go"),
            trueMutations: new[]
            {
                Mutations.DeleteFromColumn(CF, "old"),
                Mutations.SetCell(CF, "new", "new-val", new BigtableVersion(2000))
            });

        var row = await Client.ReadRowAsync(TN, "cam-cf-mtsd");
        var cols = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().NotContain("old");
        cols.Should().Contain("new");
        cols.Should().Contain("trigger");
    }

    [Fact]
    public async Task Regex_predicate_filter()
    {
        await Client.MutateRowAsync(TN, "cam-cf-regex",
            Mutations.SetCell(CF, "email", "test@example.com", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-cf-regex",
            RowFilters.ValueRegex(".*@example\\.com"),
            trueMutations: new[] { Mutations.SetCell(CF, "valid-email", "yes", new BigtableVersion(2000)) });

        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Timestamp_range_predicate()
    {
        var ts = new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        await Client.MutateRowAsync(TN, "cam-cf-ts",
            Mutations.SetCell(CF, "c", "val", new BigtableVersion(ts)));

        var start = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-cf-ts",
            RowFilters.TimestampRange(start, end),
            trueMutations: new[] { Mutations.SetCell(CF, "in-june", "yes", new BigtableVersion(1000)) });

        result.PredicateMatched.Should().BeTrue();
    }
}

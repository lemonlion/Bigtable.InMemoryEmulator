using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// CheckAndMutateRow predicate matrix: different predicate filter types × true/false mutation combos.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#checkandmutaterowrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CheckAndMutatePredicateMatrixTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "campm-tests";
    private const string CF = "cf";

    public CheckAndMutatePredicateMatrixTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task PassAll_predicate_with_data_returns_true()
    {
        await Client.MutateRowAsync(TN, "campm-pa",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "campm-pa",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "status", "true", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task PassAll_predicate_no_data_returns_false()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "campm-pa-empty",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)) });
        result.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task BlockAll_predicate_always_returns_false()
    {
        await Client.MutateRowAsync(TN, "campm-ba",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "campm-ba",
            RowFilters.BlockAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "never", "never", new BigtableVersion(2000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "status", "false", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeFalse();

        var row = await Client.ReadRowAsync(TN, "campm-ba");
        var quals = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().Contain("status");
        quals.Should().NotContain("never");
    }

    [Fact]
    public async Task ValueExact_predicate_match()
    {
        await Client.MutateRowAsync(TN, "campm-ve",
            Mutations.SetCell(CF, "c", "target", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "campm-ve",
            RowFilters.ValueExact("target"),
            trueMutations: new[] { Mutations.SetCell(CF, "matched", "yes", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task ValueExact_predicate_no_match()
    {
        await Client.MutateRowAsync(TN, "campm-ve-f",
            Mutations.SetCell(CF, "c", "other", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "campm-ve-f",
            RowFilters.ValueExact("target"),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "matched", "no", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task ValueRegex_predicate_match()
    {
        await Client.MutateRowAsync(TN, "campm-vreg",
            Mutations.SetCell(CF, "c", "hello-world", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "campm-vreg",
            RowFilters.ValueRegex("hello-.*"),
            trueMutations: new[] { Mutations.SetCell(CF, "matched", "yes", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task FamilyName_predicate_match()
    {
        await Client.MutateRowAsync(TN, "campm-fn",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "campm-fn",
            RowFilters.FamilyNameExact(CF),
            trueMutations: new[] { Mutations.SetCell(CF, "matched", "yes", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task FamilyName_predicate_no_match()
    {
        await Client.MutateRowAsync(TN, "campm-fn-f",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "campm-fn-f",
            RowFilters.FamilyNameExact("nonexistent"),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "matched", "no", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task ColumnQualifier_predicate_match()
    {
        await Client.MutateRowAsync(TN, "campm-cq",
            Mutations.SetCell(CF, "target", "v", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "campm-cq",
            RowFilters.ColumnQualifierExact("target"),
            trueMutations: new[] { Mutations.SetCell(CF, "matched", "yes", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task ColumnQualifier_predicate_no_match()
    {
        await Client.MutateRowAsync(TN, "campm-cq-f",
            Mutations.SetCell(CF, "other", "v", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "campm-cq-f",
            RowFilters.ColumnQualifierExact("target"),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "matched", "no", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task Chain_predicate_both_match()
    {
        await Client.MutateRowAsync(TN, "campm-chain",
            Mutations.SetCell(CF, "target", "yes", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "campm-chain",
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("target"),
                RowFilters.ValueExact("yes")),
            trueMutations: new[] { Mutations.SetCell(CF, "matched", "yes", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Chain_predicate_second_fails()
    {
        await Client.MutateRowAsync(TN, "campm-chain-f",
            Mutations.SetCell(CF, "target", "no", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "campm-chain-f",
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("target"),
                RowFilters.ValueExact("yes")),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "matched", "no", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task Interleave_predicate_one_branch_matches()
    {
        await Client.MutateRowAsync(TN, "campm-ilv",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "campm-ilv",
            RowFilters.Interleave(
                RowFilters.ColumnQualifierExact("a"),
                RowFilters.ColumnQualifierExact("nonexistent")),
            trueMutations: new[] { Mutations.SetCell(CF, "matched", "yes", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Interleave_predicate_no_branch_matches()
    {
        await Client.MutateRowAsync(TN, "campm-ilv-f",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "campm-ilv-f",
            RowFilters.Interleave(
                RowFilters.ColumnQualifierExact("x"),
                RowFilters.ColumnQualifierExact("y")),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "matched", "no", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task True_branch_sets_cell()
    {
        await Client.MutateRowAsync(TN, "campm-tb-set",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, "campm-tb-set",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "new", "created", new BigtableVersion(2000)) });

        var row = await Client.ReadRowAsync(TN, "campm-tb-set");
        row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).Should().Contain("new");
    }

    [Fact]
    public async Task True_branch_deletes_column()
    {
        await Client.MutateRowAsync(TN, "campm-tb-del",
            Mutations.SetCell(CF, "keep", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "remove", "v", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, "campm-tb-del",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.DeleteFromColumn(CF, "remove") });

        var row = await Client.ReadRowAsync(TN, "campm-tb-del");
        row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).Should().NotContain("remove");
    }

    [Fact]
    public async Task False_branch_sets_cell()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "campm-fb-set",
            RowFilters.PassAllFilter(),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "c", "from-false", new BigtableVersion(1000)) });
        result.PredicateMatched.Should().BeFalse();

        var row = await Client.ReadRowAsync(TN, "campm-fb-set");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("from-false");
    }

    [Fact]
    public async Task True_branch_multiple_mutations()
    {
        await Client.MutateRowAsync(TN, "campm-tb-multi",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, "campm-tb-multi",
            RowFilters.PassAllFilter(),
            trueMutations: new[]
            {
                Mutations.SetCell(CF, "new1", "v1", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "new2", "v2", new BigtableVersion(2000)),
            });

        var row = await Client.ReadRowAsync(TN, "campm-tb-multi");
        var quals = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().Contain("new1");
        quals.Should().Contain("new2");
    }

    [Fact]
    public async Task CellsPerColumnLimit_predicate()
    {
        await Client.MutateRowAsync(TN, "campm-cpcl",
            Mutations.SetCell(CF, "c", "latest", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, "campm-cpcl",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "campm-cpcl",
            RowFilters.Chain(
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueExact("latest")),
            trueMutations: new[] { Mutations.SetCell(CF, "matched", "yes", new BigtableVersion(3000)) });
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task ValueRange_predicate_match()
    {
        await Client.MutateRowAsync(TN, "campm-vr",
            Mutations.SetCell(CF, "c", "medium", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "campm-vr",
            RowFilters.ValueRange(ValueRange.Closed("a", "z")),
            trueMutations: new[] { Mutations.SetCell(CF, "matched", "yes", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task ValueRange_predicate_no_match()
    {
        await Client.MutateRowAsync(TN, "campm-vr-f",
            Mutations.SetCell(CF, "c", "abc", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "campm-vr-f",
            RowFilters.ValueRange(ValueRange.Closed("xyz", "zzz")),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "matched", "no", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task ColumnRange_predicate_match()
    {
        await Client.MutateRowAsync(TN, "campm-cr",
            Mutations.SetCell(CF, "m", "v", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "campm-cr",
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "z")),
            trueMutations: new[] { Mutations.SetCell(CF, "matched", "yes", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task ColumnRange_predicate_no_match()
    {
        await Client.MutateRowAsync(TN, "campm-cr-f",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "campm-cr-f",
            RowFilters.ColumnRange(ColumnRange.Closed(CF, "m", "z")),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "matched", "no", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task TimestampRange_predicate_match()
    {
        var ts = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        await Client.MutateRowAsync(TN, "campm-ts",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(ts)));

        var result = await Client.CheckAndMutateRowAsync(TN, "campm-ts",
            RowFilters.TimestampRange(ts, ts.AddDays(1)),
            trueMutations: new[] { Mutations.SetCell(CF, "matched", "yes", new BigtableVersion(new DateTime(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc))) });
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task StripValue_predicate_still_produces_output()
    {
        await Client.MutateRowAsync(TN, "campm-strip",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        // StripValueTransformer still outputs cells (just with empty values) → predicate matches
        var result = await Client.CheckAndMutateRowAsync(TN, "campm-strip",
            RowFilters.StripValueTransformer(),
            trueMutations: new[] { Mutations.SetCell(CF, "matched", "yes", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Repeated_check_produces_consistent_results()
    {
        await Client.MutateRowAsync(TN, "campm-repeat",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        for (int i = 0; i < 5; i++)
        {
            var result = await Client.CheckAndMutateRowAsync(TN, "campm-repeat",
                RowFilters.PassAllFilter(),
                trueMutations: new[] { Mutations.SetCell(CF, $"iter-{i}", "v", new BigtableVersion((i + 2) * 1000)) });
            result.PredicateMatched.Should().BeTrue();
        }
    }

    [Fact]
    public async Task True_branch_deletes_from_row()
    {
        await Client.MutateRowAsync(TN, "campm-tb-delrow",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, "campm-tb-delrow",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.DeleteFromRow() });

        var row = await Client.ReadRowAsync(TN, "campm-tb-delrow");
        row.Should().BeNull();
    }
}

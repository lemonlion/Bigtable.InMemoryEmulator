using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for CheckAndMutateRow with various predicate patterns and mutation types.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#checkandmutaterowrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CheckAndMutateExtendedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "came-tests";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public CheckAndMutateExtendedTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });

        await Client.MutateRowAsync(TN, "cam-row1",
            Mutations.SetCell(CF, "status", "active", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "count", "5", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "cam-row2",
            Mutations.SetCell(CF, "status", "inactive", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "count", "0", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task True_branch_executes_on_match()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-row1",
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueExact("active")),
            trueMutations: new[] { Mutations.SetCell(CF, "flag", "matched", new BigtableVersion(2000)) });

        result.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, "cam-row1");
        GetLatestValue(row!, CF, "flag").Should().Be("matched");
    }

    [Fact]
    public async Task False_branch_executes_on_no_match()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-row2",
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueExact("active")),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "flag", "not-matched", new BigtableVersion(2000)) });

        result.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, "cam-row2");
        GetLatestValue(row!, CF, "flag").Should().Be("not-matched");
    }

    [Fact]
    public async Task Predicate_on_nonexistent_row()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-ghost",
            RowFilters.PassAllFilter(),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "created", "yes", new BigtableVersion(1000)) });

        result.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, "cam-ghost");
        row.Should().NotBeNull();
        GetLatestValue(row!, CF, "created").Should().Be("yes");
    }

    [Fact]
    public async Task True_branch_with_delete_mutation()
    {
        await Client.MutateRowAsync(TN, "cam-del",
            Mutations.SetCell(CF, "temp", "data", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "keep", "data", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-del",
            RowFilters.Chain(RowFilters.ColumnQualifierExact("temp"), RowFilters.ValueExact("data")),
            trueMutations: new[] { Mutations.DeleteFromColumn(CF, "temp") });

        result.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, "cam-del");
        row!.Families[0].Columns.Should().ContainSingle();
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("keep");
    }

    [Fact]
    public async Task True_branch_with_multiple_mutations()
    {
        await Client.MutateRowAsync(TN, "cam-multi",
            Mutations.SetCell(CF, "trigger", "yes", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-multi",
            RowFilters.Chain(RowFilters.ColumnQualifierExact("trigger"), RowFilters.ValueExact("yes")),
            trueMutations: new[]
            {
                Mutations.SetCell(CF, "a", "1", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "b", "2", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "c", "3", new BigtableVersion(2000))
            });

        result.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, "cam-multi");
        var cols = row!.Families.First(f => f.Name == CF).Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("a");
        cols.Should().Contain("b");
        cols.Should().Contain("c");
    }

    [Fact]
    public async Task Both_branches_only_true_executes()
    {
        await Client.MutateRowAsync(TN, "cam-both",
            Mutations.SetCell(CF, "val", "yes", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-both",
            RowFilters.Chain(RowFilters.ColumnQualifierExact("val"), RowFilters.ValueExact("yes")),
            trueMutations: new[] { Mutations.SetCell(CF, "result", "true-path", new BigtableVersion(2000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "result", "false-path", new BigtableVersion(2000)) });

        result.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, "cam-both");
        GetLatestValue(row!, CF, "result").Should().Be("true-path");
    }

    [Fact]
    public async Task Predicate_with_family_filter()
    {
        await Client.MutateRowAsync(TN, "cam-famfilt",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "v", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-famfilt",
            RowFilters.Chain(RowFilters.FamilyNameExact("cf2"), RowFilters.ColumnQualifierExact("c")),
            trueMutations: new[] { Mutations.SetCell(CF, "found", "yes", new BigtableVersion(2000)) });

        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Predicate_with_timestamp_range()
    {
        await Client.MutateRowAsync(TN, "cam-ts",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(5000)));

        var start = new DateTime(1970, 1, 1, 0, 0, 4, DateTimeKind.Utc); // 4000ms
        var end = new DateTime(1970, 1, 1, 0, 0, 6, DateTimeKind.Utc);   // 6000ms

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-ts",
            RowFilters.TimestampRange(start, end),
            trueMutations: new[] { Mutations.SetCell(CF, "found_new", "yes", new BigtableVersion(6000)) });

        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Predicate_with_value_regex()
    {
        await Client.MutateRowAsync(TN, "cam-vreg",
            Mutations.SetCell(CF, "msg", "error: something bad", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-vreg",
            RowFilters.Chain(RowFilters.ColumnQualifierExact("msg"), RowFilters.ValueRegex("error:.*")),
            trueMutations: new[] { Mutations.SetCell(CF, "has_error", "true", new BigtableVersion(2000)) });

        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task False_branch_delete()
    {
        await Client.MutateRowAsync(TN, "cam-fdel",
            Mutations.SetCell(CF, "status", "unknown", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "data", "some", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-fdel",
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueExact("valid")),
            trueMutations: null,
            falseMutations: new[] { Mutations.DeleteFromColumn(CF, "data") });

        result.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, "cam-fdel");
        var cols = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().NotContain("data");
    }

    [Fact]
    public async Task Repeated_check_and_mutate()
    {
        await Client.MutateRowAsync(TN, "cam-repeat",
            Mutations.SetCell(CF, "counter", "0", new BigtableVersion(1000)));

        for (int i = 1; i <= 5; i++)
        {
            await Client.CheckAndMutateRowAsync(TN, "cam-repeat",
                RowFilters.PassAllFilter(),
                trueMutations: new[]
                {
                    Mutations.SetCell(CF, "counter", i.ToString(), new BigtableVersion(1000 + i * 1000))
                });
        }

        var row = await Client.ReadRowAsync(TN, "cam-repeat");
        var cells = row!.Families.First(f => f.Name == CF).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "counter").Cells;
        cells.OrderByDescending(c => c.TimestampMicros).First().Value.ToStringUtf8().Should().Be("5");
    }

    [Fact]
    public async Task Predicate_block_all_always_false()
    {
        await Client.MutateRowAsync(TN, "cam-block",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-block",
            RowFilters.BlockAllFilter(),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "branch", "false", new BigtableVersion(2000)) });

        result.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, "cam-block");
        GetLatestValue(row!, CF, "branch").Should().Be("false");
    }

    [Fact]
    public async Task Predicate_pass_all_matches_existing_row()
    {
        await Client.MutateRowAsync(TN, "cam-pass",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-pass",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "branch", "true", new BigtableVersion(2000)) });

        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task True_mutation_to_different_family()
    {
        await Client.MutateRowAsync(TN, "cam-crossfam",
            Mutations.SetCell(CF, "trigger", "go", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-crossfam",
            RowFilters.Chain(RowFilters.ColumnQualifierExact("trigger"), RowFilters.ValueExact("go")),
            trueMutations: new[] { Mutations.SetCell("cf2", "result", "done", new BigtableVersion(2000)) });

        result.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, "cam-crossfam");
        row!.Families.Select(f => f.Name).Should().Contain("cf2");
    }

    [Fact]
    public async Task Condition_with_interleave_filter()
    {
        await Client.MutateRowAsync(TN, "cam-interleave",
            Mutations.SetCell(CF, "a", "x", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "y", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-interleave",
            RowFilters.Interleave(
                RowFilters.Chain(RowFilters.ColumnQualifierExact("a"), RowFilters.ValueExact("x")),
                RowFilters.Chain(RowFilters.ColumnQualifierExact("b"), RowFilters.ValueExact("y"))),
            trueMutations: new[] { Mutations.SetCell(CF, "found", "both", new BigtableVersion(2000)) });

        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_row_in_true_branch()
    {
        await Client.MutateRowAsync(TN, "cam-delrow",
            Mutations.SetCell(CF, "status", "expired", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-delrow",
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueExact("expired")),
            trueMutations: new[] { Mutations.DeleteFromRow() });

        result.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, "cam-delrow");
        row.Should().BeNull();
    }

    private static string GetLatestValue(Row row, string family, string col) =>
        row.Families
            .First(f => f.Name == family).Columns
            .First(c => c.Qualifier.ToStringUtf8() == col)
            .Cells.OrderByDescending(c => c.TimestampMicros).First()
            .Value.ToStringUtf8();
}

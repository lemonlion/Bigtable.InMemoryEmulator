using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CheckAndMutateBranchingTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "cam-branch";
    private const string CF = "cf";

    public CheckAndMutateBranchingTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task True_branch_when_predicate_matches()
    {
        var rk = "camb-true";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "exists"));
        var result = await Client.CheckAndMutateRowAsync(TN, rk,
            predicateFilter: RowFilters.ValueExact("exists"),
            trueMutations: new[] { Mutations.SetCell(CF, "flag", "true-hit") },
            falseMutations: null);
        result.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.SelectMany(f => f.Columns).First(c => c.Qualifier.ToStringUtf8() == "flag")
            .Cells[0].Value.ToStringUtf8().Should().Be("true-hit");
    }

    [Fact]
    public async Task False_branch_when_predicate_fails()
    {
        var rk = "camb-false";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "other"));
        var result = await Client.CheckAndMutateRowAsync(TN, rk,
            predicateFilter: RowFilters.ValueExact("not-here"),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "flag", "false-hit") });
        result.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task Nonexistent_row_takes_false_branch()
    {
        var rk = "camb-noexist";
        var result = await Client.CheckAndMutateRowAsync(TN, rk,
            predicateFilter: RowFilters.PassAllFilter(),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "created", "yes") });
        result.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_cells_on_true()
    {
        var rk = "camb-del";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "keep", "v"), Mutations.SetCell(CF, "remove", "v"));
        await Client.CheckAndMutateRowAsync(TN, rk,
            predicateFilter: RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.DeleteFromColumn(CF, "remove") },
            falseMutations: null);
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8())
            .Should().ContainSingle().Which.Should().Be("keep");
    }

    [Fact]
    public async Task Multiple_true_mutations()
    {
        var rk = "camb-multi";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "x"));
        await Client.CheckAndMutateRowAsync(TN, rk,
            predicateFilter: RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "a", "1"), Mutations.SetCell(CF, "b", "2"), Mutations.SetCell(CF, "c", "3") },
            falseMutations: null);
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(4);
    }

    [Fact]
    public async Task Column_qualifier_predicate()
    {
        var rk = "camb-cqf";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "target", "val"));
        var result = await Client.CheckAndMutateRowAsync(TN, rk,
            predicateFilter: RowFilters.ColumnQualifierExact("target"),
            trueMutations: new[] { Mutations.SetCell(CF, "found", "yes") },
            falseMutations: null);
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Value_regex_predicate()
    {
        var rk = "camb-vreg";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "hello-world"));
        var result = await Client.CheckAndMutateRowAsync(TN, rk,
            predicateFilter: RowFilters.ValueRegex("hello.*"),
            trueMutations: new[] { Mutations.SetCell(CF, "matched", "yes") },
            falseMutations: null);
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_from_family_on_true()
    {
        var rk = "camb-delfam";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a", "1"), Mutations.SetCell(CF, "b", "2"));
        await Client.CheckAndMutateRowAsync(TN, rk,
            predicateFilter: RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.DeleteFromFamily(CF) },
            falseMutations: null);
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().BeNull();
    }

    [Fact]
    public async Task Delete_from_row_on_true()
    {
        var rk = "camb-delrow";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a", "1"));
        await Client.CheckAndMutateRowAsync(TN, rk,
            predicateFilter: RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.DeleteFromRow() },
            falseMutations: null);
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().BeNull();
    }

    [Fact]
    public async Task Chain_predicate()
    {
        var rk = "camb-chain";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "target", "match-me"));
        var result = await Client.CheckAndMutateRowAsync(TN, rk,
            predicateFilter: RowFilters.Chain(RowFilters.ColumnQualifierExact("target"), RowFilters.ValueRegex("match.*")),
            trueMutations: new[] { Mutations.SetCell(CF, "result", "chained") },
            falseMutations: null);
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task False_branch_creates_row()
    {
        var rk = "camb-create";
        var result = await Client.CheckAndMutateRowAsync(TN, rk,
            predicateFilter: RowFilters.PassAllFilter(),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "init", "created") });
        result.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task With_timestamp_version()
    {
        var rk = "camb-ts";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "v"));
        await Client.CheckAndMutateRowAsync(TN, rk,
            predicateFilter: RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "versioned", "val", new BigtableVersion(5000)) },
            falseMutations: null);
        var row = await Client.ReadRowAsync(TN, rk);
        var cell = row!.Families.SelectMany(f => f.Columns).First(c => c.Qualifier.ToStringUtf8() == "versioned").Cells[0];
        cell.TimestampMicros.Should().Be(5_000_000);
    }

    [Fact]
    public async Task Successive_operations_toggle_branch()
    {
        var rk = "camb-succ";
        await Client.CheckAndMutateRowAsync(TN, rk,
            predicateFilter: RowFilters.PassAllFilter(),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "counter", "0") });
        var result = await Client.CheckAndMutateRowAsync(TN, rk,
            predicateFilter: RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "counter", "1") },
            falseMutations: null);
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task BlockAll_predicate_always_false()
    {
        var rk = "camb-block";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "val"));
        var result = await Client.CheckAndMutateRowAsync(TN, rk,
            predicateFilter: RowFilters.BlockAllFilter(),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "blocked", "yes") });
        result.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task Cells_per_row_limit_predicate()
    {
        var rk = "camb-limit";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a", "1"), Mutations.SetCell(CF, "b", "2"), Mutations.SetCell(CF, "c", "3"));
        var result = await Client.CheckAndMutateRowAsync(TN, rk,
            predicateFilter: RowFilters.Chain(RowFilters.CellsPerRowLimit(1), RowFilters.ColumnQualifierExact("a")),
            trueMutations: new[] { Mutations.SetCell(CF, "pass", "yes") },
            falseMutations: null);
        result.PredicateMatched.Should().BeTrue();
    }
}

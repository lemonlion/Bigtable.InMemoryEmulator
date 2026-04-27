using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Advanced CheckAndMutate tests focusing on state machine patterns and intricate predicate logic.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#checkandmutaterowrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CheckAndMutateComplexTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public CheckAndMutateComplexTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync("cam-complex", new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("cam-complex");

    [Fact]
    public async Task CaM_true_branch_sets_cell()
    {
        await Client.MutateRowAsync(TN, "cam-true",
            Mutations.SetCell(CF, "status", "active", new BigtableVersion(1000)));

        var response = await Client.CheckAndMutateRowAsync(TN, "cam-true",
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueRegex("active")),
            trueMutations: new[] { Mutations.SetCell(CF, "status", "deactivated", new BigtableVersion(2000)) },
            falseMutations: null);

        response.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, "cam-true");
        row!.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "status")
            .Cells[0].Value.ToStringUtf8().Should().Be("deactivated");
    }

    [Fact]
    public async Task CaM_false_branch_increments()
    {
        await Client.MutateRowAsync(TN, "cam-false",
            Mutations.SetCell(CF, "status", "inactive", new BigtableVersion(1000)));

        var response = await Client.CheckAndMutateRowAsync(TN, "cam-false",
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueRegex("active")),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "status", "retry-needed", new BigtableVersion(2000)) });

        response.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, "cam-false");
        row!.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "status")
            .Cells[0].Value.ToStringUtf8().Should().Be("retry-needed");
    }

    [Fact]
    public async Task CaM_multi_column_predicate()
    {
        // Predicate checks if CF has "special" value in ANY cell
        await Client.MutateRowAsync(TN, "cam-multi-col",
            Mutations.SetCell(CF, "a", "normal", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "special", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "normal", new BigtableVersion(1000)));

        var response = await Client.CheckAndMutateRowAsync(TN, "cam-multi-col",
            RowFilters.Chain(
                RowFilters.FamilyNameExact(CF),
                RowFilters.ValueRegex("special")),
            trueMutations: new[] { Mutations.SetCell(CF, "found", "yes", new BigtableVersion(2000)) },
            falseMutations: null);

        response.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, "cam-multi-col");
        row!.Families[0].Columns.Any(c => c.Qualifier.ToStringUtf8() == "found").Should().BeTrue();
    }

    [Fact]
    public async Task CaM_predicate_checks_specific_family()
    {
        await Client.MutateRowAsync(TN, "cam-fam",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "target", new BigtableVersion(1000)));

        // Predicate on cf2 only
        var response = await Client.CheckAndMutateRowAsync(TN, "cam-fam",
            RowFilters.Chain(
                RowFilters.FamilyNameExact("cf2"),
                RowFilters.ValueRegex("target")),
            trueMutations: new[] { Mutations.SetCell(CF, "result", "matched-cf2", new BigtableVersion(2000)) },
            falseMutations: null);

        response.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task CaM_predicate_with_interleave()
    {
        await Client.MutateRowAsync(TN, "cam-interleave",
            Mutations.SetCell(CF, "a", "x", new BigtableVersion(1000)));

        // Interleave in predicate
        var response = await Client.CheckAndMutateRowAsync(TN, "cam-interleave",
            RowFilters.Interleave(
                RowFilters.Chain(RowFilters.ColumnQualifierExact("a"), RowFilters.ValueRegex("x")),
                RowFilters.Chain(RowFilters.ColumnQualifierExact("b"), RowFilters.ValueRegex("y"))),
            trueMutations: new[] { Mutations.SetCell(CF, "result", "found", new BigtableVersion(2000)) },
            falseMutations: null);

        // "a"="x" matches, so interleave produces cells → true
        response.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task CaM_multiple_true_mutations()
    {
        await Client.MutateRowAsync(TN, "cam-multi-mut",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, "cam-multi-mut",
            RowFilters.PassAllFilter(),
            trueMutations: new[]
            {
                Mutations.SetCell(CF, "c", "updated", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "extra1", "e1", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "extra2", "e2", new BigtableVersion(2000))
            },
            falseMutations: null);

        var row = await Client.ReadRowAsync(TN, "cam-multi-mut");
        var cols = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("extra1");
        cols.Should().Contain("extra2");
    }

    [Fact]
    public async Task CaM_delete_in_true_mutations()
    {
        await Client.MutateRowAsync(TN, "cam-del",
            Mutations.SetCell(CF, "keep", "yes", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "remove", "no", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, "cam-del",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.DeleteFromColumn(CF, "remove") },
            falseMutations: null);

        var row = await Client.ReadRowAsync(TN, "cam-del");
        row.Should().NotBeNull();
        row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8())
            .Should().NotContain("remove");
    }

    [Fact]
    public async Task CaM_repeated_calls_state_machine()
    {
        // Simulate: pending → processing → complete
        await Client.MutateRowAsync(TN, "cam-sm",
            Mutations.SetCell(CF, "state", "pending", new BigtableVersion(1000)));

        // pending → processing
        var r1 = await Client.CheckAndMutateRowAsync(TN, "cam-sm",
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("state"),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueRegex("pending")),
            trueMutations: new[] { Mutations.SetCell(CF, "state", "processing", new BigtableVersion(2000)) },
            falseMutations: null);
        r1.PredicateMatched.Should().BeTrue();

        // processing → complete
        var r2 = await Client.CheckAndMutateRowAsync(TN, "cam-sm",
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("state"),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueRegex("processing")),
            trueMutations: new[] { Mutations.SetCell(CF, "state", "complete", new BigtableVersion(3000)) },
            falseMutations: null);
        r2.PredicateMatched.Should().BeTrue();

        // Try to go from "pending" again → should fail
        var r3 = await Client.CheckAndMutateRowAsync(TN, "cam-sm",
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("state"),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueRegex("pending")),
            trueMutations: new[] { Mutations.SetCell(CF, "state", "processing", new BigtableVersion(4000)) },
            falseMutations: null);
        r3.PredicateMatched.Should().BeFalse();

        // Final state should be "complete"
        var row = await Client.ReadRowAsync(TN, "cam-sm");
        row!.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "state")
            .Cells[0].Value.ToStringUtf8().Should().Be("complete");
    }

    [Fact]
    public async Task CaM_on_nonexistent_row_false_mutations_create_row()
    {
        var response = await Client.CheckAndMutateRowAsync(TN, "cam-create",
            RowFilters.PassAllFilter(),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "init", "created", new BigtableVersion(1000)) });

        response.PredicateMatched.Should().BeFalse(); // row doesn't exist
        var row = await Client.ReadRowAsync(TN, "cam-create");
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("created");
    }

    [Fact]
    public async Task CaM_concurrent_on_same_row()
    {
        await Client.MutateRowAsync(TN, "cam-conc",
            Mutations.SetCell(CF, "c", "initial", new BigtableVersion(1000)));

        var tasks = Enumerable.Range(0, 10)
            .Select(i => Client.CheckAndMutateRowAsync(TN, "cam-conc",
                RowFilters.Chain(
                    RowFilters.ColumnQualifierExact("c"),
                    RowFilters.CellsPerColumnLimit(1),
                    RowFilters.ValueRegex("initial")),
                trueMutations: new[]
                {
                    Mutations.SetCell(CF, "c", $"updated-{i}", new BigtableVersion((i + 2) * 1000))
                },
                falseMutations: null))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Exactly one should match (first to execute)
        results.Count(r => r.PredicateMatched).Should().Be(1);
    }
}

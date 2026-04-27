using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CheckAndMutateEdgeCaseTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "cam-edge";
    private const string CF = "cf";

    public CheckAndMutateEdgeCaseTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task True_branch_on_existing_row()
    {
        await Client.MutateRowAsync(TN, "r1", Mutations.SetCell(CF, "c", "v"));
        var result = await Client.CheckAndMutateRowAsync(TN, "r1",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "flag", "true") },
            falseMutations: null);
        result.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, "r1");
        row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8())
            .Should().Contain("flag");
    }

    [Fact]
    public async Task False_branch_on_missing_row()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "r2",
            RowFilters.PassAllFilter(),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "created", "yes") });
        result.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, "r2");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Value_predicate_true()
    {
        await Client.MutateRowAsync(TN, "r3", Mutations.SetCell(CF, "status", "active"));
        var result = await Client.CheckAndMutateRowAsync(TN, "r3",
            RowFilters.ValueExact("active"),
            trueMutations: new[] { Mutations.SetCell(CF, "status", "inactive") },
            falseMutations: null);
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Value_predicate_false()
    {
        await Client.MutateRowAsync(TN, "r4", Mutations.SetCell(CF, "status", "active"));
        var result = await Client.CheckAndMutateRowAsync(TN, "r4",
            RowFilters.ValueExact("inactive"),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "flag", "f") });
        result.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_mutation_in_true_branch()
    {
        await Client.MutateRowAsync(TN, "r5",
            Mutations.SetCell(CF, "keep", "yes"),
            Mutations.SetCell(CF, "remove", "yes"));
        await Client.CheckAndMutateRowAsync(TN, "r5",
            RowFilters.ColumnQualifierExact("remove"),
            trueMutations: new[] { Mutations.DeleteFromColumn(CF, "remove") },
            falseMutations: null);
        var row = await Client.ReadRowAsync(TN, "r5");
        row!.Families.SelectMany(f => f.Columns).Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("keep");
    }

    [Fact]
    public async Task Multiple_mutations_in_true_branch()
    {
        await Client.MutateRowAsync(TN, "r6", Mutations.SetCell(CF, "c", "v"));
        await Client.CheckAndMutateRowAsync(TN, "r6",
            RowFilters.PassAllFilter(),
            trueMutations: new[]
            {
                Mutations.SetCell(CF, "a", "1"),
                Mutations.SetCell(CF, "b", "2"),
                Mutations.SetCell(CF, "c2", "3"),
            },
            falseMutations: null);
        var row = await Client.ReadRowAsync(TN, "r6");
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(4);
    }

    [Fact]
    public async Task Block_all_predicate_always_false()
    {
        await Client.MutateRowAsync(TN, "r7", Mutations.SetCell(CF, "c", "v"));
        var result = await Client.CheckAndMutateRowAsync(TN, "r7",
            RowFilters.BlockAllFilter(),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "landed", "false-branch") });
        result.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, "r7");
        row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8())
            .Should().Contain("landed");
    }

    [Fact]
    public async Task Column_qualifier_predicate()
    {
        await Client.MutateRowAsync(TN, "r8",
            Mutations.SetCell(CF, "name", "Alice"),
            Mutations.SetCell(CF, "age", "30"));
        var result = await Client.CheckAndMutateRowAsync(TN, "r8",
            RowFilters.ColumnQualifierRegex("name"),
            trueMutations: new[] { Mutations.SetCell(CF, "has-name", "true") },
            falseMutations: null);
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Repeated_check_and_mutate()
    {
        await Client.MutateRowAsync(TN, "r9", Mutations.SetCell(CF, "counter", "0", new BigtableVersion(1000)));
        for (int i = 0; i < 5; i++)
        {
            var result = await Client.CheckAndMutateRowAsync(TN, "r9",
                RowFilters.PassAllFilter(),
                trueMutations: new[] { Mutations.SetCell(CF, "counter", $"{i + 1}", new BigtableVersion((i + 2) * 1000)) },
                falseMutations: null);
            result.PredicateMatched.Should().BeTrue();
        }
        var row = await Client.ReadRowAsync(TN, "r9", RowFilters.CellsPerColumnLimit(1));
        row!.Families.SelectMany(f => f.Columns)
            .First(c => c.Qualifier.ToStringUtf8() == "counter")
            .Cells.First().Value.ToStringUtf8().Should().Be("5");
    }

    [Fact]
    public async Task Delete_from_row_in_true_branch()
    {
        await Client.MutateRowAsync(TN, "r10", Mutations.SetCell(CF, "c", "v"));
        await Client.CheckAndMutateRowAsync(TN, "r10",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.DeleteFromRow() },
            falseMutations: null);
        var row = await Client.ReadRowAsync(TN, "r10");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Family_name_predicate()
    {
        await Client.MutateRowAsync(TN, "r11", Mutations.SetCell(CF, "c", "v"));
        var result = await Client.CheckAndMutateRowAsync(TN, "r11",
            RowFilters.FamilyNameExact(CF),
            trueMutations: new[] { Mutations.SetCell(CF, "found", "yes") },
            falseMutations: null);
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Wrong_family_predicate_is_false()
    {
        await Client.MutateRowAsync(TN, "r12", Mutations.SetCell(CF, "c", "v"));
        var result = await Client.CheckAndMutateRowAsync(TN, "r12",
            RowFilters.FamilyNameExact("nonexistent"),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "nf", "1") });
        result.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task Regex_value_predicate()
    {
        await Client.MutateRowAsync(TN, "r13", Mutations.SetCell(CF, "val", "hello-world"));
        var result = await Client.CheckAndMutateRowAsync(TN, "r13",
            RowFilters.ValueRegex("hello-.*"),
            trueMutations: new[] { Mutations.SetCell(CF, "matched", "yes") },
            falseMutations: null);
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Chain_predicate()
    {
        await Client.MutateRowAsync(TN, "r14",
            Mutations.SetCell(CF, "name", "Alice"),
            Mutations.SetCell(CF, "age", "30"));
        var predicate = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.ValueExact("Alice"));
        var result = await Client.CheckAndMutateRowAsync(TN, "r14",
            predicate,
            trueMutations: new[] { Mutations.SetCell(CF, "verified", "yes") },
            falseMutations: null);
        result.PredicateMatched.Should().BeTrue();
    }
}

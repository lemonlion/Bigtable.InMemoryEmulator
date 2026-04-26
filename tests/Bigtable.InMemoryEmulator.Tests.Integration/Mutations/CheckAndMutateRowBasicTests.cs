using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CheckAndMutateRowBasicTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "cam-basic";
    private const string CF = "cf";

    public CheckAndMutateRowBasicTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task True_branch_when_row_exists()
    {
        await Client.MutateRowAsync(TN, "r1", Mutations.SetCell(CF, "c", "val"));
        var resp = await Client.CheckAndMutateRowAsync(TN, "r1",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "status", "found") },
            falseMutations: null);
        resp.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, "r1");
        row!.Families[0].Columns.Any(c => c.Qualifier.ToStringUtf8() == "status").Should().BeTrue();
    }

    [Fact]
    public async Task False_branch_when_row_missing()
    {
        var resp = await Client.CheckAndMutateRowAsync(TN, "r2",
            RowFilters.PassAllFilter(),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "c", "created") });
        resp.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, "r2");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task True_branch_with_value_filter()
    {
        await Client.MutateRowAsync(TN, "r3", Mutations.SetCell(CF, "c", "yes"));
        var resp = await Client.CheckAndMutateRowAsync(TN, "r3",
            RowFilters.ValueExact("yes"),
            trueMutations: new[] { Mutations.SetCell(CF, "matched", "true") },
            falseMutations: null);
        resp.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task False_branch_with_value_mismatch()
    {
        await Client.MutateRowAsync(TN, "r4", Mutations.SetCell(CF, "c", "no"));
        var resp = await Client.CheckAndMutateRowAsync(TN, "r4",
            RowFilters.ValueExact("yes"),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "matched", "false") });
        resp.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_on_true_branch()
    {
        await Client.MutateRowAsync(TN, "r5",
            Mutations.SetCell(CF, "c", "val"),
            Mutations.SetCell(CF, "temp", "remove-me"));
        var resp = await Client.CheckAndMutateRowAsync(TN, "r5",
            RowFilters.ColumnQualifierExact("temp"),
            trueMutations: new[] { Mutations.DeleteFromColumn(CF, "temp") },
            falseMutations: null);
        resp.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, "r5");
        row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8())
            .Should().NotContain("temp");
    }

    [Fact]
    public async Task Both_branches_with_column_filter()
    {
        await Client.MutateRowAsync(TN, "r6", Mutations.SetCell(CF, "a", "v1"));
        // Check for column "b" (doesn't exist) — false branch
        var resp = await Client.CheckAndMutateRowAsync(TN, "r6",
            RowFilters.ColumnQualifierExact("b"),
            trueMutations: new[] { Mutations.SetCell(CF, "result", "had-b") },
            falseMutations: new[] { Mutations.SetCell(CF, "result", "no-b") });
        resp.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, "r6");
        var resultCol = row!.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "result");
        resultCol.Cells[0].Value.ToStringUtf8().Should().Be("no-b");
    }

    [Fact]
    public async Task True_match_returns_predicate_matched()
    {
        await Client.MutateRowAsync(TN, "r7", Mutations.SetCell(CF, "c", "val"));
        // SDK requires at least one mutation in true or false branch
        var resp = await Client.CheckAndMutateRowAsync(TN, "r7",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "flag", "t") },
            falseMutations: null);
        resp.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Multiple_true_mutations()
    {
        await Client.MutateRowAsync(TN, "r8", Mutations.SetCell(CF, "c", "val"));
        await Client.CheckAndMutateRowAsync(TN, "r8",
            RowFilters.PassAllFilter(),
            trueMutations: new[]
            {
                Mutations.SetCell(CF, "x", "1"),
                Mutations.SetCell(CF, "y", "2"),
                Mutations.SetCell(CF, "z", "3")
            },
            falseMutations: null);
        var row = await Client.ReadRowAsync(TN, "r8");
        row!.Families.SelectMany(f => f.Columns).Should().HaveCountGreaterThanOrEqualTo(4);
    }

    [Fact]
    public async Task Family_filter_predicate()
    {
        await Client.MutateRowAsync(TN, "r9", Mutations.SetCell(CF, "c", "val"));
        var resp = await Client.CheckAndMutateRowAsync(TN, "r9",
            RowFilters.FamilyNameExact(CF),
            trueMutations: new[] { Mutations.SetCell(CF, "found", "yes") },
            falseMutations: null);
        resp.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Family_filter_no_match()
    {
        await Client.MutateRowAsync(TN, "r10", Mutations.SetCell(CF, "c", "val"));
        var resp = await Client.CheckAndMutateRowAsync(TN, "r10",
            RowFilters.FamilyNameExact("nonexistent"),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "result", "no") });
        resp.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task Idempotent_check()
    {
        await Client.MutateRowAsync(TN, "r11", Mutations.SetCell(CF, "c", "val", new BigtableVersion(1000)));
        // Run same check twice — should be idempotent
        for (int i = 0; i < 3; i++)
        {
            var resp = await Client.CheckAndMutateRowAsync(TN, "r11",
                RowFilters.PassAllFilter(),
                trueMutations: new[] { Mutations.SetCell(CF, "c", "val", new BigtableVersion(1000)) },
                falseMutations: null);
            resp.PredicateMatched.Should().BeTrue();
        }
        var row = await Client.ReadRowAsync(TN, "r11");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle(); // same version overwrites
    }
}

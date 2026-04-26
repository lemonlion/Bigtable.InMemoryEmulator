using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CheckAndMutatePredicateFilterTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "cam-pf";
    private const string CF = "cf";

    public CheckAndMutatePredicateFilterTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        await Client.MutateRowAsync(TN, "r1",
            Mutations.SetCell(CF, "status", "active"),
            Mutations.SetCell(CF, "count", "5"),
            Mutations.SetCell(CF, "name", "Alice"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task ValueExact_predicate_true()
    {
        var r = await Client.CheckAndMutateRowAsync(TN, "r1",
            RowFilters.ValueExact("active"),
            trueMutations: new[] { Mutations.SetCell(CF, "checked", "yes") },
            falseMutations: null);
        r.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task ValueExact_predicate_false()
    {
        var r = await Client.CheckAndMutateRowAsync(TN, "r1",
            RowFilters.ValueExact("inactive"),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "checked", "no") });
        r.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task ValueRegex_predicate()
    {
        var r = await Client.CheckAndMutateRowAsync(TN, "r1",
            RowFilters.ValueRegex("act.*"),
            trueMutations: new[] { Mutations.SetCell(CF, "matched", "regex") },
            falseMutations: null);
        r.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task ColumnQualifier_predicate()
    {
        var r = await Client.CheckAndMutateRowAsync(TN, "r1",
            RowFilters.ColumnQualifierExact("name"),
            trueMutations: new[] { Mutations.SetCell(CF, "has-name", "true") },
            falseMutations: null);
        r.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task ColumnQualifier_missing()
    {
        var r = await Client.CheckAndMutateRowAsync(TN, "r1",
            RowFilters.ColumnQualifierExact("email"),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "no-email", "true") });
        r.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task Chain_predicate()
    {
        var predicate = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("status"),
            RowFilters.ValueExact("active"));
        var r = await Client.CheckAndMutateRowAsync(TN, "r1",
            predicate,
            trueMutations: new[] { Mutations.SetCell(CF, "verified", "yes") },
            falseMutations: null);
        r.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Chain_predicate_false()
    {
        var predicate = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("status"),
            RowFilters.ValueExact("deleted"));
        var r = await Client.CheckAndMutateRowAsync(TN, "r1",
            predicate,
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "not-deleted", "true") });
        r.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task FamilyName_predicate()
    {
        var r = await Client.CheckAndMutateRowAsync(TN, "r1",
            RowFilters.FamilyNameExact(CF),
            trueMutations: new[] { Mutations.SetCell(CF, "has-cf", "yes") },
            falseMutations: null);
        r.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task BlockAll_predicate_always_false()
    {
        var r = await Client.CheckAndMutateRowAsync(TN, "r1",
            RowFilters.BlockAllFilter(),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "blocked", "yes") });
        r.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task PassAll_predicate_always_true()
    {
        var r = await Client.CheckAndMutateRowAsync(TN, "r1",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "passed", "yes") },
            falseMutations: null);
        r.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Interleave_predicate()
    {
        var predicate = RowFilters.Interleave(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.ColumnQualifierExact("status"));
        var r = await Client.CheckAndMutateRowAsync(TN, "r1",
            predicate,
            trueMutations: new[] { Mutations.SetCell(CF, "multi", "yes") },
            falseMutations: null);
        r.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Empty_row_false()
    {
        var r = await Client.CheckAndMutateRowAsync(TN, "empty-row",
            RowFilters.PassAllFilter(),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "created", "yes") });
        r.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task ColumnQualifierRegex_predicate()
    {
        var r = await Client.CheckAndMutateRowAsync(TN, "r1",
            RowFilters.ColumnQualifierRegex("na.*"),
            trueMutations: new[] { Mutations.SetCell(CF, "regex-col", "yes") },
            falseMutations: null);
        r.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task CellsPerRowLimit_predicate()
    {
        var r = await Client.CheckAndMutateRowAsync(TN, "r1",
            RowFilters.CellsPerRowLimit(1),
            trueMutations: new[] { Mutations.SetCell(CF, "has-cells", "yes") },
            falseMutations: null);
        r.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task StripValue_predicate()
    {
        // StripValue still returns cells (just empty values), so predicate matches
        var r = await Client.CheckAndMutateRowAsync(TN, "r1",
            RowFilters.StripValueTransformer(),
            trueMutations: new[] { Mutations.SetCell(CF, "stripped", "yes") },
            falseMutations: null);
        r.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_in_true_branch()
    {
        await Client.MutateRowAsync(TN, "r-del", Mutations.SetCell(CF, "temp", "data"));
        var r = await Client.CheckAndMutateRowAsync(TN, "r-del",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.DeleteFromRow() },
            falseMutations: null);
        r.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, "r-del");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Multiple_mutations_in_false_branch()
    {
        var r = await Client.CheckAndMutateRowAsync(TN, "r-new",
            RowFilters.PassAllFilter(),
            trueMutations: null,
            falseMutations: new[]
            {
                Mutations.SetCell(CF, "a", "1"),
                Mutations.SetCell(CF, "b", "2"),
                Mutations.SetCell(CF, "c", "3")
            });
        r.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, "r-new");
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(3);
    }
}

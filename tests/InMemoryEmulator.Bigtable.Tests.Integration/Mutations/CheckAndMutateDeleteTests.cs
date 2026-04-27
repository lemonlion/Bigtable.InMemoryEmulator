using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CheckAndMutateDeleteTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "cam-del";
    private const string CF = "cf";

    public CheckAndMutateDeleteTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Delete_row_on_true()
    {
        await Client.MutateRowAsync(TN, "r1", Mutations.SetCell(CF, "c", "val"));
        var resp = await Client.CheckAndMutateRowAsync(TN, "r1",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.DeleteFromRow() },
            falseMutations: null);
        resp.PredicateMatched.Should().BeTrue();
        (await Client.ReadRowAsync(TN, "r1")).Should().BeNull();
    }

    [Fact]
    public async Task Delete_column_on_true()
    {
        await Client.MutateRowAsync(TN, "r2",
            Mutations.SetCell(CF, "keep", "v1"),
            Mutations.SetCell(CF, "remove", "v2"));
        await Client.CheckAndMutateRowAsync(TN, "r2",
            RowFilters.ColumnQualifierExact("remove"),
            trueMutations: new[] { Mutations.DeleteFromColumn(CF, "remove") },
            falseMutations: null);
        var row = await Client.ReadRowAsync(TN, "r2");
        row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8())
            .Should().ContainSingle().Which.Should().Be("keep");
    }

    [Fact]
    public async Task Delete_family_on_true()
    {
        await Client.MutateRowAsync(TN, "r3", Mutations.SetCell(CF, "c", "val"));
        await Client.CheckAndMutateRowAsync(TN, "r3",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.DeleteFromFamily(CF) },
            falseMutations: null);
        (await Client.ReadRowAsync(TN, "r3")).Should().BeNull();
    }

    [Fact]
    public async Task Create_on_false_delete_on_true()
    {
        // Row doesn't exist → false branch creates it
        var resp1 = await Client.CheckAndMutateRowAsync(TN, "r4",
            RowFilters.PassAllFilter(),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "c", "created") });
        resp1.PredicateMatched.Should().BeFalse();
        // Row now exists → true branch deletes it
        var resp2 = await Client.CheckAndMutateRowAsync(TN, "r4",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.DeleteFromRow() },
            falseMutations: null);
        resp2.PredicateMatched.Should().BeTrue();
        (await Client.ReadRowAsync(TN, "r4")).Should().BeNull();
    }

    [Fact]
    public async Task Multiple_deletes_in_true_branch()
    {
        await Client.MutateRowAsync(TN, "r5",
            Mutations.SetCell(CF, "a", "v1"),
            Mutations.SetCell(CF, "b", "v2"),
            Mutations.SetCell(CF, "c", "v3"));
        await Client.CheckAndMutateRowAsync(TN, "r5",
            RowFilters.PassAllFilter(),
            trueMutations: new[]
            {
                Mutations.DeleteFromColumn(CF, "a"),
                Mutations.DeleteFromColumn(CF, "c")
            },
            falseMutations: null);
        var row = await Client.ReadRowAsync(TN, "r5");
        row!.Families.SelectMany(f => f.Columns).Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("b");
    }

    [Fact]
    public async Task Value_predicate_delete()
    {
        await Client.MutateRowAsync(TN, "r6", Mutations.SetCell(CF, "status", "expired"));
        await Client.CheckAndMutateRowAsync(TN, "r6",
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueExact("expired")),
            trueMutations: new[] { Mutations.DeleteFromRow() },
            falseMutations: null);
        (await Client.ReadRowAsync(TN, "r6")).Should().BeNull();
    }

    [Fact]
    public async Task Delete_preserves_other_rows()
    {
        await Client.MutateRowAsync(TN, "r7a", Mutations.SetCell(CF, "c", "v1"));
        await Client.MutateRowAsync(TN, "r7b", Mutations.SetCell(CF, "c", "v2"));
        await Client.CheckAndMutateRowAsync(TN, "r7a",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.DeleteFromRow() },
            falseMutations: null);
        (await Client.ReadRowAsync(TN, "r7b")).Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_and_set_in_same_mutation()
    {
        await Client.MutateRowAsync(TN, "r8",
            Mutations.SetCell(CF, "old", "v1"),
            Mutations.SetCell(CF, "keep", "v2"));
        await Client.CheckAndMutateRowAsync(TN, "r8",
            RowFilters.PassAllFilter(),
            trueMutations: new[]
            {
                Mutations.DeleteFromColumn(CF, "old"),
                Mutations.SetCell(CF, "new", "v3")
            },
            falseMutations: null);
        var row = await Client.ReadRowAsync(TN, "r8");
        var cols = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("keep").And.Contain("new").And.NotContain("old");
    }

    [Fact]
    public async Task Repeated_conditional_delete()
    {
        await Client.MutateRowAsync(TN, "r9", Mutations.SetCell(CF, "c", "val", new BigtableVersion(1000)));
        // First check — exists → delete
        await Client.CheckAndMutateRowAsync(TN, "r9",
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.DeleteFromRow() },
            falseMutations: null);
        // Second check — gone → false
        var resp = await Client.CheckAndMutateRowAsync(TN, "r9",
            RowFilters.PassAllFilter(),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "c", "reborn") });
        resp.PredicateMatched.Should().BeFalse();
        (await Client.ReadRowAsync(TN, "r9")).Should().NotBeNull();
    }
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Advanced CheckAndMutateRow integration tests — complex predicates, edge cases,
/// and interaction with other operations.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#checkandmutaterowrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CheckAndMutateAdvancedIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "cam-adv-tests";
    private const string CF = "cf";

    public CheckAndMutateAdvancedIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task CheckAndMutate_on_nonexistent_row_triggers_false_branch()
    {
        // Row doesn't exist → predicate filter produces no results → false branch
        var rk = new BigtableByteString("cam-norow");
        var response = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "a", "true", new BigtableVersion(1000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "a", "false", new BigtableVersion(1000)) });
        response.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("false");
    }

    [Fact]
    public async Task CheckAndMutate_with_complex_predicate()
    {
        // Use a chain filter as predicate
        var rk = new BigtableByteString("cam-cpred");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "status", "active", new BigtableVersion(1000)));
        var response = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.ValueExact("active")),
            trueMutations: new[] { Mutations.SetCell(CF, "result", "matched", new BigtableVersion(2000)) });
        response.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns.Should().Contain(c => c.Qualifier.ToStringUtf8() == "result");
    }

    [Fact]
    public async Task CheckAndMutate_with_value_regex_predicate()
    {
        var rk = new BigtableByteString("cam-vreg");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "data", "hello-world-123", new BigtableVersion(1000)));
        var response = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.ValueRegex("hello.*123"),
            trueMutations: new[] { Mutations.SetCell(CF, "found", "yes", new BigtableVersion(2000)) });
        response.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAndMutate_false_branch_only()
    {
        // Only false mutations provided — true is null
        var rk = new BigtableByteString("cam-fonly");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "x", "y", new BigtableVersion(1000)));
        var response = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.ValueRegex("NONEXISTENT"),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "fallback", "applied", new BigtableVersion(2000)) });
        response.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns.Should().Contain(c => c.Qualifier.ToStringUtf8() == "fallback");
    }

    [Fact]
    public async Task CheckAndMutate_true_branch_with_delete()
    {
        var rk = new BigtableByteString("cam-tdel");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "keep", "yes", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "remove", "no", new BigtableVersion(1000)));
        var response = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.DeleteFromColumn(CF, "remove") });
        response.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns.Should().ContainSingle();
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("keep");
    }

    [Fact]
    public async Task CheckAndMutate_multiple_true_mutations()
    {
        var rk = new BigtableByteString("cam-multi");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "trigger", "yes", new BigtableVersion(1000)));
        var response = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.PassAllFilter(),
            trueMutations: new[]
            {
                Mutations.SetCell(CF, "a", "1", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "b", "2", new BigtableVersion(2000)),
                Mutations.SetCell("cf2", "c", "3", new BigtableVersion(2000)),
            });
        response.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, rk);
        var allCols = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        allCols.Should().Contain("a");
        allCols.Should().Contain("b");
        allCols.Should().Contain("c");
    }

    [Fact]
    public async Task CheckAndMutate_with_block_all_filter_returns_false()
    {
        var rk = new BigtableByteString("cam-block");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "x", "y", new BigtableVersion(1000)));
        var response = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.BlockAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "never", "applied", new BigtableVersion(2000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "always", "applied", new BigtableVersion(2000)) });
        response.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns.Should().Contain(c => c.Qualifier.ToStringUtf8() == "always");
    }

    [Fact]
    public async Task CheckAndMutate_preserves_other_columns()
    {
        // Verify that the mutation doesn't affect unrelated data
        var rk = new BigtableByteString("cam-preserve");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "existing", "untouched", new BigtableVersion(1000)));
        await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "new", "added", new BigtableVersion(2000)) });
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns.Should().Contain(c => c.Qualifier.ToStringUtf8() == "existing");
        row.Families[0].Columns.Should().Contain(c => c.Qualifier.ToStringUtf8() == "new");
    }

    [Fact]
    public async Task CheckAndMutate_response_has_predicate_matched_field()
    {
        var rk = new BigtableByteString("cam-resp");
        var response = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.PassAllFilter(),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "x", "y", new BigtableVersion(1000)) });
        // Empty row → false branch
        response.Should().NotBeNull();
        response.PredicateMatched.Should().BeFalse();
    }
}

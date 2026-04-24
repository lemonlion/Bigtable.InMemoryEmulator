using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for CheckAndMutate state machine patterns and complex predicates.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#checkandmutaterowrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CheckAndMutateStateMachineTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "cam-sm";
    private const string CF = "cf";

    public CheckAndMutateStateMachineTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<string> ReadValue(string rowKey, string family, string col)
    {
        var list = new List<Row>();
        await foreach (var row in Client.ReadRows(TN,
            rows: RowSet.FromRowKeys(rowKey),
            filter: RowFilters.Chain(
                RowFilters.FamilyNameRegex(family),
                RowFilters.ColumnQualifierExact(col),
                RowFilters.CellsPerColumnLimit(1))))
            list.Add(row);
        if (list.Count == 0) return "";
        var fam = list[0].Families.FirstOrDefault(f => f.Name == family);
        if (fam == null) return "";
        var column = fam.Columns.FirstOrDefault(c => c.Qualifier.ToStringUtf8() == col);
        return column?.Cells[0].Value.ToStringUtf8() ?? "";
    }

    #region State transitions

    [Fact]
    public async Task State_transition_pending_to_active()
    {
        await Client.MutateRowAsync(TN, "cam-sm-01",
            Mutations.SetCell(CF, "state", "pending", new BigtableVersion(1000)));

        // Ref: CaM checks predicate, if true → apply true mutations
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-sm-01",
            predicateFilter: RowFilters.Chain(
                RowFilters.ColumnQualifierExact("state"),
                RowFilters.ValueExact("pending"),
                RowFilters.CellsPerColumnLimit(1)),
            trueMutations: new[] { Mutations.SetCell(CF, "state", "active", new BigtableVersion(2000)) },
            falseMutations: null);

        result.PredicateMatched.Should().BeTrue();
        (await ReadValue("cam-sm-01", CF, "state")).Should().Be("active");
    }

    [Fact]
    public async Task State_transition_active_to_completed()
    {
        await Client.MutateRowAsync(TN, "cam-sm-02",
            Mutations.SetCell(CF, "state", "active", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-sm-02",
            predicateFilter: RowFilters.Chain(
                RowFilters.ColumnQualifierExact("state"),
                RowFilters.ValueExact("active"),
                RowFilters.CellsPerColumnLimit(1)),
            trueMutations: new[] { Mutations.SetCell(CF, "state", "completed", new BigtableVersion(2000)) },
            falseMutations: null);

        result.PredicateMatched.Should().BeTrue();
        (await ReadValue("cam-sm-02", CF, "state")).Should().Be("completed");
    }

    [Fact]
    public async Task State_transition_fails_on_wrong_state()
    {
        await Client.MutateRowAsync(TN, "cam-sm-03",
            Mutations.SetCell(CF, "state", "completed", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-sm-03",
            predicateFilter: RowFilters.Chain(
                RowFilters.ColumnQualifierExact("state"),
                RowFilters.ValueExact("pending"),
                RowFilters.CellsPerColumnLimit(1)),
            trueMutations: new[] { Mutations.SetCell(CF, "state", "active", new BigtableVersion(2000)) },
            falseMutations: null);

        result.PredicateMatched.Should().BeFalse();
        (await ReadValue("cam-sm-03", CF, "state")).Should().Be("completed");
    }

    [Fact]
    public async Task Two_step_state_machine()
    {
        // pending -> active -> completed
        await Client.MutateRowAsync(TN, "cam-sm-04",
            Mutations.SetCell(CF, "state", "pending", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, "cam-sm-04",
            predicateFilter: RowFilters.Chain(
                RowFilters.ColumnQualifierExact("state"),
                RowFilters.ValueExact("pending"),
                RowFilters.CellsPerColumnLimit(1)),
            trueMutations: new[] { Mutations.SetCell(CF, "state", "active", new BigtableVersion(2000)) },
            falseMutations: null);

        await Client.CheckAndMutateRowAsync(TN, "cam-sm-04",
            predicateFilter: RowFilters.Chain(
                RowFilters.ColumnQualifierExact("state"),
                RowFilters.ValueExact("active"),
                RowFilters.CellsPerColumnLimit(1)),
            trueMutations: new[] { Mutations.SetCell(CF, "state", "completed", new BigtableVersion(3000)) },
            falseMutations: null);

        (await ReadValue("cam-sm-04", CF, "state")).Should().Be("completed");
    }

    #endregion

    #region False mutations

    [Fact]
    public async Task False_mutations_on_no_match()
    {
        await Client.MutateRowAsync(TN, "cam-sm-05",
            Mutations.SetCell(CF, "value", "old", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-sm-05",
            predicateFilter: RowFilters.Chain(
                RowFilters.ColumnQualifierExact("value"),
                RowFilters.ValueExact("expected"),
                RowFilters.CellsPerColumnLimit(1)),
            trueMutations: new[] { Mutations.SetCell(CF, "value", "updated", new BigtableVersion(2000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "error", "mismatch", new BigtableVersion(2000)) });

        result.PredicateMatched.Should().BeFalse();
        (await ReadValue("cam-sm-05", CF, "value")).Should().Be("old");
        (await ReadValue("cam-sm-05", CF, "error")).Should().Be("mismatch");
    }

    [Fact]
    public async Task True_mutations_preserve_false_column()
    {
        await Client.MutateRowAsync(TN, "cam-sm-06",
            Mutations.SetCell(CF, "flag", "yes", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "data", "original", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-sm-06",
            predicateFilter: RowFilters.Chain(
                RowFilters.ColumnQualifierExact("flag"),
                RowFilters.ValueExact("yes"),
                RowFilters.CellsPerColumnLimit(1)),
            trueMutations: new[] { Mutations.SetCell(CF, "data", "modified", new BigtableVersion(2000)) },
            falseMutations: null);

        result.PredicateMatched.Should().BeTrue();
        (await ReadValue("cam-sm-06", CF, "data")).Should().Be("modified");
        (await ReadValue("cam-sm-06", CF, "flag")).Should().Be("yes"); // Unchanged
    }

    #endregion

    #region Delete mutations

    [Fact]
    public async Task CaM_with_delete_mutation_on_true()
    {
        await Client.MutateRowAsync(TN, "cam-sm-07",
            Mutations.SetCell(CF, "status", "expired", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "data", "some-data", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, "cam-sm-07",
            predicateFilter: RowFilters.Chain(
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.ValueExact("expired"),
                RowFilters.CellsPerColumnLimit(1)),
            trueMutations: new[] { Mutations.DeleteFromRow() },
            falseMutations: null);

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("cam-sm-07")))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task CaM_delete_column_on_true()
    {
        await Client.MutateRowAsync(TN, "cam-sm-08",
            Mutations.SetCell(CF, "keep", "yes", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "remove", "data", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, "cam-sm-08",
            predicateFilter: RowFilters.Chain(
                RowFilters.ColumnQualifierExact("remove"),
                RowFilters.CellsPerColumnLimit(1)),
            trueMutations: new[] { Mutations.DeleteFromColumn(CF, "remove") },
            falseMutations: null);

        (await ReadValue("cam-sm-08", CF, "keep")).Should().Be("yes");
        (await ReadValue("cam-sm-08", CF, "remove")).Should().BeEmpty();
    }

    #endregion

    #region On nonexistent row

    [Fact]
    public async Task CaM_nonexistent_row_predicate_false()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-sm-09",
            predicateFilter: RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(CF, "created", "yes", new BigtableVersion(1000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "created", "no", new BigtableVersion(1000)) });

        // No row exists, so predicate has no cells => false
        result.PredicateMatched.Should().BeFalse();
        (await ReadValue("cam-sm-09", CF, "created")).Should().Be("no");
    }

    [Fact]
    public async Task CaM_creates_row_via_false_mutations()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-sm-10",
            predicateFilter: RowFilters.PassAllFilter(),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "init", "true", new BigtableVersion(1000)) });

        result.PredicateMatched.Should().BeFalse();
        (await ReadValue("cam-sm-10", CF, "init")).Should().Be("true");
    }

    #endregion

    #region Cross-family predicates

    [Fact]
    public async Task CaM_predicate_on_different_family_than_mutation()
    {
        await Client.MutateRowAsync(TN, "cam-sm-11",
            Mutations.SetCell(CF, "flag", "go", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-sm-11",
            predicateFilter: RowFilters.Chain(
                RowFilters.FamilyNameRegex(CF),
                RowFilters.ColumnQualifierExact("flag"),
                RowFilters.ValueExact("go"),
                RowFilters.CellsPerColumnLimit(1)),
            trueMutations: new[] { Mutations.SetCell("cf2", "result", "done", new BigtableVersion(2000)) },
            falseMutations: null);

        result.PredicateMatched.Should().BeTrue();
        (await ReadValue("cam-sm-11", "cf2", "result")).Should().Be("done");
    }

    #endregion

    #region Multiple mutations

    [Fact]
    public async Task CaM_multiple_true_mutations()
    {
        await Client.MutateRowAsync(TN, "cam-sm-12",
            Mutations.SetCell(CF, "trigger", "yes", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, "cam-sm-12",
            predicateFilter: RowFilters.Chain(
                RowFilters.ColumnQualifierExact("trigger"),
                RowFilters.ValueExact("yes"),
                RowFilters.CellsPerColumnLimit(1)),
            trueMutations: new[]
            {
                Mutations.SetCell(CF, "a", "1", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "b", "2", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "c", "3", new BigtableVersion(2000)),
            },
            falseMutations: null);

        (await ReadValue("cam-sm-12", CF, "a")).Should().Be("1");
        (await ReadValue("cam-sm-12", CF, "b")).Should().Be("2");
        (await ReadValue("cam-sm-12", CF, "c")).Should().Be("3");
    }

    [Fact]
    public async Task CaM_multiple_false_mutations()
    {
        await Client.MutateRowAsync(TN, "cam-sm-13",
            Mutations.SetCell(CF, "status", "wrong", new BigtableVersion(1000)));

        await Client.CheckAndMutateRowAsync(TN, "cam-sm-13",
            predicateFilter: RowFilters.Chain(
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.ValueExact("expected"),
                RowFilters.CellsPerColumnLimit(1)),
            trueMutations: null,
            falseMutations: new[]
            {
                Mutations.SetCell(CF, "error_code", "ERR001", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "error_msg", "Unexpected state", new BigtableVersion(2000)),
            });

        (await ReadValue("cam-sm-13", CF, "error_code")).Should().Be("ERR001");
        (await ReadValue("cam-sm-13", CF, "error_msg")).Should().Be("Unexpected state");
    }

    #endregion

    #region Regex predicate

    [Fact]
    public async Task CaM_value_regex_predicate()
    {
        await Client.MutateRowAsync(TN, "cam-sm-14",
            Mutations.SetCell(CF, "code", "ERR-404", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-sm-14",
            predicateFilter: RowFilters.Chain(
                RowFilters.ColumnQualifierExact("code"),
                RowFilters.ValueRegex("ERR-.*"),
                RowFilters.CellsPerColumnLimit(1)),
            trueMutations: new[] { Mutations.SetCell(CF, "needs_retry", "true", new BigtableVersion(2000)) },
            falseMutations: null);

        result.PredicateMatched.Should().BeTrue();
        (await ReadValue("cam-sm-14", CF, "needs_retry")).Should().Be("true");
    }

    [Fact]
    public async Task CaM_column_exists_predicate()
    {
        // Predicate: does column "flag" exist at all?
        await Client.MutateRowAsync(TN, "cam-sm-15",
            Mutations.SetCell(CF, "flag", "x", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-sm-15",
            predicateFilter: RowFilters.Chain(
                RowFilters.ColumnQualifierExact("flag"),
                RowFilters.CellsPerColumnLimit(1)),
            trueMutations: new[] { Mutations.SetCell(CF, "exists", "true", new BigtableVersion(2000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "exists", "false", new BigtableVersion(2000)) });

        result.PredicateMatched.Should().BeTrue();
        (await ReadValue("cam-sm-15", CF, "exists")).Should().Be("true");
    }

    [Fact]
    public async Task CaM_column_not_exists_predicate()
    {
        await Client.MutateRowAsync(TN, "cam-sm-16",
            Mutations.SetCell(CF, "other", "x", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "cam-sm-16",
            predicateFilter: RowFilters.Chain(
                RowFilters.ColumnQualifierExact("flag"),
                RowFilters.CellsPerColumnLimit(1)),
            trueMutations: new[] { Mutations.SetCell(CF, "exists", "true", new BigtableVersion(2000)) },
            falseMutations: new[] { Mutations.SetCell(CF, "exists", "false", new BigtableVersion(2000)) });

        result.PredicateMatched.Should().BeFalse();
        (await ReadValue("cam-sm-16", CF, "exists")).Should().Be("false");
    }

    #endregion
}

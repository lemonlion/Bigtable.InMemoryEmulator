using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for CheckAndMutate with complex predicate patterns and mutation combinations.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#checkandmutaterowrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CheckAndMutatePredicateTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "cam-pred";
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public CheckAndMutatePredicateTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null)
    {
        var list = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: rows, filter: filter))
            list.Add(row);
        return list;
    }

    #region Value-based predicates

    [Fact]
    public async Task Predicate_exact_value_match()
    {
        await Client.MutateRowAsync(TN, "cp-val-1",
            Mutations.SetCell(CF, "status", "active", new BigtableVersion(1000)));
        var result = await Client.CheckAndMutateRowAsync(TN, "cp-val-1",
            RowFilters.Chain(RowFilters.CellsPerColumnLimit(1), RowFilters.ValueExact("active")),
            Mutations.SetCell(CF, "status", "inactive", new BigtableVersion(2000)));
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Predicate_exact_value_no_match()
    {
        await Client.MutateRowAsync(TN, "cp-val-2",
            Mutations.SetCell(CF, "status", "active", new BigtableVersion(1000)));
        var result = await Client.CheckAndMutateRowAsync(TN, "cp-val-2",
            RowFilters.Chain(RowFilters.CellsPerColumnLimit(1), RowFilters.ValueExact("inactive")),
            Mutations.SetCell(CF, "status", "error", new BigtableVersion(2000)));
        result.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task Predicate_value_regex()
    {
        await Client.MutateRowAsync(TN, "cp-val-3",
            Mutations.SetCell(CF, "code", "ERR-404", new BigtableVersion(1000)));
        var result = await Client.CheckAndMutateRowAsync(TN, "cp-val-3",
            RowFilters.Chain(RowFilters.CellsPerColumnLimit(1), RowFilters.ValueRegex("ERR-.*")),
            Mutations.SetCell(CF, "handled", "true", new BigtableVersion(1000)));
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Predicate_value_range()
    {
        await Client.MutateRowAsync(TN, "cp-val-4",
            Mutations.SetCell(CF, "score", "75", new BigtableVersion(1000)));
        var result = await Client.CheckAndMutateRowAsync(TN, "cp-val-4",
            RowFilters.Chain(
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueRange(ValueRange.Closed("50", "99"))),
            Mutations.SetCell(CF, "grade", "pass", new BigtableVersion(1000)));
        result.PredicateMatched.Should().BeTrue();
    }

    #endregion

    #region Column-based predicates

    [Fact]
    public async Task Predicate_column_exists()
    {
        await Client.MutateRowAsync(TN, "cp-col-1",
            Mutations.SetCell(CF, "email", "test@test.com", new BigtableVersion(1000)));
        var result = await Client.CheckAndMutateRowAsync(TN, "cp-col-1",
            RowFilters.ColumnQualifierExact("email"),
            Mutations.SetCell(CF, "verified", "true", new BigtableVersion(1000)));
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Predicate_column_not_exists()
    {
        await Client.MutateRowAsync(TN, "cp-col-2",
            Mutations.SetCell(CF, "name", "Alice", new BigtableVersion(1000)));
        var result = await Client.CheckAndMutateRowAsync(TN, "cp-col-2",
            RowFilters.ColumnQualifierExact("email"),
            Mutations.SetCell(CF, "needs_email", "true", new BigtableVersion(1000)));
        result.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task Predicate_column_qualifier_regex()
    {
        await Client.MutateRowAsync(TN, "cp-col-3",
            Mutations.SetCell(CF, "address_line1", "123 St", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "address_city", "Portland", new BigtableVersion(1000)));
        var result = await Client.CheckAndMutateRowAsync(TN, "cp-col-3",
            RowFilters.ColumnQualifierRegex("address_.*"),
            Mutations.SetCell(CF, "has_address", "true", new BigtableVersion(1000)));
        result.PredicateMatched.Should().BeTrue();
    }

    #endregion

    #region Family-based predicates

    [Fact]
    public async Task Predicate_family_exists()
    {
        await Client.MutateRowAsync(TN, "cp-fam-1",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "v", new BigtableVersion(1000)));
        var result = await Client.CheckAndMutateRowAsync(TN, "cp-fam-1",
            RowFilters.FamilyNameExact(CF2),
            Mutations.SetCell(CF, "has_cf2", "true", new BigtableVersion(1000)));
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Predicate_family_not_exists()
    {
        await Client.MutateRowAsync(TN, "cp-fam-2",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var result = await Client.CheckAndMutateRowAsync(TN, "cp-fam-2",
            RowFilters.FamilyNameExact(CF2),
            Mutations.SetCell(CF, "missing_cf2", "true", new BigtableVersion(1000)));
        result.PredicateMatched.Should().BeFalse();
    }

    #endregion

    #region Complex chain predicates

    [Fact]
    public async Task Predicate_family_and_column_and_value()
    {
        await Client.MutateRowAsync(TN, "cp-chain-1",
            Mutations.SetCell(CF, "status", "active", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "role", "admin", new BigtableVersion(1000)));
        var result = await Client.CheckAndMutateRowAsync(TN, "cp-chain-1",
            RowFilters.Chain(
                RowFilters.FamilyNameExact(CF2),
                RowFilters.ColumnQualifierExact("role"),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueExact("admin")),
            Mutations.SetCell(CF, "is_admin", "true", new BigtableVersion(1000)));
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Predicate_chain_partial_match_fails()
    {
        await Client.MutateRowAsync(TN, "cp-chain-2",
            Mutations.SetCell(CF, "status", "active", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "role", "user", new BigtableVersion(1000)));
        var result = await Client.CheckAndMutateRowAsync(TN, "cp-chain-2",
            RowFilters.Chain(
                RowFilters.FamilyNameExact(CF2),
                RowFilters.ColumnQualifierExact("role"),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueExact("admin")),
            Mutations.SetCell(CF, "is_admin", "true", new BigtableVersion(1000)));
        result.PredicateMatched.Should().BeFalse();
    }

    #endregion

    #region True and false mutation branches

    [Fact]
    public async Task True_mutations_applied_on_match()
    {
        await Client.MutateRowAsync(TN, "cp-tf-1",
            Mutations.SetCell(CF, "flag", "yes", new BigtableVersion(1000)));
        await Client.CheckAndMutateRowAsync(TN, "cp-tf-1",
            RowFilters.Chain(RowFilters.CellsPerColumnLimit(1), RowFilters.ValueExact("yes")),
            trueMutations: new[]
            {
                Mutations.SetCell(CF, "result", "matched", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "processed", "true", new BigtableVersion(2000))
            },
            falseMutations: new[]
            {
                Mutations.SetCell(CF, "result", "not-matched", new BigtableVersion(2000))
            });
        var rows = await ReadAll(RowSet.FromRowKeys("cp-tf-1"), RowFilters.CellsPerColumnLimit(1));
        var cols = rows[0].Families[0].Columns.ToDictionary(c => c.Qualifier.ToStringUtf8());
        cols["result"].Cells[0].Value.ToStringUtf8().Should().Be("matched");
        cols.Should().ContainKey("processed");
    }

    [Fact]
    public async Task False_mutations_applied_on_no_match()
    {
        await Client.MutateRowAsync(TN, "cp-tf-2",
            Mutations.SetCell(CF, "flag", "no", new BigtableVersion(1000)));
        await Client.CheckAndMutateRowAsync(TN, "cp-tf-2",
            RowFilters.Chain(RowFilters.CellsPerColumnLimit(1), RowFilters.ValueExact("yes")),
            trueMutations: new[]
            {
                Mutations.SetCell(CF, "result", "matched", new BigtableVersion(2000))
            },
            falseMutations: new[]
            {
                Mutations.SetCell(CF, "result", "not-matched", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "fallback", "true", new BigtableVersion(2000))
            });
        var rows = await ReadAll(RowSet.FromRowKeys("cp-tf-2"), RowFilters.CellsPerColumnLimit(1));
        var cols = rows[0].Families[0].Columns.ToDictionary(c => c.Qualifier.ToStringUtf8());
        cols["result"].Cells[0].Value.ToStringUtf8().Should().Be("not-matched");
        cols.Should().ContainKey("fallback");
    }

    [Fact]
    public async Task True_mutations_with_delete()
    {
        await Client.MutateRowAsync(TN, "cp-tf-3",
            Mutations.SetCell(CF, "old", "data", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "flag", "yes", new BigtableVersion(1000)));
        await Client.CheckAndMutateRowAsync(TN, "cp-tf-3",
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("flag"),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueExact("yes")),
            Mutations.DeleteFromColumn(CF, "old"),
            Mutations.SetCell(CF, "new", "data", new BigtableVersion(2000)));
        var rows = await ReadAll(RowSet.FromRowKeys("cp-tf-3"), RowFilters.CellsPerColumnLimit(1));
        var cols = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().NotContain("old");
        cols.Should().Contain("new");
    }

    [Fact]
    public async Task False_mutations_with_delete()
    {
        await Client.MutateRowAsync(TN, "cp-tf-4",
            Mutations.SetCell(CF, "temp", "data", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "flag", "no", new BigtableVersion(1000)));
        await Client.CheckAndMutateRowAsync(TN, "cp-tf-4",
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("flag"),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueExact("yes")),
            trueMutations: null,
            falseMutations: new[] { Mutations.DeleteFromColumn(CF, "temp") });
        var rows = await ReadAll(RowSet.FromRowKeys("cp-tf-4"), RowFilters.CellsPerColumnLimit(1));
        rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).Should().NotContain("temp");
    }

    #endregion

    #region Sequential CAM on same row

    [Fact]
    public async Task Sequential_CAM_state_transitions()
    {
        await Client.MutateRowAsync(TN, "cp-seq-1",
            Mutations.SetCell(CF, "state", "new", new BigtableVersion(1000)));
        // new -> processing
        var r1 = await Client.CheckAndMutateRowAsync(TN, "cp-seq-1",
            RowFilters.Chain(RowFilters.CellsPerColumnLimit(1), RowFilters.ValueExact("new")),
            Mutations.SetCell(CF, "state", "processing", new BigtableVersion(2000)));
        r1.PredicateMatched.Should().BeTrue();
        // processing -> done
        var r2 = await Client.CheckAndMutateRowAsync(TN, "cp-seq-1",
            RowFilters.Chain(RowFilters.CellsPerColumnLimit(1), RowFilters.ValueExact("processing")),
            Mutations.SetCell(CF, "state", "done", new BigtableVersion(3000)));
        r2.PredicateMatched.Should().BeTrue();
        // done -> new (should fail because state is "done", not "new")
        var r3 = await Client.CheckAndMutateRowAsync(TN, "cp-seq-1",
            RowFilters.Chain(RowFilters.CellsPerColumnLimit(1), RowFilters.ValueExact("new")),
            Mutations.SetCell(CF, "state", "processing", new BigtableVersion(4000)));
        r3.PredicateMatched.Should().BeFalse();
        // Verify final state
        var rows = await ReadAll(RowSet.FromRowKeys("cp-seq-1"), RowFilters.CellsPerColumnLimit(1));
        rows[0].Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "state")
            .Cells[0].Value.ToStringUtf8().Should().Be("done");
    }

    [Fact]
    public async Task CAM_with_multi_column_mutation()
    {
        await Client.MutateRowAsync(TN, "cp-multi",
            Mutations.SetCell(CF, "status", "pending", new BigtableVersion(1000)));
        await Client.CheckAndMutateRowAsync(TN, "cp-multi",
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueExact("pending")),
            Mutations.SetCell(CF, "status", "approved", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "approved_by", "system", new BigtableVersion(2000)),
            Mutations.SetCell(CF2, "audit", "auto-approved", new BigtableVersion(2000)));
        var rows = await ReadAll(RowSet.FromRowKeys("cp-multi"), RowFilters.CellsPerColumnLimit(1));
        rows[0].Families.Should().HaveCount(2);
        var cfCols = rows[0].Families.First(f => f.Name == CF).Columns
            .ToDictionary(c => c.Qualifier.ToStringUtf8());
        cfCols["status"].Cells[0].Value.ToStringUtf8().Should().Be("approved");
        cfCols["approved_by"].Cells[0].Value.ToStringUtf8().Should().Be("system");
    }

    #endregion

    #region CAM preserves unrelated data

    [Fact]
    public async Task CAM_preserves_other_columns()
    {
        await Client.MutateRowAsync(TN, "cp-pres-1",
            Mutations.SetCell(CF, "name", "Alice", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "email", "alice@test.com", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "flag", "yes", new BigtableVersion(1000)));
        await Client.CheckAndMutateRowAsync(TN, "cp-pres-1",
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("flag"),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueExact("yes")),
            Mutations.SetCell(CF, "flag", "processed", new BigtableVersion(2000)));
        var rows = await ReadAll(RowSet.FromRowKeys("cp-pres-1"), RowFilters.CellsPerColumnLimit(1));
        var cols = rows[0].Families[0].Columns.ToDictionary(c => c.Qualifier.ToStringUtf8());
        cols["name"].Cells[0].Value.ToStringUtf8().Should().Be("Alice");
        cols["email"].Cells[0].Value.ToStringUtf8().Should().Be("alice@test.com");
        cols["flag"].Cells[0].Value.ToStringUtf8().Should().Be("processed");
    }

    [Fact]
    public async Task CAM_preserves_other_families()
    {
        await Client.MutateRowAsync(TN, "cp-pres-2",
            Mutations.SetCell(CF, "data", "important", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "flag", "yes", new BigtableVersion(1000)));
        await Client.CheckAndMutateRowAsync(TN, "cp-pres-2",
            RowFilters.Chain(
                RowFilters.FamilyNameExact(CF2),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueExact("yes")),
            Mutations.SetCell(CF2, "flag", "no", new BigtableVersion(2000)));
        var rows = await ReadAll(RowSet.FromRowKeys("cp-pres-2"), RowFilters.CellsPerColumnLimit(1));
        rows[0].Families.First(f => f.Name == CF).Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("important");
    }

    #endregion
}

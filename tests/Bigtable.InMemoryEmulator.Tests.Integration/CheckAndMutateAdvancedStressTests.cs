using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Stress tests for CheckAndMutateRow with complex patterns.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#checkandmutaterowrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CheckAndMutateAdvancedStressTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "cam-adv";
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public CheckAndMutateAdvancedStressTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
        var client = _fixture.Client;
        var tn = _fixture.GetTableName(Table);

        // Seed: 10 rows with different patterns
        for (int i = 0; i < 10; i++)
        {
            await client.MutateRowAsync(tn, $"cma-{i:D3}",
                Mutations.SetCell(CF, "status", i % 2 == 0 ? "active" : "inactive", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "count", $"{i}", new BigtableVersion(1000)),
                Mutations.SetCell(CF2, "tag", $"tag-{i}", new BigtableVersion(1000)));
        }
    }
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
    public async Task ValueExact_match_true()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cma-000",
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueExact("active")),
            new[] { Mutations.SetCell(CF, "checked", "true", new BigtableVersion(2000)) },
            null);
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task ValueExact_no_match_false()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cma-001",
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueExact("active")),
            null,
            new[] { Mutations.SetCell(CF, "checked", "false", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task ValueRegex_match()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cma-000",
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueRegex("act.*")),
            new[] { Mutations.SetCell(CF, "regex", "matched", new BigtableVersion(2000)) },
            null);
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task ValueRegex_no_match()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cma-000",
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueRegex("xyz.*")),
            new[] { Mutations.SetCell(CF, "regex", "matched", new BigtableVersion(2000)) },
            null);
        result.PredicateMatched.Should().BeFalse();
    }

    #endregion

    #region Column-based predicates

    [Fact]
    public async Task ColumnQualifierExact_match()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cma-000",
            RowFilters.ColumnQualifierExact("status"),
            new[] { Mutations.SetCell(CF, "col_checked", "yes", new BigtableVersion(2000)) },
            null);
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task ColumnQualifierExact_no_match()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cma-000",
            RowFilters.ColumnQualifierExact("nonexistent"),
            null,
            new[] { Mutations.SetCell(CF, "col_checked", "no", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeFalse();
    }

    #endregion

    #region Family-based predicates

    [Fact]
    public async Task FamilyNameExact_match()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cma-000",
            RowFilters.FamilyNameExact(CF2),
            new[] { Mutations.SetCell(CF, "fam_check", "cf2_exists", new BigtableVersion(2000)) },
            null);
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task FamilyNameExact_no_match()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cma-000",
            RowFilters.FamilyNameExact("nosuchfamily"),
            null,
            new[] { Mutations.SetCell(CF, "fam_check", "no_family", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeFalse();
    }

    #endregion

    #region Complex chain predicates

    [Fact]
    public async Task Chain_family_column_value_match()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cma-000",
            RowFilters.Chain(
                RowFilters.FamilyNameExact(CF),
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueExact("active")),
            new[] { Mutations.SetCell(CF, "complex", "matched", new BigtableVersion(2000)) },
            null);
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Chain_cross_family_check()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cma-005",
            RowFilters.Chain(
                RowFilters.FamilyNameExact(CF2),
                RowFilters.ColumnQualifierExact("tag"),
                RowFilters.ValueExact("tag-5")),
            new[] { Mutations.SetCell(CF, "xcheck", "from_cf2", new BigtableVersion(2000)) },
            null);
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Chain_multiple_cell_limits()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cma-000",
            RowFilters.Chain(
                RowFilters.FamilyNameExact(CF),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.CellsPerRowLimit(1)),
            new[] { Mutations.SetCell(CF, "limit_check", "ok", new BigtableVersion(2000)) },
            null);
        result.PredicateMatched.Should().BeTrue();
    }

    #endregion

    #region True and false mutations

    [Fact]
    public async Task True_branch_sets_cell()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cma-000",
            RowFilters.PassAllFilter(),
            new[] { Mutations.SetCell(CF, "true_set", "yes", new BigtableVersion(2000)) },
            null);
        result.PredicateMatched.Should().BeTrue();
        var rows = await ReadAll(RowSet.FromRowKeys("cma-000"),
            RowFilters.Chain(RowFilters.ColumnQualifierExact("true_set"), RowFilters.CellsPerColumnLimit(1)));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("yes");
    }

    [Fact]
    public async Task False_branch_sets_cell()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cma-000",
            RowFilters.BlockAllFilter(),
            null,
            new[] { Mutations.SetCell(CF, "false_set", "no", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeFalse();
        var rows = await ReadAll(RowSet.FromRowKeys("cma-000"),
            RowFilters.Chain(RowFilters.ColumnQualifierExact("false_set"), RowFilters.CellsPerColumnLimit(1)));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("no");
    }

    [Fact]
    public async Task True_branch_deletes_from_row()
    {
        // Create a temp row
        await Client.MutateRowAsync(TN, "cma-del-true",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)));
        var result = await Client.CheckAndMutateRowAsync(TN, "cma-del-true",
            RowFilters.PassAllFilter(),
            new[] { Mutations.DeleteFromRow() },
            null);
        result.PredicateMatched.Should().BeTrue();
        var rows = await ReadAll(RowSet.FromRowKeys("cma-del-true"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task True_branch_deletes_from_family()
    {
        await Client.MutateRowAsync(TN, "cma-del-fam",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "b", "v", new BigtableVersion(1000)));
        var result = await Client.CheckAndMutateRowAsync(TN, "cma-del-fam",
            RowFilters.PassAllFilter(),
            new[] { Mutations.DeleteFromFamily(CF) },
            null);
        result.PredicateMatched.Should().BeTrue();
        var rows = await ReadAll(RowSet.FromRowKeys("cma-del-fam"));
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be("cf2");
    }

    [Fact]
    public async Task True_branch_deletes_from_column()
    {
        await Client.MutateRowAsync(TN, "cma-del-col",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v", new BigtableVersion(1000)));
        var result = await Client.CheckAndMutateRowAsync(TN, "cma-del-col",
            RowFilters.PassAllFilter(),
            new[] { Mutations.DeleteFromColumn(CF, "a") },
            null);
        result.PredicateMatched.Should().BeTrue();
        var rows = await ReadAll(RowSet.FromRowKeys("cma-del-col"));
        rows[0].Families[0].Columns.Should().ContainSingle().Which.Qualifier.ToStringUtf8().Should().Be("b");
    }

    [Fact]
    public async Task Multiple_true_mutations()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cma-000",
            RowFilters.PassAllFilter(),
            new[]
            {
                Mutations.SetCell(CF, "multi1", "a", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "multi2", "b", new BigtableVersion(2000)),
                Mutations.SetCell(CF2, "multi3", "c", new BigtableVersion(2000))
            },
            null);
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Multiple_false_mutations()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cma-000",
            RowFilters.BlockAllFilter(),
            null,
            new[]
            {
                Mutations.SetCell(CF, "fmulti1", "x", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "fmulti2", "y", new BigtableVersion(2000))
            });
        result.PredicateMatched.Should().BeFalse();
    }

    #endregion

    #region Nonexistent row

    [Fact]
    public async Task Nonexistent_row_passall_false()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cma-norow",
            RowFilters.PassAllFilter(),
            new[] { Mutations.SetCell(CF, "created", "true", new BigtableVersion(2000)) },
            new[] { Mutations.SetCell(CF, "created", "false", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task Nonexistent_row_false_branch_creates_row()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cma-norow-create",
            RowFilters.PassAllFilter(),
            null,
            new[] { Mutations.SetCell(CF, "created", "yes", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeFalse();
        var rows = await ReadAll(RowSet.FromRowKeys("cma-norow-create"));
        rows.Should().ContainSingle();
    }

    #endregion

    #region Sequential CAM operations

    [Fact]
    public async Task Sequential_state_machine()
    {
        // S0 → S1 → S2
        await Client.MutateRowAsync(TN, "cma-sm",
            Mutations.SetCell(CF, "state", "S0", new BigtableVersion(1000)));

        // S0 → S1
        var r1 = await Client.CheckAndMutateRowAsync(TN, "cma-sm",
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("state"),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueExact("S0")),
            new[] { Mutations.SetCell(CF, "state", "S1", new BigtableVersion(2000)) },
            null);
        r1.PredicateMatched.Should().BeTrue();

        // S1 → S2
        var r2 = await Client.CheckAndMutateRowAsync(TN, "cma-sm",
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("state"),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueExact("S1")),
            new[] { Mutations.SetCell(CF, "state", "S2", new BigtableVersion(3000)) },
            null);
        r2.PredicateMatched.Should().BeTrue();

        // Verify final state
        var rows = await ReadAll(RowSet.FromRowKeys("cma-sm"),
            RowFilters.Chain(RowFilters.ColumnQualifierExact("state"), RowFilters.CellsPerColumnLimit(1)));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("S2");
    }

    [Fact]
    public async Task Idempotent_CAM_same_predicate()
    {
        await Client.MutateRowAsync(TN, "cma-idem",
            Mutations.SetCell(CF, "val", "X", new BigtableVersion(1000)));

        // Run same CAM twice
        for (int i = 0; i < 2; i++)
        {
            await Client.CheckAndMutateRowAsync(TN, "cma-idem",
                RowFilters.PassAllFilter(),
                new[] { Mutations.SetCell(CF, "idempotent", "yes", new BigtableVersion(2000)) },
                null);
        }

        var rows = await ReadAll(RowSet.FromRowKeys("cma-idem"),
            RowFilters.Chain(RowFilters.ColumnQualifierExact("idempotent"), RowFilters.CellsPerColumnLimit(1)));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("yes");
    }

    [Fact]
    public async Task CAM_5_different_rows()
    {
        for (int i = 0; i < 5; i++)
        {
            var result = await Client.CheckAndMutateRowAsync(TN, $"cma-{i:D3}",
                RowFilters.PassAllFilter(),
                new[] { Mutations.SetCell(CF, "cam5", $"done-{i}", new BigtableVersion(2000)) },
                null);
            result.PredicateMatched.Should().BeTrue();
        }
    }

    #endregion

    #region Predicate preserves data

    [Fact]
    public async Task CAM_does_not_modify_predicate_columns()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cma-000",
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.ValueExact("active")),
            new[] { Mutations.SetCell(CF, "separate", "val", new BigtableVersion(2000)) },
            null);
        result.PredicateMatched.Should().BeTrue();

        // Status column unchanged
        var rows = await ReadAll(RowSet.FromRowKeys("cma-000"),
            RowFilters.Chain(RowFilters.ColumnQualifierExact("status"), RowFilters.CellsPerColumnLimit(1)));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("active");
    }

    [Fact]
    public async Task CAM_preserves_other_families()
    {
        await Client.CheckAndMutateRowAsync(TN, "cma-000",
            RowFilters.PassAllFilter(),
            new[] { Mutations.SetCell(CF, "preserve_test", "val", new BigtableVersion(2000)) },
            null);
        var rows = await ReadAll(RowSet.FromRowKeys("cma-000"));
        rows[0].Families.Select(f => f.Name).Should().Contain("cf2");
    }

    #endregion
}

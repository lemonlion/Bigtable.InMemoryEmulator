using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Stress tests for CheckAndMutateRow — complex predicates, branching, edge cases.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#checkandmutaterowrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CheckAndMutateStressTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "cam-stress";
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public CheckAndMutateStressTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });

        // Seed 10 rows
        for (int i = 0; i < 10; i++)
            await Client.MutateRowAsync(TN, $"cam-{i:D2}",
                Mutations.SetCell(CF, "status", i % 2 == 0 ? "active" : "inactive", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "count", $"{i}", new BigtableVersion(1000)),
                Mutations.SetCell(CF2, "data", $"payload-{i}", new BigtableVersion(1000)));
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

    #region Predicate matching

    [Fact]
    public async Task Predicate_value_regex_matches()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-00",
            RowFilters.ValueRegex("active"),
            new[] { Mutations.SetCell(CF, "marked", "yes", new BigtableVersion(2000)) },
            null);
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Predicate_value_regex_does_not_match()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-01",
            RowFilters.ValueRegex("active"),
            null,
            new[] { Mutations.SetCell(CF, "marked", "no", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task Predicate_column_qualifier_exact()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-00",
            RowFilters.ColumnQualifierExact("status"),
            new[] { Mutations.SetCell(CF, "has_status", "true", new BigtableVersion(2000)) },
            null);
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Predicate_column_qualifier_nonexistent()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-00",
            RowFilters.ColumnQualifierExact("nonexistent"),
            null,
            new[] { Mutations.SetCell(CF, "no_col", "true", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task Predicate_family_name_exact()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-00",
            RowFilters.FamilyNameExact(CF2),
            new[] { Mutations.SetCell(CF, "cf2_exists", "yes", new BigtableVersion(2000)) },
            null);
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Predicate_family_nonexistent() 
    {
        // CF "nofam" doesn't exist → predicate should not match
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-00",
            RowFilters.FamilyNameExact("nofam"),
            null,
            new[] { Mutations.SetCell(CF, "nofam_check", "false", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task Predicate_pass_all_on_existing_row()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-00",
            RowFilters.PassAllFilter(),
            new[] { Mutations.SetCell(CF, "exists", "true", new BigtableVersion(2000)) },
            null);
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Predicate_block_all_always_false()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-00",
            RowFilters.BlockAllFilter(),
            null,
            new[] { Mutations.SetCell(CF, "blocked", "true", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task No_predicate_nonexistent_row_returns_false()
    {
        // Ref: null predicate checks row existence
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-nonexistent",
            predicateFilter: null,
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "created", "true", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task No_predicate_existing_row_returns_true()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-00",
            predicateFilter: null,
            trueMutations: new[] { Mutations.SetCell(CF, "confirmed", "true", new BigtableVersion(2000)) },
            falseMutations: null);
        result.PredicateMatched.Should().BeTrue();
    }

    #endregion

    #region Complex predicates

    [Fact]
    public async Task Predicate_chain_family_and_value()
    {
        var predicate = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.ValueRegex("active"));
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-00",
            predicate,
            new[] { Mutations.SetCell(CF, "chain_check", "passed", new BigtableVersion(2000)) },
            null);
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Predicate_chain_fails_when_one_part_fails()
    {
        var predicate = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF),
            RowFilters.ValueRegex("NONEXISTENT"));
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-00",
            predicate, null,
            new[] { Mutations.SetCell(CF, "chain_fail", "true", new BigtableVersion(2000)) });
        result.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task Predicate_interleave()
    {
        var predicate = RowFilters.Interleave(
            RowFilters.ValueRegex("active"),
            RowFilters.ValueRegex("NOMATCH"));
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-00",
            predicate,
            new[] { Mutations.SetCell(CF, "interleave_check", "ok", new BigtableVersion(2000)) },
            null);
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Predicate_timestamp_range()
    {
        var predicate = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                StartTimestampMicros = 500_000,
                EndTimestampMicros = 1_500_000
            }
        };
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-00",
            predicate,
            new[] { Mutations.SetCell(CF, "ts_check", "ok", new BigtableVersion(2000)) },
            null);
        result.PredicateMatched.Should().BeTrue();
    }

    #endregion

    #region True/false branch mutations

    [Fact]
    public async Task True_branch_sets_cell()
    {
        await Client.CheckAndMutateRowAsync(TN, "cam-00",
            RowFilters.PassAllFilter(),
            new[] { Mutations.SetCell(CF, "added", "by_true", new BigtableVersion(2000)) },
            null);
        var rows = await ReadAll(RowSet.FromRowKeys("cam-00"));
        rows[0].Families.First(f => f.Name == CF).Columns
            .Any(c => c.Qualifier.ToStringUtf8() == "added").Should().BeTrue();
    }

    [Fact]
    public async Task False_branch_sets_cell()
    {
        await Client.CheckAndMutateRowAsync(TN, "cam-01",
            RowFilters.ValueRegex("active"),
            null,
            new[] { Mutations.SetCell(CF, "fallback", "by_false", new BigtableVersion(2000)) });
        var rows = await ReadAll(RowSet.FromRowKeys("cam-01"));
        rows[0].Families.First(f => f.Name == CF).Columns
            .Any(c => c.Qualifier.ToStringUtf8() == "fallback").Should().BeTrue();
    }

    [Fact]
    public async Task True_branch_multiple_mutations()
    {
        await Client.CheckAndMutateRowAsync(TN, "cam-02",
            RowFilters.PassAllFilter(),
            new[]
            {
                Mutations.SetCell(CF, "m1", "v1", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "m2", "v2", new BigtableVersion(2000)),
                Mutations.SetCell(CF2, "m3", "v3", new BigtableVersion(2000))
            },
            null);
        var rows = await ReadAll(RowSet.FromRowKeys("cam-02"));
        rows[0].Families.First(f => f.Name == CF).Columns.Select(c => c.Qualifier.ToStringUtf8())
            .Should().Contain(new[] { "m1", "m2" });
    }

    [Fact]
    public async Task True_branch_with_delete_from_row()
    {
        await Client.CheckAndMutateRowAsync(TN, "cam-03",
            RowFilters.PassAllFilter(),
            new[] { Mutations.DeleteFromRow() },
            null);
        var rows = await ReadAll(RowSet.FromRowKeys("cam-03"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task True_branch_with_delete_from_family()
    {
        await Client.CheckAndMutateRowAsync(TN, "cam-04",
            RowFilters.PassAllFilter(),
            new[] { Mutations.DeleteFromFamily(CF2) },
            null);
        var rows = await ReadAll(RowSet.FromRowKeys("cam-04"));
        rows.Should().ContainSingle();
        rows[0].Families.Select(f => f.Name).Should().NotContain(CF2);
    }

    [Fact]
    public async Task True_branch_with_delete_from_column()
    {
        await Client.CheckAndMutateRowAsync(TN, "cam-05",
            RowFilters.PassAllFilter(),
            new[] { Mutations.DeleteFromColumn(CF, "status", new BigtableVersionRange(new BigtableVersion(0), new BigtableVersion(2000))) },
            null);
        var rows = await ReadAll(RowSet.FromRowKeys("cam-05"));
        rows[0].Families.First(f => f.Name == CF).Columns
            .Select(c => c.Qualifier.ToStringUtf8()).Should().NotContain("status");
    }

    [Fact]
    public async Task False_branch_creates_new_row()
    {
        await Client.CheckAndMutateRowAsync(TN, "cam-new-row",
            predicateFilter: null,
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "c", "created", new BigtableVersion(2000)) });
        var rows = await ReadAll(RowSet.FromRowKeys("cam-new-row"));
        rows.Should().ContainSingle();
    }

    #endregion

    #region Preserving unrelated data

    [Fact]
    public async Task True_branch_preserves_unrelated_columns()
    {
        await Client.CheckAndMutateRowAsync(TN, "cam-06",
            RowFilters.PassAllFilter(),
            new[] { Mutations.SetCell(CF, "new_col", "value", new BigtableVersion(2000)) },
            null);
        var rows = await ReadAll(RowSet.FromRowKeys("cam-06"));
        var cfCols = rows[0].Families.First(f => f.Name == CF).Columns.Select(c => c.Qualifier.ToStringUtf8());
        cfCols.Should().Contain("status");
        cfCols.Should().Contain("count");
        cfCols.Should().Contain("new_col");
    }

    [Fact]
    public async Task True_branch_preserves_other_families()
    {
        await Client.CheckAndMutateRowAsync(TN, "cam-07",
            RowFilters.PassAllFilter(),
            new[] { Mutations.SetCell(CF, "new_col", "value", new BigtableVersion(2000)) },
            null);
        var rows = await ReadAll(RowSet.FromRowKeys("cam-07"));
        rows[0].Families.Select(f => f.Name).Should().Contain(CF2);
    }

    #endregion

    #region Sequential CheckAndMutate

    [Fact]
    public async Task Two_sequential_CheckAndMutate_on_same_row()
    {
        // First: set flag
        await Client.CheckAndMutateRowAsync(TN, "cam-08",
            RowFilters.PassAllFilter(),
            new[] { Mutations.SetCell(CF, "step", "1", new BigtableVersion(2000)) },
            null);

        // Second: check flag, add step 2
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-08",
            RowFilters.Chain(RowFilters.ColumnQualifierExact("step"), RowFilters.ValueExact("1")),
            new[] { Mutations.SetCell(CF, "step2", "done", new BigtableVersion(3000)) },
            null);
        result.PredicateMatched.Should().BeTrue();

        var rows = await ReadAll(RowSet.FromRowKeys("cam-08"));
        rows[0].Families.First(f => f.Name == CF).Columns.Select(c => c.Qualifier.ToStringUtf8())
            .Should().Contain("step2");
    }

    [Fact]
    public async Task CheckAndMutate_toggle_value()
    {
        // Write initial value
        await Client.MutateRowAsync(TN, "cam-toggle",
            Mutations.SetCell(CF, "status", "on", new BigtableVersion(1000)));

        // If latest is "on" → set to "off" (use CellsPerColumnLimit to check latest only)
        var r1 = await Client.CheckAndMutateRowAsync(TN, "cam-toggle",
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueExact("on")),
            new[] { Mutations.SetCell(CF, "status", "off", new BigtableVersion(2000)) },
            null);
        r1.PredicateMatched.Should().BeTrue();

        // Now latest is "off" — checking for "on" (latest only) should not match
        var r2 = await Client.CheckAndMutateRowAsync(TN, "cam-toggle",
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueExact("on")),
            null,
            new[] { Mutations.SetCell(CF, "toggled", "ok", new BigtableVersion(3000)) });
        r2.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAndMutate_idempotent_when_no_state_change()
    {
        // Two identical calls, both should return true
        for (int i = 0; i < 2; i++)
        {
            var result = await Client.CheckAndMutateRowAsync(TN, "cam-09",
                RowFilters.PassAllFilter(),
                new[] { Mutations.SetCell(CF, "idem", "same", new BigtableVersion(2000)) },
                null);
            result.PredicateMatched.Should().BeTrue();
        }
    }

    #endregion

    #region Cross-family predicates

    [Fact]
    public async Task Predicate_on_cf2_mutate_cf1()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "cam-00",
            RowFilters.Chain(RowFilters.FamilyNameExact(CF2), RowFilters.ValueRegex("payload-0")),
            new[] { Mutations.SetCell(CF, "from_cf2", "true", new BigtableVersion(2000)) },
            null);
        result.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Predicate_on_cf1_delete_from_cf2()
    {
        await Client.CheckAndMutateRowAsync(TN, "cam-00",
            RowFilters.Chain(RowFilters.FamilyNameExact(CF), RowFilters.ValueRegex("active")),
            new[] { Mutations.DeleteFromFamily(CF2) },
            null);
        var rows = await ReadAll(RowSet.FromRowKeys("cam-00"));
        rows[0].Families.Select(f => f.Name).Should().NotContain(CF2);
    }

    #endregion
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for cross-family operations — multi-family reads, writes, filters,
/// and interactions between column families.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CrossFamilyStressTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "crossfamily-stress";
    private const string CF1 = "alpha";
    private const string CF2 = "beta";
    private const string CF3 = "gamma";
    private const string CF4 = "delta";

    public CrossFamilyStressTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF1, CF2, CF3, CF4 });

        // Seed 10 rows, each with data in all 4 families
        for (int i = 0; i < 10; i++)
        {
            var mutations = new List<Mutation>();
            foreach (var fam in new[] { CF1, CF2, CF3, CF4 })
            {
                mutations.Add(Mutations.SetCell(fam, "id", $"{i}", new BigtableVersion(1000)));
                mutations.Add(Mutations.SetCell(fam, "val", $"{fam}-{i}", new BigtableVersion(1000)));
            }
            await Client.MutateRowAsync(TN, $"xf-{i:D2}", mutations.ToArray());
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

    #region Multi-family reads

    [Fact]
    public async Task All_4_families_returned()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("xf-00"));
        rows.Should().ContainSingle();
        rows[0].Families.Select(f => f.Name).Should().Contain(new[] { CF1, CF2, CF3, CF4 });
    }

    [Fact]
    public async Task Families_sorted_lexicographically()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("xf-00"));
        var names = rows[0].Families.Select(f => f.Name).ToList();
        names.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Each_family_has_correct_columns()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("xf-00"));
        foreach (var fam in rows[0].Families)
        {
            var cols = fam.Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
            cols.Should().Contain(new[] { "id", "val" });
        }
    }

    [Fact]
    public async Task Each_family_data_is_independent()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("xf-05"));
        foreach (var fam in rows[0].Families)
        {
            var valCell = fam.Columns.First(c => c.Qualifier.ToStringUtf8() == "val").Cells[0];
            valCell.Value.ToStringUtf8().Should().StartWith($"{fam.Name}-");
        }
    }

    #endregion

    #region Family filter isolation

    [Fact]
    public async Task FamilyNameExact_returns_single_family()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("xf-00"), RowFilters.FamilyNameExact(CF1));
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF1);
    }

    [Fact]
    public async Task FamilyNameExact_each_family_independently()
    {
        foreach (var fam in new[] { CF1, CF2, CF3, CF4 })
        {
            var rows = await ReadAll(RowSet.FromRowKeys("xf-00"), RowFilters.FamilyNameExact(fam));
            rows.Should().ContainSingle();
            rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(fam);
        }
    }

    [Fact]
    public async Task FamilyNameRegex_selects_subset()
    {
        // "alpha|gamma" selects 2 of 4 families
        var rows = await ReadAll(RowSet.FromRowKeys("xf-00"), RowFilters.FamilyNameRegex("alpha|gamma"));
        rows.Should().ContainSingle();
        rows[0].Families.Select(f => f.Name).Should().Contain(new[] { CF1, CF3 });
        rows[0].Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task FamilyNameRegex_pattern_match()
    {
        // ".*a$" matches "alpha", "beta", "gamma", "delta" — actually all end in 'a'
        var rows = await ReadAll(RowSet.FromRowKeys("xf-00"), RowFilters.FamilyNameRegex(".*a"));
        rows.Should().ContainSingle();
        rows[0].Families.Should().HaveCount(4);
    }

    [Fact]
    public async Task FamilyNameRegex_single_char_class()
    {
        // "[abd].*" matches alpha, beta, delta
        var rows = await ReadAll(RowSet.FromRowKeys("xf-00"), RowFilters.FamilyNameRegex("[abd].*"));
        rows.Should().ContainSingle();
        rows[0].Families.Select(f => f.Name).Should().Contain(new[] { CF1, CF2, CF4 });
    }

    #endregion

    #region Cross-family mutations

    [Fact]
    public async Task Write_to_specific_family_others_unchanged()
    {
        await Client.MutateRowAsync(TN, "xf-00",
            Mutations.SetCell(CF1, "extra", "new", new BigtableVersion(2000)));
        var rows = await ReadAll(RowSet.FromRowKeys("xf-00"));
        var alphaCols = rows[0].Families.First(f => f.Name == CF1).Columns.Select(c => c.Qualifier.ToStringUtf8());
        alphaCols.Should().Contain("extra");
        // Other families unchanged
        rows[0].Families.First(f => f.Name == CF2).Columns.Should().HaveCount(2);
    }

    [Fact]
    public async Task Delete_from_one_family_preserves_others()
    {
        await Client.MutateRowAsync(TN, "xf-01", Mutations.DeleteFromFamily(CF3));
        var rows = await ReadAll(RowSet.FromRowKeys("xf-01"));
        rows.Should().ContainSingle();
        rows[0].Families.Select(f => f.Name).Should().NotContain(CF3);
        rows[0].Families.Should().HaveCount(3);
    }

    [Fact]
    public async Task Delete_from_all_families_makes_row_invisible()
    {
        await Client.MutateRowAsync(TN, "xf-02",
            Mutations.DeleteFromFamily(CF1),
            Mutations.DeleteFromFamily(CF2),
            Mutations.DeleteFromFamily(CF3),
            Mutations.DeleteFromFamily(CF4));
        var rows = await ReadAll(RowSet.FromRowKeys("xf-02"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_column_from_specific_family()
    {
        await Client.MutateRowAsync(TN, "xf-03",
            Mutations.DeleteFromColumn(CF2, "val", new BigtableVersionRange(new BigtableVersion(0), new BigtableVersion(2000))));
        var rows = await ReadAll(RowSet.FromRowKeys("xf-03"));
        var betaCols = rows[0].Families.First(f => f.Name == CF2).Columns.Select(c => c.Qualifier.ToStringUtf8());
        betaCols.Should().Contain("id");
        betaCols.Should().NotContain("val");
    }

    [Fact]
    public async Task Write_to_multiple_families_in_single_mutation()
    {
        await Client.MutateRowAsync(TN, "xf-04",
            Mutations.SetCell(CF1, "new1", "x", new BigtableVersion(2000)),
            Mutations.SetCell(CF2, "new2", "y", new BigtableVersion(2000)),
            Mutations.SetCell(CF3, "new3", "z", new BigtableVersion(2000)));
        var rows = await ReadAll(RowSet.FromRowKeys("xf-04"));
        rows[0].Families.First(f => f.Name == CF1).Columns.Select(c => c.Qualifier.ToStringUtf8())
            .Should().Contain("new1");
        rows[0].Families.First(f => f.Name == CF2).Columns.Select(c => c.Qualifier.ToStringUtf8())
            .Should().Contain("new2");
    }

    #endregion

    #region Cross-family filters (chain + interleave)

    [Fact]
    public async Task Chain_family_then_column_qualifier()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF1),
            RowFilters.ColumnQualifierExact("val"));
        var rows = await ReadAll(RowSet.FromRowKeys("xf-00"), filter);
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle();
        rows[0].Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("val");
    }

    [Fact]
    public async Task Interleave_two_families()
    {
        var filter = RowFilters.Interleave(
            RowFilters.FamilyNameExact(CF1),
            RowFilters.FamilyNameExact(CF4));
        var rows = await ReadAll(RowSet.FromRowKeys("xf-00"), filter);
        rows.Should().ContainSingle();
        rows[0].Families.Select(f => f.Name).Should().Contain(new[] { CF1, CF4 });
        rows[0].Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Interleave_three_families()
    {
        var filter = RowFilters.Interleave(
            RowFilters.FamilyNameExact(CF1),
            RowFilters.FamilyNameExact(CF2),
            RowFilters.FamilyNameExact(CF3));
        var rows = await ReadAll(RowSet.FromRowKeys("xf-00"), filter);
        rows.Should().ContainSingle();
        rows[0].Families.Should().HaveCount(3);
    }

    [Fact]
    public async Task Condition_per_family()
    {
        // Predicate: does alpha family have value "alpha-5"?
        var filter = RowFilters.Condition(
            RowFilters.Chain(RowFilters.FamilyNameExact(CF1), RowFilters.ValueRegex("alpha-5")),
            RowFilters.FamilyNameExact(CF2),   // true: return beta
            RowFilters.FamilyNameExact(CF3));   // false: return gamma
        var rows5 = await ReadAll(RowSet.FromRowKeys("xf-05"), filter);
        rows5.Should().ContainSingle();
        rows5[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF2);

        var rows0 = await ReadAll(RowSet.FromRowKeys("xf-00"), filter);
        rows0.Should().ContainSingle();
        rows0[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF3);
    }

    [Fact]
    public async Task CellsPerRowLimit_counts_across_all_families()
    {
        // Each row has 4 families x 2 cols = 8 cells
        var rows = await ReadAll(RowSet.FromRowKeys("xf-00"), RowFilters.CellsPerRowLimit(3));
        rows.Should().ContainSingle();
        var totalCells = rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();
        totalCells.Should().Be(3);
    }

    [Fact]
    public async Task CellsPerRowOffset_skips_across_families()
    {
        // Skip first 6 cells (from first 3 families), get last 2
        var rows = await ReadAll(RowSet.FromRowKeys("xf-00"), RowFilters.CellsPerRowOffset(6));
        rows.Should().ContainSingle();
        var totalCells = rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();
        totalCells.Should().Be(2);
    }

    #endregion

    #region Cross-family versioning

    [Fact]
    public async Task Different_families_different_version_counts()
    {
        await Client.MutateRowAsync(TN, "xf-06",
            Mutations.SetCell(CF1, "id", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF1, "id", "v3", new BigtableVersion(3000)));
        // CF1 now has 3 versions of "id", CF2 still has 1
        var rows = await ReadAll(RowSet.FromRowKeys("xf-06"));
        var alphaId = rows[0].Families.First(f => f.Name == CF1).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "id").Cells;
        alphaId.Should().HaveCount(3);
        var betaId = rows[0].Families.First(f => f.Name == CF2).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "id").Cells;
        betaId.Should().HaveCount(1);
    }

    [Fact]
    public async Task CellsPerColumnLimit_applies_independently_per_family()
    {
        await Client.MutateRowAsync(TN, "xf-07",
            Mutations.SetCell(CF1, "id", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF1, "id", "v3", new BigtableVersion(3000)));

        var rows = await ReadAll(RowSet.FromRowKeys("xf-07"), RowFilters.CellsPerColumnLimit(1));
        foreach (var fam in rows[0].Families)
            foreach (var col in fam.Columns)
                col.Cells.Should().ContainSingle();
    }

    #endregion

    #region ReadModifyWrite cross-family

    [Fact]
    public async Task ReadModifyWrite_increment_in_different_families()
    {
        await Client.ReadModifyWriteRowAsync(TN, "xf-08",
            ReadModifyWriteRules.Increment(CF1, "counter", 5),
            ReadModifyWriteRules.Increment(CF2, "counter", 10));
        var rows = await ReadAll(RowSet.FromRowKeys("xf-08"));
        rows.Should().ContainSingle();
        var alphaCounter = rows[0].Families.First(f => f.Name == CF1).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "counter").Cells[0];
        var betaCounter = rows[0].Families.First(f => f.Name == CF2).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "counter").Cells[0];
        var alphaVal = BitConverter.ToInt64(alphaCounter.Value.ToByteArray().Reverse().ToArray(), 0);
        var betaVal = BitConverter.ToInt64(betaCounter.Value.ToByteArray().Reverse().ToArray(), 0);
        alphaVal.Should().Be(5);
        betaVal.Should().Be(10);
    }

    [Fact]
    public async Task ReadModifyWrite_append_in_different_families()
    {
        await Client.ReadModifyWriteRowAsync(TN, "xf-09",
            ReadModifyWriteRules.Append(CF1, "log", "hello-"),
            ReadModifyWriteRules.Append(CF3, "log", "world"));
        var rows = await ReadAll(RowSet.FromRowKeys("xf-09"));
        var alphaLog = rows[0].Families.First(f => f.Name == CF1).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "log").Cells[0].Value.ToStringUtf8();
        var gammaLog = rows[0].Families.First(f => f.Name == CF3).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "log").Cells[0].Value.ToStringUtf8();
        alphaLog.Should().Be("hello-");
        gammaLog.Should().Be("world");
    }

    #endregion

    #region CheckAndMutate cross-family

    [Fact]
    public async Task CheckAndMutate_predicate_one_family_mutate_another()
    {
        // Check alpha family, mutate beta
        var result = await Client.CheckAndMutateRowAsync(TN, "xf-05",
            RowFilters.Chain(RowFilters.FamilyNameExact(CF1), RowFilters.ValueRegex("alpha-5")),
            new[] { Mutations.SetCell(CF2, "cam", "done", new BigtableVersion(2000)) },
            null);
        result.PredicateMatched.Should().BeTrue();
        var rows = await ReadAll(RowSet.FromRowKeys("xf-05"), RowFilters.FamilyNameExact(CF2));
        rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).Should().Contain("cam");
    }

    [Fact]
    public async Task CheckAndMutate_mutate_multiple_families()
    {
        var result = await Client.CheckAndMutateRowAsync(TN, "xf-05",
            RowFilters.PassAllFilter(),
            new[]
            {
                Mutations.SetCell(CF1, "mark", "1", new BigtableVersion(2000)),
                Mutations.SetCell(CF3, "mark", "1", new BigtableVersion(2000)),
                Mutations.SetCell(CF4, "mark", "1", new BigtableVersion(2000)),
            },
            null);
        result.PredicateMatched.Should().BeTrue();
        var rows = await ReadAll(RowSet.FromRowKeys("xf-05"));
        var familiesWithMark = rows[0].Families
            .Where(f => f.Columns.Any(c => c.Qualifier.ToStringUtf8() == "mark"))
            .Select(f => f.Name).ToList();
        familiesWithMark.Should().Contain(new[] { CF1, CF3, CF4 });
    }

    #endregion
}

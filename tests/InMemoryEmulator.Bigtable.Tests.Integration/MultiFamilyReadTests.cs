using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for reading data from multi-family tables with varied schemas.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MultiFamilyReadTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "mf-read";
    private const string CF1 = "profile";
    private const string CF2 = "activity";
    private const string CF3 = "settings";

    public MultiFamilyReadTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF1, CF2, CF3 });
        var c = Client;
        var tn = TN;
        // Seed 10 rows with data across all 3 families
        for (int r = 0; r < 10; r++)
        {
            var key = $"mfr-{r:D2}";
            var mutations = new List<Mutation>
            {
                Mutations.SetCell(CF1, "name", $"user-{r}", new BigtableVersion(1000)),
                Mutations.SetCell(CF1, "email", $"user{r}@test.com", new BigtableVersion(1000)),
                Mutations.SetCell(CF2, "last_login", $"2024-01-{r + 1:D2}", new BigtableVersion(1000)),
                Mutations.SetCell(CF2, "login_count", $"{r * 10}", new BigtableVersion(1000)),
                Mutations.SetCell(CF3, "theme", r % 2 == 0 ? "dark" : "light", new BigtableVersion(1000)),
                Mutations.SetCell(CF3, "language", r % 3 == 0 ? "en" : "fr", new BigtableVersion(1000))
            };
            await c.MutateRowAsync(tn, key, mutations.ToArray());
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

    #region Family filters

    [Fact]
    public async Task Read_profile_family_only()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("mfr-00"), RowFilters.FamilyNameExact(CF1));
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF1);
        rows[0].Families[0].Columns.Should().HaveCount(2);
    }

    [Fact]
    public async Task Read_activity_family_only()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("mfr-00"), RowFilters.FamilyNameExact(CF2));
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF2);
    }

    [Fact]
    public async Task Read_settings_family_only()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("mfr-00"), RowFilters.FamilyNameExact(CF3));
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF3);
    }

    [Fact]
    public async Task Read_nonexistent_family_returns_empty()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("mfr-00"), RowFilters.FamilyNameExact("nonexistent"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Read_two_families_via_interleave()
    {
        var filter = RowFilters.Interleave(
            RowFilters.FamilyNameExact(CF1),
            RowFilters.FamilyNameExact(CF3));
        var rows = await ReadAll(RowSet.FromRowKeys("mfr-00"), filter);
        rows[0].Families.Should().HaveCount(2);
        rows[0].Families.Select(f => f.Name).Should().BeEquivalentTo(new[] { CF1, CF3 });
    }

    [Fact]
    public async Task Read_all_families()
    {
        var rows = await ReadAll(RowSet.FromRowKeys("mfr-00"));
        rows[0].Families.Should().HaveCount(3);
    }

    #endregion

    #region Family + column filters

    [Fact]
    public async Task Family_and_column_filter()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF1),
            RowFilters.ColumnQualifierExact("name"));
        var rows = await ReadAll(RowSet.FromRowKeys("mfr-00"), filter);
        rows[0].Families.Should().ContainSingle();
        rows[0].Families[0].Columns.Should().ContainSingle()
            .Which.Cells[0].Value.ToStringUtf8().Should().Be("user-0");
    }

    [Fact]
    public async Task Cross_family_column_filter_returns_both()
    {
        // ColumnQualifierExact without family filter matches columns in all families
        var filter = RowFilters.ColumnQualifierExact("language");
        var rows = await ReadAll(RowSet.FromRowKeys("mfr-00"), filter);
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF3);
    }

    [Fact]
    public async Task Column_qualifier_regex_across_families()
    {
        var filter = RowFilters.ColumnQualifierRegex(".*_count|language");
        var rows = await ReadAll(RowSet.FromRowKeys("mfr-00"), filter);
        var allCols = rows[0].Families.SelectMany(f => f.Columns.Select(c => c.Qualifier.ToStringUtf8())).ToList();
        allCols.Should().BeEquivalentTo(new[] { "login_count", "language" });
    }

    #endregion

    #region Family + value filters

    [Fact]
    public async Task Value_filter_scoped_to_family()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF3),
            RowFilters.ValueExact("dark"));
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("mfr-", "mfr~")), filter);
        rows.Should().HaveCount(5); // even rows have dark theme
    }

    [Fact]
    public async Task Value_regex_across_families()
    {
        var filter = RowFilters.ValueRegex("user-[0-2]");
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("mfr-", "mfr~")), filter);
        rows.Should().HaveCount(3); // only profile family has matching values
    }

    #endregion

    #region Family mutations

    [Fact]
    public async Task Delete_one_family_preserves_others()
    {
        var key = "mfr-del-1";
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF1, "name", "test", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "action", "login", new BigtableVersion(1000)),
            Mutations.SetCell(CF3, "theme", "dark", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, key, Mutations.DeleteFromFamily(CF2));
        var rows = await ReadAll(RowSet.FromRowKeys(key));
        rows[0].Families.Should().HaveCount(2);
        rows[0].Families.Select(f => f.Name).Should().BeEquivalentTo(new[] { CF1, CF3 });
    }

    [Fact]
    public async Task Update_one_family_doesnt_affect_others()
    {
        var key = "mfr-upd-1";
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF1, "name", "original", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "action", "original", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF1, "name", "updated", new BigtableVersion(2000)));
        var rows = await ReadAll(RowSet.FromRowKeys(key), RowFilters.CellsPerColumnLimit(1));
        var profile = rows[0].Families.First(f => f.Name == CF1);
        var activity = rows[0].Families.First(f => f.Name == CF2);
        profile.Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("updated");
        activity.Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("original");
    }

    [Fact]
    public async Task Add_column_to_existing_family()
    {
        var key = "mfr-add-1";
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF1, "name", "test", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF1, "phone", "123-456", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys(key), RowFilters.FamilyNameExact(CF1));
        rows[0].Families[0].Columns.Should().HaveCount(2);
    }

    #endregion

    #region Family regex filter

    [Fact]
    public async Task Family_regex_matches_multiple()
    {
        var filter = RowFilters.FamilyNameRegex("profile|settings");
        var rows = await ReadAll(RowSet.FromRowKeys("mfr-00"), filter);
        rows[0].Families.Should().HaveCount(2);
        rows[0].Families.Select(f => f.Name).Should().BeEquivalentTo(new[] { CF1, CF3 });
    }

    [Fact]
    public async Task Family_regex_dot_star()
    {
        var filter = RowFilters.FamilyNameRegex(".*");
        var rows = await ReadAll(RowSet.FromRowKeys("mfr-00"), filter);
        rows[0].Families.Should().HaveCount(3);
    }

    [Fact]
    public async Task Family_regex_no_match()
    {
        var filter = RowFilters.FamilyNameRegex("zzz.*");
        var rows = await ReadAll(RowSet.FromRowKeys("mfr-00"), filter);
        rows.Should().BeEmpty();
    }

    #endregion

    #region Multi-family batch operations

    [Fact]
    public async Task Batch_write_across_families()
    {
        var entries = Enumerable.Range(0, 5).Select(i =>
            Mutations.CreateEntry($"mfr-batch-{i}",
                Mutations.SetCell(CF1, "name", $"batch-{i}", new BigtableVersion(1000)),
                Mutations.SetCell(CF2, "action", "created", new BigtableVersion(1000)),
                Mutations.SetCell(CF3, "theme", "dark", new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("mfr-batch-", "mfr-batch~")));
        rows.Should().HaveCount(5);
        foreach (var row in rows)
            row.Families.Should().HaveCount(3);
    }

    [Fact]
    public async Task Batch_delete_family_from_multiple_rows()
    {
        for (int i = 0; i < 3; i++)
            await Client.MutateRowAsync(TN, $"mfr-bdel-{i}",
                Mutations.SetCell(CF1, "n", "v", new BigtableVersion(1000)),
                Mutations.SetCell(CF2, "a", "v", new BigtableVersion(1000)),
                Mutations.SetCell(CF3, "s", "v", new BigtableVersion(1000)));
        var entries = Enumerable.Range(0, 3).Select(i =>
            Mutations.CreateEntry($"mfr-bdel-{i}", Mutations.DeleteFromFamily(CF2))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        for (int i = 0; i < 3; i++)
        {
            var rows = await ReadAll(RowSet.FromRowKeys($"mfr-bdel-{i}"));
            rows[0].Families.Should().HaveCount(2);
            rows[0].Families.Select(f => f.Name).Should().NotContain(CF2);
        }
    }

    #endregion

    #region RMW across families

    [Fact]
    public async Task RMW_rules_in_different_families()
    {
        var key = "mfr-rmw-1";
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF1, "name", "test", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "counter",
                ByteString.CopyFrom(BitConverter.GetBytes(0L).Reverse().ToArray()),
                new BigtableVersion(1000)));
        await Client.ReadModifyWriteRowAsync(TN, key,
            ReadModifyWriteRules.Append(CF1, "name", "-updated"),
            ReadModifyWriteRules.Increment(CF2, "counter", 1));
        var rows = await ReadAll(RowSet.FromRowKeys(key), RowFilters.CellsPerColumnLimit(1));
        var name = rows[0].Families.First(f => f.Name == CF1).Columns[0].Cells[0].Value.ToStringUtf8();
        name.Should().Be("test-updated");
        var counter = BitConverter.ToInt64(
            rows[0].Families.First(f => f.Name == CF2).Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray());
        counter.Should().Be(1);
    }

    #endregion

    #region CAM with multi-family predicates

    [Fact]
    public async Task CAM_predicate_on_one_family_mutates_another()
    {
        var key = "mfr-cam-1";
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF3, "theme", "dark", new BigtableVersion(1000)));
        var result = await Client.CheckAndMutateRowAsync(TN, key,
            RowFilters.Chain(
                RowFilters.FamilyNameExact(CF3),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueExact("dark")),
            Mutations.SetCell(CF1, "note", "dark-theme-user", new BigtableVersion(1000)));
        result.PredicateMatched.Should().BeTrue();
        var rows = await ReadAll(RowSet.FromRowKeys(key), RowFilters.FamilyNameExact(CF1));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("dark-theme-user");
    }

    #endregion
}

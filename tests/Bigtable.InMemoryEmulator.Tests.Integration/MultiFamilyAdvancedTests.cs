using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Advanced multi-family read/write/filter tests.
///
/// Ref: https://cloud.google.com/bigtable/docs/schema-design
///   "A table can have multiple column families."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MultiFamilyAdvancedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF1 = "meta";
    private const string CF2 = "data";
    private const string CF3 = "stats";

    public MultiFamilyAdvancedTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync("mf-adv", new[] { CF1, CF2, CF3 });
        var tn = _fixture.GetTableName("mf-adv");
        var client = _fixture.Client;

        // Seed data across all families
        for (int i = 1; i <= 10; i++)
        {
            await client.MutateRowAsync(tn, $"mf-{i:D3}",
                Mutations.SetCell(CF1, "name", $"item-{i}", new BigtableVersion(1000)),
                Mutations.SetCell(CF1, "type", i % 2 == 0 ? "even" : "odd", new BigtableVersion(1000)),
                Mutations.SetCell(CF2, "payload", $"data-{i}", new BigtableVersion(1000)),
                Mutations.SetCell(CF3, "count", $"{i * 100}", new BigtableVersion(1000)));
        }
    }
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("mf-adv");

    [Fact]
    public async Task Read_single_family()
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("mf-001"),
            RowFilters.FamilyNameExact(CF1)))
            rows.Add(row);

        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF1);
    }

    [Fact]
    public async Task Read_two_families()
    {
        var filter = RowFilters.Interleave(
            RowFilters.FamilyNameExact(CF1),
            RowFilters.FamilyNameExact(CF3));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("mf-001"), filter))
            rows.Add(row);

        rows.Should().ContainSingle();
        var families = rows[0].Families.Select(f => f.Name).ToList();
        families.Should().Contain(CF1);
        families.Should().Contain(CF3);
        families.Should().NotContain(CF2);
    }

    [Fact]
    public async Task Family_regex_filter()
    {
        // Match "meta" and "stats" but not "data"
        var filter = RowFilters.FamilyNameRegex("(meta|stats)");
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("mf-001"), filter))
            rows.Add(row);

        rows.Should().ContainSingle();
        var families = rows[0].Families.Select(f => f.Name).ToList();
        families.Should().Contain(CF1);
        families.Should().Contain(CF3);
    }

    [Fact]
    public async Task Delete_from_one_family_preserves_others()
    {
        await Client.MutateRowAsync(TN, "mf-del",
            Mutations.SetCell(CF1, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "v2", new BigtableVersion(1000)),
            Mutations.SetCell(CF3, "c", "v3", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "mf-del", Mutations.DeleteFromFamily(CF2));

        var row = await Client.ReadRowAsync(TN, "mf-del");
        row.Should().NotBeNull();
        var families = row!.Families.Select(f => f.Name).ToList();
        families.Should().Contain(CF1);
        families.Should().Contain(CF3);
        families.Should().NotContain(CF2);
    }

    [Fact]
    public async Task Write_to_different_families_in_batch()
    {
        var entries = Enumerable.Range(0, 5).Select(i =>
            Mutations.CreateEntry($"mf-batch-{i}",
                Mutations.SetCell(CF1, "c", "v1", new BigtableVersion(1000)),
                Mutations.SetCell(CF2, "c", "v2", new BigtableVersion(1000)))).ToArray();

        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "mf-batch-0");
        row!.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task CaM_predicate_on_one_family_mutates_another()
    {
        // Check meta family, mutate data family
        var response = await Client.CheckAndMutateRowAsync(TN, "mf-001",
            RowFilters.Chain(
                RowFilters.FamilyNameExact(CF1),
                RowFilters.ColumnQualifierExact("type"),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueRegex("odd")),
            trueMutations: new[] { Mutations.SetCell(CF2, "flag", "marked", new BigtableVersion(2000)) },
            falseMutations: null);

        response.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, "mf-001");
        row!.Families.First(f => f.Name == CF2).Columns
            .Any(c => c.Qualifier.ToStringUtf8() == "flag").Should().BeTrue();
    }

    [Fact]
    public async Task Column_specific_filter_across_families()
    {
        // Only return "name" columns from any family
        var filter = RowFilters.ColumnQualifierExact("name");
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("mf-001"), filter))
            rows.Add(row);

        rows.Should().ContainSingle();
        var allCols = rows[0].Families.SelectMany(f => f.Columns).ToList();
        allCols.Should().ContainSingle();
        allCols[0].Qualifier.ToStringUtf8().Should().Be("name");
    }

    [Fact]
    public async Task All_families_have_independent_column_namespaces()
    {
        // Same column name in different families
        await Client.MutateRowAsync(TN, "mf-ns",
            Mutations.SetCell(CF1, "shared-name", "from-meta", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "shared-name", "from-data", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "mf-ns");
        var metaVal = row!.Families.First(f => f.Name == CF1).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "shared-name").Cells[0].Value.ToStringUtf8();
        var dataVal = row.Families.First(f => f.Name == CF2).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "shared-name").Cells[0].Value.ToStringUtf8();

        metaVal.Should().Be("from-meta");
        dataVal.Should().Be("from-data");
    }

    [Fact]
    public async Task RMW_on_specific_family()
    {
        await Client.MutateRowAsync(TN, "mf-rmw",
            Mutations.SetCell(CF1, "log", "init", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "log", "init", new BigtableVersion(1000)));

        await Client.ReadModifyWriteRowAsync(TN, "mf-rmw",
            ReadModifyWriteRules.Append(CF1, "log", "-updated"));

        var row = await Client.ReadRowAsync(TN, "mf-rmw");
        var meta = row!.Families.First(f => f.Name == CF1).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "log").Cells[0].Value.ToStringUtf8();
        var data = row.Families.First(f => f.Name == CF2).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "log").Cells[0].Value.ToStringUtf8();

        meta.Should().Be("init-updated");
        data.Should().Be("init"); // Unchanged
    }
}

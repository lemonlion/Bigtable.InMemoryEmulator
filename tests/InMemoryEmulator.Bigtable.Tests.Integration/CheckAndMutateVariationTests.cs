using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for CheckAndMutateRow with various predicate and mutation combinations.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#checkandmutaterowrequest
///   "Mutates a row atomically based on the output of a predicate Reader filter."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CheckAndMutateVariationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "cam-var";

    public CheckAndMutateVariationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task True_mutations_only_match()
    {
        await Client.MutateRowAsync(TN, "cv-r1",
            Mutations.SetCell(CF, "c", "val", new BigtableVersion(1000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cv-r1",
            RowFilters.PassAllFilter(),
            new[] { Mutations.SetCell(CF, "c", "matched", new BigtableVersion(2000)) },
            null);
        resp.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, "cv-r1", RowFilters.CellsPerColumnLimit(1));
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("matched");
    }

    [Fact]
    public async Task False_mutations_only_no_match()
    {
        await Client.MutateRowAsync(TN, "cv-r2",
            Mutations.SetCell(CF, "c", "val", new BigtableVersion(1000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cv-r2",
            RowFilters.BlockAllFilter(),
            null,
            new[] { Mutations.SetCell(CF, "c", "not-matched", new BigtableVersion(2000)) });
        resp.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, "cv-r2", RowFilters.CellsPerColumnLimit(1));
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("not-matched");
    }

    [Fact]
    public async Task No_row_means_predicate_false()
    {
        var resp = await Client.CheckAndMutateRowAsync(TN, "cv-r3-noexist",
            RowFilters.PassAllFilter(),
            new[] { Mutations.SetCell(CF, "c", "true", new BigtableVersion(1000)) },
            new[] { Mutations.SetCell(CF, "c", "false", new BigtableVersion(1000)) });
        resp.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, "cv-r3-noexist");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("false");
    }

    [Fact]
    public async Task Value_regex_predicate()
    {
        await Client.MutateRowAsync(TN, "cv-r4",
            Mutations.SetCell(CF, "c", "hello123", new BigtableVersion(1000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cv-r4",
            RowFilters.ValueRegex("hello.*"),
            new[] { Mutations.SetCell(CF, "flag", "matched", new BigtableVersion(2000)) },
            null);
        resp.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task Value_regex_predicate_no_match()
    {
        await Client.MutateRowAsync(TN, "cv-r5",
            Mutations.SetCell(CF, "c", "hello", new BigtableVersion(1000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cv-r5",
            RowFilters.ValueRegex("xyz.*"),
            new[] { Mutations.SetCell(CF, "flag", "yes", new BigtableVersion(2000)) },
            new[] { Mutations.SetCell(CF, "flag", "no", new BigtableVersion(2000)) });
        resp.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task CaM_can_delete_on_true()
    {
        await Client.MutateRowAsync(TN, "cv-r6",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));
        await Client.CheckAndMutateRowAsync(TN, "cv-r6",
            RowFilters.PassAllFilter(),
            new[] { Mutations.DeleteFromRow() },
            null);
        var row = await Client.ReadRowAsync(TN, "cv-r6");
        row.Should().BeNull();
    }

    [Fact]
    public async Task CaM_can_delete_on_false()
    {
        await Client.MutateRowAsync(TN, "cv-r7",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));
        await Client.CheckAndMutateRowAsync(TN, "cv-r7",
            RowFilters.BlockAllFilter(),
            null,
            new[] { Mutations.DeleteFromRow() });
        var row = await Client.ReadRowAsync(TN, "cv-r7");
        row.Should().BeNull();
    }

    [Fact]
    public async Task CaM_multiple_true_mutations()
    {
        await Client.MutateRowAsync(TN, "cv-r8",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.CheckAndMutateRowAsync(TN, "cv-r8",
            RowFilters.PassAllFilter(),
            new[]
            {
                Mutations.SetCell(CF, "a", "1", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "b", "2", new BigtableVersion(2000))
            },
            null);
        var row = await Client.ReadRowAsync(TN, "cv-r8");
        row!.Families[0].Columns.Should().HaveCountGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task CaM_family_filter_predicate()
    {
        await Client.MutateRowAsync(TN, "cv-r9",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cv-r9",
            RowFilters.FamilyNameRegex(CF),
            new[] { Mutations.SetCell(CF, "flag", "found", new BigtableVersion(2000)) },
            null);
        resp.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task CaM_family_filter_predicate_no_match()
    {
        await Client.MutateRowAsync(TN, "cv-r10",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cv-r10",
            RowFilters.FamilyNameRegex("nonexistent"),
            new[] { Mutations.SetCell(CF, "flag", "yes", new BigtableVersion(2000)) },
            new[] { Mutations.SetCell(CF, "flag", "no", new BigtableVersion(2000)) });
        resp.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task CaM_column_qualifier_predicate()
    {
        await Client.MutateRowAsync(TN, "cv-r11",
            Mutations.SetCell(CF, "target", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "other", "v", new BigtableVersion(1000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "cv-r11",
            RowFilters.ColumnQualifierRegex("target"),
            new[] { Mutations.SetCell(CF, "flag", "found", new BigtableVersion(2000)) },
            null);
        resp.PredicateMatched.Should().BeTrue();
    }

    [Fact]
    public async Task CaM_returns_correct_predicate_matched_value()
    {
        var resp1 = await Client.CheckAndMutateRowAsync(TN, "cv-r12",
            RowFilters.PassAllFilter(),
            new[] { Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)) },
            new[] { Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)) });
        // No row exists, should be false
        resp1.PredicateMatched.Should().BeFalse();

        var resp2 = await Client.CheckAndMutateRowAsync(TN, "cv-r12",
            RowFilters.PassAllFilter(),
            new[] { Mutations.SetCell(CF, "c2", "v", new BigtableVersion(2000)) },
            null);
        // Row now exists, should be true
        resp2.PredicateMatched.Should().BeTrue();
    }
}

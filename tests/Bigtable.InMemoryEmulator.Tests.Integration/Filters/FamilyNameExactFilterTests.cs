using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class FamilyNameExactFilterTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "fam-exact";
    private const string CF1 = "alpha";
    private const string CF2 = "beta";
    private const string CF3 = "gamma";

    public FamilyNameExactFilterTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF1, CF2, CF3 });
        await Client.MutateRowAsync(TN, "r1",
            Mutations.SetCell(CF1, "c", "a1"),
            Mutations.SetCell(CF2, "c", "b1"),
            Mutations.SetCell(CF3, "c", "g1"));
        await Client.MutateRowAsync(TN, "r2",
            Mutations.SetCell(CF1, "c", "a2"),
            Mutations.SetCell(CF2, "c", "b2"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Filter_single_family()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.FamilyNameExact(CF1));
        row.Should().NotBeNull();
        row!.Families.Should().ContainSingle();
        row.Families[0].Name.Should().Be(CF1);
    }

    [Fact]
    public async Task Filter_excludes_other_families()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.FamilyNameExact(CF2));
        row!.Families.Should().ContainSingle();
        row.Families[0].Name.Should().Be(CF2);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("b1");
    }

    [Fact]
    public async Task Nonexistent_family_returns_null()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.FamilyNameExact("nope"));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Family_filter_on_row_without_family()
    {
        var row = await Client.ReadRowAsync(TN, "r2", RowFilters.FamilyNameExact(CF3));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Interleave_two_families()
    {
        var filter = RowFilters.Interleave(
            RowFilters.FamilyNameExact(CF1),
            RowFilters.FamilyNameExact(CF3));
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
        row!.Families.Select(f => f.Name).Should().BeEquivalentTo(new[] { CF1, CF3 });
    }

    [Fact]
    public async Task Family_filter_all_rows()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.FamilyNameExact(CF1)))
            rows.Add(r);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Chain_family_and_column()
    {
        var filter = RowFilters.Chain(
            RowFilters.FamilyNameExact(CF2),
            RowFilters.ColumnQualifierExact("c"));
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("b1");
    }

    [Fact]
    public async Task Family_regex_matches_multiple()
    {
        // alpha and gamma both contain 'a'
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.FamilyNameRegex(".*a.*")))
            rows.Add(r);
        // all three families contain 'a' (alpha, beta, gamma), so both rows match with all families
        rows.Should().HaveCount(2);
        var r1 = rows.First(r => r.Key.ToStringUtf8() == "r1");
        r1.Families.Should().HaveCount(3);
    }

    [Fact]
    public async Task Family_exact_case_sensitive()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.FamilyNameExact("Alpha"));
        row.Should().BeNull(); // case sensitive
    }
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MultiFamilyFilterTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "mf-filt";

    public MultiFamilyFilterTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { "personal", "work", "meta" });
        var tn = _fixture.GetTableName(Table);
        await Client.MutateRowAsync(tn, "user1",
            Mutations.SetCell("personal", "name", "Alice"),
            Mutations.SetCell("personal", "age", "30"),
            Mutations.SetCell("work", "company", "Acme"),
            Mutations.SetCell("work", "role", "Engineer"),
            Mutations.SetCell("meta", "created", "2024-01-01"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task Filter_single_family()
    {
        var row = await Client.ReadRowAsync(TN, "user1", RowFilters.FamilyNameExact("personal"));
        row!.Families.Should().ContainSingle().Which.Name.Should().Be("personal");
        row.Families.SelectMany(f => f.Columns).Should().HaveCount(2);
    }

    [Fact]
    public async Task Filter_two_families_interleave()
    {
        var filter = RowFilters.Interleave(
            RowFilters.FamilyNameExact("personal"),
            RowFilters.FamilyNameExact("work"));
        var row = await Client.ReadRowAsync(TN, "user1", filter);
        row!.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Filter_family_regex()
    {
        var row = await Client.ReadRowAsync(TN, "user1", RowFilters.FamilyNameRegex("personal|work"));
        row!.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Column_from_specific_family()
    {
        var chain = RowFilters.Chain(
            RowFilters.FamilyNameExact("work"),
            RowFilters.ColumnQualifierExact("role"));
        var row = await Client.ReadRowAsync(TN, "user1", chain);
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("Engineer");
    }

    [Fact]
    public async Task All_families()
    {
        var row = await Client.ReadRowAsync(TN, "user1");
        row!.Families.Should().HaveCount(3);
    }

    [Fact]
    public async Task Family_with_value_filter()
    {
        var chain = RowFilters.Chain(
            RowFilters.FamilyNameExact("personal"),
            RowFilters.ValueExact("Alice"));
        var row = await Client.ReadRowAsync(TN, "user1", chain);
        row!.Families.SelectMany(f => f.Columns).Should().ContainSingle();
    }

    [Fact]
    public async Task Filter_nonexistent_family()
    {
        var row = await Client.ReadRowAsync(TN, "user1", RowFilters.FamilyNameExact("nope"));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Family_filter_preserves_columns()
    {
        var row = await Client.ReadRowAsync(TN, "user1", RowFilters.FamilyNameExact("work"));
        var cols = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("company").And.Contain("role");
    }

    [Fact]
    public async Task Delete_from_one_family()
    {
        await Client.MutateRowAsync(TN, "user1", Mutations.DeleteFromFamily("meta"));
        var row = await Client.ReadRowAsync(TN, "user1");
        row!.Families.Should().HaveCount(2);
        row.Families.Select(f => f.Name).Should().NotContain("meta");
    }

    [Fact]
    public async Task Write_to_new_family()
    {
        await Client.MutateRowAsync(TN, "user1", Mutations.SetCell("meta", "updated", "2024-06-01"));
        var row = await Client.ReadRowAsync(TN, "user1", RowFilters.FamilyNameExact("meta"));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Family_regex_star()
    {
        var row = await Client.ReadRowAsync(TN, "user1", RowFilters.FamilyNameRegex(".*"));
        row!.Families.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task CellsPerRow_across_families()
    {
        var row = await Client.ReadRowAsync(TN, "user1", RowFilters.CellsPerRowLimit(3));
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(3);
    }

    [Fact]
    public async Task StripValue_across_families()
    {
        var row = await Client.ReadRowAsync(TN, "user1", RowFilters.StripValueTransformer());
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .All(c => c.Value.IsEmpty).Should().BeTrue();
    }
}

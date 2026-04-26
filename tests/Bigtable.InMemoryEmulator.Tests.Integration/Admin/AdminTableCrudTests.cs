using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class AdminTableCrudTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;

    public AdminTableCrudTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public ValueTask InitializeAsync() => default;
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Create_table_with_single_family()
    {
        await _fixture.CreateTableAsync("at-crud-1", new[] { "cf" });
        var tn = _fixture.GetTableName("at-crud-1");
        // Verify we can write to it
        await _fixture.Client.MutateRowAsync(tn, "r1", Mutations.SetCell("cf", "c", "v"));
        var row = await _fixture.Client.ReadRowAsync(tn, "r1");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Create_table_with_multiple_families()
    {
        await _fixture.CreateTableAsync("at-crud-2", new[] { "cf1", "cf2", "cf3" });
        var tn = _fixture.GetTableName("at-crud-2");
        await _fixture.Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("cf1", "c", "1"),
            Mutations.SetCell("cf2", "c", "2"),
            Mutations.SetCell("cf3", "c", "3"));
        var row = await _fixture.Client.ReadRowAsync(tn, "r1");
        row!.Families.Should().HaveCount(3);
    }

    [Fact]
    public async Task Write_to_different_tables_isolated()
    {
        await _fixture.CreateTableAsync("at-iso-a", new[] { "cf" });
        await _fixture.CreateTableAsync("at-iso-b", new[] { "cf" });
        var tnA = _fixture.GetTableName("at-iso-a");
        var tnB = _fixture.GetTableName("at-iso-b");
        await _fixture.Client.MutateRowAsync(tnA, "r1", Mutations.SetCell("cf", "c", "a"));
        await _fixture.Client.MutateRowAsync(tnB, "r1", Mutations.SetCell("cf", "c", "b"));
        var rowA = await _fixture.Client.ReadRowAsync(tnA, "r1");
        var rowB = await _fixture.Client.ReadRowAsync(tnB, "r1");
        rowA!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("a");
        rowB!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("b");
    }

    [Fact]
    public async Task Table_with_many_families()
    {
        var families = Enumerable.Range(0, 10).Select(i => $"fam{i}").ToArray();
        await _fixture.CreateTableAsync("at-many-fam", families);
        var tn = _fixture.GetTableName("at-many-fam");
        foreach (var fam in families)
            await _fixture.Client.MutateRowAsync(tn, "r1", Mutations.SetCell(fam, "c", fam));
        var row = await _fixture.Client.ReadRowAsync(tn, "r1");
        row!.Families.Should().HaveCount(10);
    }

    [Fact]
    public async Task Multiple_tables_same_fixture()
    {
        for (int i = 0; i < 5; i++)
        {
            await _fixture.CreateTableAsync($"at-multi-{i}", new[] { "cf" });
            var tn = _fixture.GetTableName($"at-multi-{i}");
            await _fixture.Client.MutateRowAsync(tn, "r1", Mutations.SetCell("cf", "c", $"{i}"));
        }
        for (int i = 0; i < 5; i++)
        {
            var tn = _fixture.GetTableName($"at-multi-{i}");
            var row = await _fixture.Client.ReadRowAsync(tn, "r1");
            row.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Family_filter_on_table_with_many_families()
    {
        await _fixture.CreateTableAsync("at-fam-filt", new[] { "alpha", "beta", "gamma" });
        var tn = _fixture.GetTableName("at-fam-filt");
        await _fixture.Client.MutateRowAsync(tn, "r1",
            Mutations.SetCell("alpha", "c", "a"),
            Mutations.SetCell("beta", "c", "b"),
            Mutations.SetCell("gamma", "c", "g"));
        var row = await _fixture.Client.ReadRowAsync(tn, "r1", RowFilters.FamilyNameExact("beta"));
        row!.Families.Should().ContainSingle().Which.Name.Should().Be("beta");
    }

    [Fact]
    public async Task ReadRows_from_empty_created_table()
    {
        await _fixture.CreateTableAsync("at-empty", new[] { "cf" });
        var tn = _fixture.GetTableName("at-empty");
        var rows = new List<Row>();
        await foreach (var r in _fixture.Client.ReadRows(tn)) rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_table_and_batch_write()
    {
        await _fixture.CreateTableAsync("at-batch", new[] { "cf" });
        var tn = _fixture.GetTableName("at-batch");
        var entries = Enumerable.Range(0, 20)
            .Select(i => Mutations.CreateEntry($"r-{i:D3}", Mutations.SetCell("cf", "c", $"{i}")))
            .ToArray();
        await _fixture.Client.MutateRowsAsync(tn, entries);
        var rows = new List<Row>();
        await foreach (var r in _fixture.Client.ReadRows(tn)) rows.Add(r);
        rows.Should().HaveCount(20);
    }
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ValueExactFilterTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "val-exact";
    private const string CF = "cf";

    public ValueExactFilterTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        await Client.MutateRowAsync(TN, "r1", Mutations.SetCell(CF, "c", "hello"));
        await Client.MutateRowAsync(TN, "r2", Mutations.SetCell(CF, "c", "world"));
        await Client.MutateRowAsync(TN, "r3", Mutations.SetCell(CF, "c", "hello"));
        await Client.MutateRowAsync(TN, "r4", Mutations.SetCell(CF, "c", ""));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Exact_match()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueExact("hello")))
            rows.Add(r);
        rows.Should().HaveCount(2); // r1 and r3
    }

    [Fact]
    public async Task No_match()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueExact("missing")))
            rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Case_sensitive()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueExact("Hello")))
            rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Empty_value_match()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueExact("")))
            rows.Add(r);
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("r4");
    }

    [Fact]
    public async Task Single_row_exact_match()
    {
        var row = await Client.ReadRowAsync(TN, "r2", RowFilters.ValueExact("world"));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Single_row_no_match()
    {
        var row = await Client.ReadRowAsync(TN, "r2", RowFilters.ValueExact("hello"));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Value_exact_with_multiple_columns()
    {
        await Client.MutateRowAsync(TN, "r5",
            Mutations.SetCell(CF, "a", "target"),
            Mutations.SetCell(CF, "b", "other"));
        var row = await Client.ReadRowAsync(TN, "r5", RowFilters.ValueExact("target"));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).Should().ContainSingle();
    }

    [Fact]
    public async Task Value_regex_dot_plus()
    {
        // ".+" matches one or more chars — excludes empty value
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueRegex(".+")))
            rows.Add(r);
        rows.Should().HaveCount(3); // r1, r2, r3
    }

    [Fact]
    public async Task Value_regex_word()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueRegex("wor.d")))
            rows.Add(r);
        rows.Should().ContainSingle();
    }
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ValueRegexBehaviorTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "valreg-beh";
    private const string CF = "cf";

    public ValueRegexBehaviorTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        await Client.MutateRowAsync(TN, "r1", Mutations.SetCell(CF, "col", "hello-world"));
        await Client.MutateRowAsync(TN, "r2", Mutations.SetCell(CF, "col", "hello-earth"));
        await Client.MutateRowAsync(TN, "r3", Mutations.SetCell(CF, "col", "goodbye-world"));
        await Client.MutateRowAsync(TN, "r4", Mutations.SetCell(CF, "col", "12345"));
        await Client.MutateRowAsync(TN, "r5", Mutations.SetCell(CF, "col", ""));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task ValueRegex_prefix()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueRegex("hello-.*")))
            rows.Add(r);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task ValueRegex_suffix()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueRegex(".*-world")))
            rows.Add(r);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task ValueRegex_exact()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueRegex("12345")))
            rows.Add(r);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task ValueRegex_alternation()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueRegex("hello-world|12345")))
            rows.Add(r);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task ValueRegex_no_match()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueRegex("nomatch")))
            rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ValueRegex_empty_string()
    {
        var rows = new List<Row>();
        // Empty value matches empty regex pattern
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueRegex("")))
            rows.Add(r);
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("r5");
    }

    [Fact]
    public async Task ValueRegex_dot_star_matches_all()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueRegex(".*")))
            rows.Add(r);
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task ValueRegex_digit_class()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueRegex("[0-9]+")))
            rows.Add(r);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task ValueRegex_combined_with_row_key_filter()
    {
        var chain = RowFilters.Chain(
            RowFilters.RowKeyRegex("r[12]"),
            RowFilters.ValueRegex("hello-.*"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: chain))
            rows.Add(r);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task ValueRegex_with_limit()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueRegex(".*"), rowsLimit: 2))
            rows.Add(r);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task ValueExact_matches_single()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueExact("goodbye-world")))
            rows.Add(r);
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("r3");
    }

    [Fact]
    public async Task ValueRange_open_range()
    {
        var range = ValueRange.Open("12345", "hello-world");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueRange(range)))
            rows.Add(r);
        // Values between "12345" and "hello-world" exclusive: "goodbye-world", "hello-earth"
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task ValueRange_closed_includes_endpoints()
    {
        var range = ValueRange.Closed("12345", "12345");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueRange(range)))
            rows.Add(r);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task ValueRegex_case_sensitive()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueRegex("HELLO-.*")))
            rows.Add(r);
        rows.Should().BeEmpty(); // Case sensitive
    }

    [Fact]
    public async Task ValueRegex_with_interleave()
    {
        var interleave = RowFilters.Interleave(
            RowFilters.ValueRegex("hello-.*"),
            RowFilters.ValueRegex("goodbye-.*"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: interleave))
            rows.Add(r);
        rows.Should().HaveCount(3);
    }
}

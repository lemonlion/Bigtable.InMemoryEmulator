using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class RowKeyExactFilterTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "rk-exact";
    private const string CF = "cf";

    public RowKeyExactFilterTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        foreach (var key in new[] { "alpha", "beta", "gamma", "delta" })
            await Client.MutateRowAsync(TN, key, Mutations.SetCell(CF, "c", key));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Exact_key_filter()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyExact("beta")))
            rows.Add(r);
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("beta");
    }

    [Fact]
    public async Task Exact_key_no_match()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyExact("omega")))
            rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Exact_key_case_sensitive()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyExact("Beta")))
            rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Row_key_regex_prefix()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex(".*a")))
            rows.Add(r);
        // Full match: "alpha", "beta", "gamma", "delta" all end in 'a'
        rows.Should().HaveCount(4);
    }

    [Fact]
    public async Task Row_key_regex_alternation()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex("alpha|gamma")))
            rows.Add(r);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Row_key_regex_dot_star()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex(".*")))
            rows.Add(r);
        rows.Should().HaveCount(4);
    }

    [Fact]
    public async Task Row_key_regex_exact_match_semantics()
    {
        // "eta" should NOT match "beta" because regex is full-match anchored
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex("eta")))
            rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Row_key_regex_partial_with_wildcard()
    {
        // ".*eta" should match "beta" and "delta"  
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex(".*eta")))
            rows.Add(r);
        rows.Should().HaveCount(1); // only "beta" ends exactly in "eta". "delta" does not.
    }

    [Fact]
    public async Task Chain_key_filter_and_value_filter()
    {
        var filter = RowFilters.Chain(
            RowFilters.RowKeyExact("gamma"),
            RowFilters.ValueExact("gamma"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: filter))
            rows.Add(r);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Interleave_key_filters()
    {
        var filter = RowFilters.Interleave(
            RowFilters.RowKeyExact("alpha"),
            RowFilters.RowKeyExact("delta"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: filter))
            rows.Add(r);
        rows.Should().HaveCount(2);
    }
}

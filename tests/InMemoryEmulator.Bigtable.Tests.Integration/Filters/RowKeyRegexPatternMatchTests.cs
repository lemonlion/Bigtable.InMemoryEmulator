using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class RowKeyRegexPatternMatchTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "rkregex-pat";
    private const string CF = "cf";

    public RowKeyRegexPatternMatchTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        foreach (var key in new[] { "user-001", "user-002", "user-100", "order-001", "order-002", "admin-001" })
            await Client.MutateRowAsync(TN, key, Mutations.SetCell(CF, "v", "1"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Regex_prefix_match()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex("user-.*")))
            rows.Add(r);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Regex_suffix_match()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex(".*-001")))
            rows.Add(r);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Regex_alternation()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex("user-001|order-001")))
            rows.Add(r);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Regex_character_class()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex(".*-00[12]")))
            rows.Add(r);
        rows.Should().HaveCount(5); // user-001, user-002, order-001, order-002, admin-001
    }

    [Fact]
    public async Task Regex_no_match_returns_empty()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex("product-.*")))
            rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Regex_exact_match()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex("admin-001")))
            rows.Add(r);
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("admin-001");
    }

    [Fact]
    public async Task Regex_dot_matches_any()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex("user-.0.")))
            rows.Add(r);
        // user-001, user-002, user-100 match user-.0. (dot matches any char)
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Regex_digit_class()
    {
        var rows = new List<Row>();
        // Matches rows ending with exactly 3 digits
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex(".*-[0-9][0-9][0-9]")))
            rows.Add(r);
        rows.Should().HaveCount(6);
    }

    [Fact]
    public async Task Regex_negated_class()
    {
        var rows = new List<Row>();
        // Rows not starting with 'u'
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex("[^u].*")))
            rows.Add(r);
        rows.Should().HaveCount(3); // order-001, order-002, admin-001
    }

    [Fact]
    public async Task Regex_quantifier_plus()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex("user-0+")))
            rows.Add(r);
        // user-001 has 0s but also 1, user-002 has 0s but also 2 -> none match "user-0+" exactly
        // because regex is full-match: user-0+ = "user-" + one-or-more 0s
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Regex_combined_with_row_range()
    {
        var rowSet = new RowSet { RowRanges = { new RowRange { StartKeyClosed = ByteString.CopyFromUtf8("order"), EndKeyOpen = ByteString.CopyFromUtf8("p") } } };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet, filter: RowFilters.RowKeyRegex("order-001")))
            rows.Add(r);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Regex_with_cells_per_row_limit()
    {
        await Client.MutateRowAsync(TN, "user-001",
            Mutations.SetCell(CF, "extra1", "e1"),
            Mutations.SetCell(CF, "extra2", "e2"));
        var rows = new List<Row>();
        var chain = RowFilters.Chain(RowFilters.RowKeyRegex("user-001"), RowFilters.CellsPerRowLimit(2));
        await foreach (var r in Client.ReadRows(TN, filter: chain))
            rows.Add(r);
        rows.Should().ContainSingle();
        rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(2);
    }

    [Fact]
    public async Task Regex_pipe_in_pattern()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex("admin-001|user-100")))
            rows.Add(r);
        rows.Should().HaveCount(2);
        rows.Select(r => r.Key.ToStringUtf8()).Should().Contain("admin-001").And.Contain("user-100");
    }

    [Fact]
    public async Task Regex_empty_table_returns_empty()
    {
        await _fixture.CreateTableAsync("rkregex-empty", new[] { CF });
        var tn2 = _fixture.GetTableName("rkregex-empty");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(tn2, filter: RowFilters.RowKeyRegex(".*")))
            rows.Add(r);
        rows.Should().BeEmpty();
    }
}

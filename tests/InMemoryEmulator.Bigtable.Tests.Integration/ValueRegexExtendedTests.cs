using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for value regex filter patterns — extended set.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ValueRegexExtendedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "vrex-tests";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public ValueRegexExtendedTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });

        await Client.MutateRowAsync(TN, "vrex-1",
            Mutations.SetCell(CF, "msg", "Hello World", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "vrex-2",
            Mutations.SetCell(CF, "msg", "hello world", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "vrex-3",
            Mutations.SetCell(CF, "msg", "HELLO WORLD", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "vrex-4",
            Mutations.SetCell(CF, "num", "12345", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "vrex-5",
            Mutations.SetCell(CF, "num", "67890", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "vrex-6",
            Mutations.SetCell(CF, "email", "user@test.com", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "vrex-7",
            Mutations.SetCell(CF, "email", "admin@test.org", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "vrex-8",
            Mutations.SetCell(CF, "status", "active", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "vrex-9",
            Mutations.SetCell(CF, "status", "inactive", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "vrex-10",
            Mutations.SetCell(CF, "status", "pending", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "vrex-11",
            Mutations.SetCell(CF, "json", "{\"key\":\"value\"}", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "vrex-12",
            Mutations.SetCell(CF, "empty", "", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task ValueExact_matches_exact()
    {
        var keys = await GetMatchingKeys(RowFilters.ValueExact("Hello World"));
        keys.Should().ContainSingle("vrex-1");
    }

    [Fact]
    public async Task ValueExact_is_case_sensitive()
    {
        var keys = await GetMatchingKeys(RowFilters.ValueExact("hello world"));
        keys.Should().ContainSingle("vrex-2");
    }

    [Fact]
    public async Task ValueRegex_prefix()
    {
        var keys = await GetMatchingKeys(RowFilters.ValueRegex("Hello.*"));
        keys.Should().ContainSingle("vrex-1");
    }

    [Fact]
    public async Task ValueRegex_suffix()
    {
        var keys = await GetMatchingKeys(RowFilters.ValueRegex(".*World"));
        keys.Should().ContainSingle("vrex-1");
    }

    [Fact]
    public async Task ValueRegex_case_insensitive()
    {
        var keys = await GetMatchingKeys(RowFilters.ValueRegex("(?i)hello world"));
        keys.Should().HaveCount(3);
    }

    [Fact]
    public async Task ValueRegex_digit_only()
    {
        var keys = await GetMatchingKeys(RowFilters.ValueRegex("[0-9]+"));
        keys.Should().HaveCount(2);
    }

    [Fact]
    public async Task ValueRegex_email_domain()
    {
        var keys = await GetMatchingKeys(RowFilters.ValueRegex(".*@test\\.com"));
        keys.Should().ContainSingle("vrex-6");
    }

    [Fact]
    public async Task ValueRegex_alternation()
    {
        var keys = await GetMatchingKeys(RowFilters.ValueRegex("(active|pending)"));
        keys.Should().HaveCount(2);
    }

    [Fact]
    public async Task ValueRegex_dot_plus_nonempty()
    {
        var keys = await GetMatchingKeys(RowFilters.ValueRegex(".+"));
        keys.Should().HaveCount(11);
    }

    [Fact]
    public async Task ValueExact_empty_string()
    {
        var keys = await GetMatchingKeys(RowFilters.ValueExact(""));
        keys.Should().ContainSingle("vrex-12");
    }

    [Fact]
    public async Task ValueRegex_no_match()
    {
        var keys = await GetMatchingKeys(RowFilters.ValueRegex("NOMATCH"));
        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task ValueExact_with_column_filter()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.ValueExact("inactive"))
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());
        keys.Should().ContainSingle("vrex-9");
    }

    [Fact]
    public async Task ValueRegex_with_limit()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.ValueRegex(".+"),
            RowsLimit = 3
        };
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(3);
    }

    [Fact]
    public async Task ValueRange_closed()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.ValueRange(ValueRange.Closed("active", "inactive")))
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());
        keys.Should().HaveCount(2);
    }

    [Fact]
    public async Task ValueRange_open()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.ValueRange(ValueRange.Open("active", "pending")))
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());
        keys.Should().ContainSingle("vrex-9");
    }

    [Fact]
    public async Task Value_interleave()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Interleave(
                RowFilters.ValueExact("active"),
                RowFilters.ValueExact("pending"))
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());
        keys.Should().HaveCount(2);
    }

    [Fact]
    public async Task ValueRegex_substring_match()
    {
        // RE2 uses substring matching by default
        var keys = await GetMatchingKeys(RowFilters.ValueRegex(".*ello.*"));
        keys.Should().HaveCount(2); // "Hello World" and "hello world" contain "ello"
    }

    private async Task<List<string>> GetMatchingKeys(RowFilter filter)
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = filter
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());
        return keys;
    }
}

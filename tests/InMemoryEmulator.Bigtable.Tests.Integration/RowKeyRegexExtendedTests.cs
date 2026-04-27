using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for row key regex filter patterns — extended set.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class RowKeyRegexExtendedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "rkre-tests";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public RowKeyRegexExtendedTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });

        var keys = new[]
        {
            "user#001", "user#002", "user#003",
            "order#100", "order#200", "order#300",
            "log#2024-01-01", "log#2024-02-01", "log#2024-03-01",
            "admin#root", "admin#backup",
            "data", "metadata"
        };
        foreach (var key in keys)
            await Client.MutateRowAsync(TN, key,
                Mutations.SetCell(CF, "c", $"val-{key}", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Exact_match()
    {
        var keys = await GetMatchingKeys(RowFilters.RowKeyExact("data"));
        keys.Should().ContainSingle("data");
    }

    [Fact]
    public async Task Prefix_match()
    {
        var keys = await GetMatchingKeys(RowFilters.RowKeyRegex("user#.*"));
        keys.Should().HaveCount(3);
    }

    [Fact]
    public async Task Suffix_match()
    {
        var keys = await GetMatchingKeys(RowFilters.RowKeyRegex(".*data"));
        keys.Should().HaveCount(2);
        keys.Should().Contain("data");
        keys.Should().Contain("metadata");
    }

    [Fact]
    public async Task Dot_star_matches_all()
    {
        var keys = await GetMatchingKeys(RowFilters.RowKeyRegex(".*"));
        keys.Should().HaveCount(13);
    }

    [Fact]
    public async Task Alternation()
    {
        var keys = await GetMatchingKeys(RowFilters.RowKeyRegex("(user|order)#.*"));
        keys.Should().HaveCount(6);
    }

    [Fact]
    public async Task Character_class()
    {
        var keys = await GetMatchingKeys(RowFilters.RowKeyRegex("order#[12]00"));
        keys.Should().HaveCount(2);
    }

    [Fact]
    public async Task Digit_pattern()
    {
        var keys = await GetMatchingKeys(RowFilters.RowKeyRegex("user#[0-9]{3}"));
        keys.Should().HaveCount(3);
    }

    [Fact]
    public async Task No_match_returns_empty()
    {
        var keys = await GetMatchingKeys(RowFilters.RowKeyRegex("nonexistent#.*"));
        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task Hash_separator()
    {
        var keys = await GetMatchingKeys(RowFilters.RowKeyRegex(".*#.*"));
        keys.Should().HaveCount(11);
    }

    [Fact]
    public async Task Regex_with_limit()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.RowKeyRegex("user#.*"),
            RowsLimit = 2
        };
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(2);
    }

    [Fact]
    public async Task Combined_with_value_filter()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.RowKeyRegex("order#.*"),
                RowFilters.ValueExact("val-order#200"))
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());
        keys.Should().ContainSingle("order#200");
    }

    [Fact]
    public async Task As_condition_predicate()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Condition(
                RowFilters.RowKeyRegex("user#.*"),
                new RowFilter { ApplyLabelTransformer = "is-user" },
                new RowFilter { ApplyLabelTransformer = "not-user" }),
            Rows = new RowSet
            {
                RowKeys =
                {
                    ByteString.CopyFromUtf8("user#001"),
                    ByteString.CopyFromUtf8("order#100")
                }
            }
        };
        var labelMap = new Dictionary<string, string>();
        await foreach (var row in Client.ReadRows(request))
        {
            var label = row.Families.SelectMany(f => f.Columns)
                .SelectMany(c => c.Cells).SelectMany(c => c.Labels).First();
            labelMap[row.Key.ToStringUtf8()] = label;
        }
        labelMap["user#001"].Should().Be("is-user");
        labelMap["order#100"].Should().Be("not-user");
    }

    [Fact]
    public async Task Results_are_sorted()
    {
        var keys = await GetMatchingKeys(RowFilters.RowKeyRegex("order#.*"));
        keys.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Exact_nonexistent()
    {
        var keys = await GetMatchingKeys(RowFilters.RowKeyExact("definitely-not-here"));
        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task Regex_with_quantifier()
    {
        var keys = await GetMatchingKeys(RowFilters.RowKeyRegex("log#2024-0[1-3]-01"));
        keys.Should().HaveCount(3);
    }

    [Fact]
    public async Task Negated_character_class()
    {
        // Keys NOT starting with digits
        var keys = await GetMatchingKeys(RowFilters.RowKeyRegex("[^0-9].*"));
        keys.Should().HaveCount(13); // All keys start with letters
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

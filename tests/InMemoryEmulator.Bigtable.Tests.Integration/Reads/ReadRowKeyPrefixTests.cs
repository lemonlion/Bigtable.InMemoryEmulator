using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowKeyPrefixTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "rk-pfx";
    private const string CF = "cf";

    public ReadRowKeyPrefixTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        foreach (var key in new[] { "ab", "abc", "abd", "b", "ba", "bcd" })
            await Client.MutateRowAsync(TN, key, Mutations.SetCell(CF, "c", key));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Prefix_a()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex("a.*")))
            rows.Add(r);
        rows.Should().HaveCount(3); // ab, abc, abd
    }

    [Fact]
    public async Task Prefix_ab()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex("ab.*")))
            rows.Add(r);
        rows.Should().HaveCount(3); // ab, abc, abd
    }

    [Fact]
    public async Task Prefix_abc()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex("abc.*")))
            rows.Add(r);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Prefix_b()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex("b.*")))
            rows.Add(r);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task No_prefix_match()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex("z.*")))
            rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Range_based_prefix_scan()
    {
        // ClosedOpen("ab", "ac") simulates prefix "ab"
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, RowSet.FromRowRanges(RowRange.ClosedOpen("ab", "ac"))))
            rows.Add(r);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Prefix_with_limit()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex(".*"), rowsLimit: 2))
            rows.Add(r);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Prefix_with_column_filter()
    {
        var filter = RowFilters.Chain(
            RowFilters.RowKeyRegex("b.*"),
            RowFilters.ColumnQualifierExact("c"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: filter)) rows.Add(r);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Single_char_key()
    {
        var row = await Client.ReadRowAsync(TN, "b");
        row.Should().NotBeNull();
        row!.Key.ToStringUtf8().Should().Be("b");
    }

    [Fact]
    public async Task Regex_alternation_two_prefixes()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.RowKeyRegex("ab.*|ba.*")))
            rows.Add(r);
        rows.Should().HaveCount(4); // ab, abc, abd, ba
    }
}

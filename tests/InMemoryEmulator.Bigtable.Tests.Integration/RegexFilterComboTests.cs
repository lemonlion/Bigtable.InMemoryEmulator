using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for regex-based filters — RowKeyRegex, FamilyNameRegex, ColumnQualifierRegex, ValueRegex.
/// Verifies RE2-style regex matching behavior.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class RegexFilterComboTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "regex-combo";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public RegexFilterComboTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF, "family2" });

        // Seed data
        await Client.MutateRowAsync(TN, "user#001",
            Mutations.SetCell(CF, "name", "Alice", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "email", "alice@example.com", new BigtableVersion(1000)),
            Mutations.SetCell("family2", "score", "100", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "user#002",
            Mutations.SetCell(CF, "name", "Bob", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "email", "bob@test.org", new BigtableVersion(1000)),
            Mutations.SetCell("family2", "score", "200", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "user#003",
            Mutations.SetCell(CF, "name", "Charlie", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "email", "charlie@example.com", new BigtableVersion(1000)),
            Mutations.SetCell("family2", "score", "150", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "admin#001",
            Mutations.SetCell(CF, "name", "Admin", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "email", "admin@example.com", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task RowKeyRegex_exact_prefix()
    {
        var keys = await ReadKeysWithFilter(RowFilters.RowKeyRegex("user#.*"));
        keys.Should().BeEquivalentTo("user#001", "user#002", "user#003");
    }

    [Fact]
    public async Task RowKeyRegex_specific_suffix()
    {
        var keys = await ReadKeysWithFilter(RowFilters.RowKeyRegex(".*#001"));
        keys.Should().BeEquivalentTo("user#001", "admin#001");
    }

    [Fact]
    public async Task RowKeyRegex_alternation()
    {
        var keys = await ReadKeysWithFilter(RowFilters.RowKeyRegex("user#001|user#003"));
        keys.Should().BeEquivalentTo("user#001", "user#003");
    }

    [Fact]
    public async Task RowKeyRegex_no_match()
    {
        var keys = await ReadKeysWithFilter(RowFilters.RowKeyRegex("zzz.*"));
        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task FamilyNameRegex_exact()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.FamilyNameRegex("family2"),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("user#001") } }
        };
        var families = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
                families.Add(f.Name);

        families.Should().ContainSingle("family2");
    }

    [Fact]
    public async Task FamilyNameRegex_pattern()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.FamilyNameRegex("cf|family2"),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("user#001") } }
        };
        var families = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
                families.Add(f.Name);

        families.Should().HaveCount(2);
        families.Should().Contain("cf");
        families.Should().Contain("family2");
    }

    [Fact]
    public async Task ColumnQualifierRegex_pattern()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.FamilyNameExact(CF),
                RowFilters.ColumnQualifierRegex("na.*")),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("user#001") } }
        };
        var cols = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
                cols.Add(c.Qualifier.ToStringUtf8());

        cols.Should().ContainSingle("name");
    }

    [Fact]
    public async Task ColumnQualifierRegex_alternation()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.FamilyNameExact(CF),
                RowFilters.ColumnQualifierRegex("name|email")),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("user#001") } }
        };
        var cols = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
                cols.Add(c.Qualifier.ToStringUtf8());

        cols.Should().HaveCount(2);
        cols.Should().Contain("name");
        cols.Should().Contain("email");
    }

    [Fact]
    public async Task ValueRegex_matches_substring()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.FamilyNameExact(CF),
                RowFilters.ColumnQualifierExact("email"),
                RowFilters.ValueRegex(".*@example\\.com")),
        };
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                vals.Add(cell.Value.ToStringUtf8());

        vals.Should().HaveCount(3);
        vals.Should().Contain("alice@example.com");
        vals.Should().Contain("charlie@example.com");
        vals.Should().Contain("admin@example.com");
    }

    [Fact]
    public async Task ValueRegex_no_match()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.ValueRegex("nonexistent_pattern_xyz"),
        };
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(0);
    }

    [Fact]
    public async Task Chain_RowKeyRegex_and_ColumnQualifier()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.RowKeyRegex("user#.*"),
                RowFilters.ColumnQualifierExact("name")),
        };
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                vals.Add(cell.Value.ToStringUtf8());

        vals.Should().HaveCount(3);
        vals.Should().Contain("Alice");
        vals.Should().Contain("Bob");
        vals.Should().Contain("Charlie");
    }

    [Fact]
    public async Task Chain_FamilyRegex_ColumnRegex_ValueRegex()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.FamilyNameRegex("cf"),
                RowFilters.ColumnQualifierRegex("email"),
                RowFilters.ValueRegex(".*@test\\.org")),
        };
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                vals.Add(cell.Value.ToStringUtf8());

        vals.Should().ContainSingle("bob@test.org");
    }

    [Fact]
    public async Task RowKeyRegex_dot_star_matches_all()
    {
        var keys = await ReadKeysWithFilter(RowFilters.RowKeyRegex(".*"));
        keys.Should().HaveCount(4);
    }

    [Fact]
    public async Task RowKeyRegex_character_class()
    {
        var keys = await ReadKeysWithFilter(RowFilters.RowKeyRegex("user#00[13]"));
        keys.Should().BeEquivalentTo("user#001", "user#003");
    }

    [Fact]
    public async Task ColumnQualifierRegex_dot_matches_any_char()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.FamilyNameExact(CF),
                RowFilters.ColumnQualifierRegex("na.e")),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("user#001") } }
        };
        var cols = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
                cols.Add(c.Qualifier.ToStringUtf8());

        cols.Should().ContainSingle("name");
    }

    [Fact]
    public async Task ValueExact_filter()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.FamilyNameExact(CF),
                RowFilters.ColumnQualifierExact("name"),
                RowFilters.ValueExact("Bob")),
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());

        keys.Should().ContainSingle("user#002");
    }

    [Fact]
    public async Task Interleave_two_regex_filters()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Interleave(
                RowFilters.Chain(
                    RowFilters.FamilyNameExact(CF),
                    RowFilters.ColumnQualifierExact("name")),
                RowFilters.Chain(
                    RowFilters.FamilyNameExact("family2"),
                    RowFilters.ColumnQualifierExact("score"))),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("user#001") } }
        };
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                vals.Add(cell.Value.ToStringUtf8());

        vals.Should().HaveCount(2);
        vals.Should().Contain("Alice");
        vals.Should().Contain("100");
    }

    [Fact]
    public async Task RowKeyRegex_with_limit()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.RowKeyRegex("user#.*"),
            RowsLimit = 2
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());

        keys.Should().HaveCount(2);
        keys.Should().BeEquivalentTo("user#001", "user#002");
    }

    private async Task<List<string>> ReadKeysWithFilter(RowFilter filter)
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

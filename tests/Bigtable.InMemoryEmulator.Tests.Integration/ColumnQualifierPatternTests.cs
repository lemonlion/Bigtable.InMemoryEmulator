using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for column qualifier patterns: binary qualifiers, special characters,
/// long qualifiers, regex matching, and edge cases.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#column
///   "qualifier: Qualifier of the column family's column. Can be any byte string."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ColumnQualifierPatternTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "colq-pat";

    public ColumnQualifierPatternTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    #region Special character qualifiers

    [Fact]
    public async Task Qualifier_with_dots()
    {
        await Client.MutateRowAsync(TN, "cq-dot",
            Mutations.SetCell(CF, "a.b.c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "cq-dot");
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("a.b.c");
    }

    [Fact]
    public async Task Qualifier_with_slashes()
    {
        await Client.MutateRowAsync(TN, "cq-slash",
            Mutations.SetCell(CF, "path/to/col", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "cq-slash");
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("path/to/col");
    }

    [Fact]
    public async Task Qualifier_with_colon()
    {
        await Client.MutateRowAsync(TN, "cq-colon",
            Mutations.SetCell(CF, "key:value", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "cq-colon");
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("key:value");
    }

    [Fact]
    public async Task Qualifier_with_hash()
    {
        await Client.MutateRowAsync(TN, "cq-hash",
            Mutations.SetCell(CF, "field#1", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "cq-hash");
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("field#1");
    }

    [Fact]
    public async Task Qualifier_with_spaces()
    {
        await Client.MutateRowAsync(TN, "cq-space",
            Mutations.SetCell(CF, "my column", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "cq-space");
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("my column");
    }

    #endregion

    #region Empty and binary qualifiers

    [Fact]
    public async Task Empty_qualifier()
    {
        await Client.MutateRowAsync(TN, "cq-empty",
            Mutations.SetCell(CF, "", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "cq-empty");
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().BeEmpty();
    }

    [Fact]
    public async Task Binary_qualifier()
    {
        var binQual = ByteString.CopyFrom(new byte[] { 0x00, 0xFF, 0x01 });
        await Client.MutateRowAsync(TN, "cq-bin",
            Mutations.SetCell(CF, binQual, ByteString.CopyFromUtf8("v"), new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "cq-bin");
        row!.Families[0].Columns[0].Qualifier.ToByteArray().Should().BeEquivalentTo(binQual.ToByteArray());
    }

    [Fact]
    public async Task Single_byte_qualifier()
    {
        var qual = ByteString.CopyFrom(new byte[] { 0x42 });
        await Client.MutateRowAsync(TN, "cq-1byte",
            Mutations.SetCell(CF, qual, ByteString.CopyFromUtf8("v"), new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "cq-1byte");
        row!.Families[0].Columns[0].Qualifier.ToByteArray().Should().BeEquivalentTo(qual.ToByteArray());
    }

    #endregion

    #region Unicode qualifiers

    [Fact]
    public async Task Unicode_qualifier()
    {
        await Client.MutateRowAsync(TN, "cq-unicode",
            Mutations.SetCell(CF, "日本語", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "cq-unicode");
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("日本語");
    }

    [Fact]
    public async Task Emoji_qualifier()
    {
        await Client.MutateRowAsync(TN, "cq-emoji",
            Mutations.SetCell(CF, "🎯", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "cq-emoji");
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("🎯");
    }

    #endregion

    #region Long qualifiers

    [Fact]
    public async Task Qualifier_256_bytes()
    {
        var qual = new string('a', 256);
        await Client.MutateRowAsync(TN, "cq-256",
            Mutations.SetCell(CF, qual, "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "cq-256");
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().HaveLength(256);
    }

    [Fact]
    public async Task Qualifier_1kb()
    {
        var qual = new string('b', 1024);
        await Client.MutateRowAsync(TN, "cq-1k",
            Mutations.SetCell(CF, qual, "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "cq-1k");
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().HaveLength(1024);
    }

    #endregion

    #region Qualifier regex filter

    [Fact]
    public async Task QualifierRegex_exact_match()
    {
        await Client.MutateRowAsync(TN, "cq-rx1",
            Mutations.SetCell(CF, "target", "yes", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "other", "no", new BigtableVersion(1000)));
        var cells = new List<string>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("cq-rx1"),
            filter: RowFilters.ColumnQualifierExact("target")))
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    cells.Add(col.Qualifier.ToStringUtf8());
        cells.Should().ContainSingle().Which.Should().Be("target");
    }

    [Fact]
    public async Task QualifierRegex_pattern()
    {
        await Client.MutateRowAsync(TN, "cq-rx2",
            Mutations.SetCell(CF, "col_a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col_b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "other", "3", new BigtableVersion(1000)));
        var cols = new List<string>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("cq-rx2"),
            filter: RowFilters.ColumnQualifierRegex("col_.*")))
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    cols.Add(col.Qualifier.ToStringUtf8());
        cols.Should().HaveCount(2);
        cols.Should().Contain("col_a").And.Contain("col_b");
    }

    [Fact]
    public async Task QualifierRegex_no_match()
    {
        await Client.MutateRowAsync(TN, "cq-rx3",
            Mutations.SetCell(CF, "abc", "v", new BigtableVersion(1000)));
        var found = false;
        await foreach (var _ in Client.ReadRows(TN, RowSet.FromRowKeys("cq-rx3"),
            filter: RowFilters.ColumnQualifierRegex("xyz")))
            found = true;
        found.Should().BeFalse();
    }

    [Fact]
    public async Task QualifierExact_returns_all_versions()
    {
        await Client.MutateRowAsync(TN, "cq-rx4",
            Mutations.SetCell(CF, "multi", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "multi", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "multi", "v3", new BigtableVersion(3000)));
        var cellCount = 0;
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("cq-rx4"),
            filter: RowFilters.ColumnQualifierExact("multi")))
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    cellCount += col.Cells.Count;
        cellCount.Should().Be(3);
    }

    #endregion

    #region Multiple columns same row

    [Fact]
    public async Task Row_with_many_columns()
    {
        var mutations = Enumerable.Range(0, 50)
            .Select(i => Mutations.SetCell(CF, $"col{i:D3}", $"v{i}", new BigtableVersion(1000)))
            .ToArray();
        await Client.MutateRowAsync(TN, "cq-many", mutations);
        var row = await Client.ReadRowAsync(TN, "cq-many");
        row!.Families[0].Columns.Should().HaveCount(50);
    }

    [Fact]
    public async Task Columns_sorted_by_qualifier()
    {
        await Client.MutateRowAsync(TN, "cq-sorted",
            Mutations.SetCell(CF, "z", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "m", "3", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "cq-sorted");
        var quals = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().BeInAscendingOrder();
    }

    #endregion
}

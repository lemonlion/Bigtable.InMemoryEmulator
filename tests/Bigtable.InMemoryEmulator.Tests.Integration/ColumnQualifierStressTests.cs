using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for column qualifier patterns — binary qualifiers, empty qualifiers,
/// multi-column reads, ordering.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#column
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ColumnQualifierStressTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "qualifier-stress";
    private const string CF = "cf";

    public ColumnQualifierStressTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null)
    {
        var list = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: rows, filter: filter))
            list.Add(row);
        return list;
    }

    #region Empty qualifier

    [Fact]
    public async Task Empty_qualifier_is_valid()
    {
        // Ref: Empty byte string is a valid qualifier
        await Client.MutateRowAsync(TN, "eq-1",
            Mutations.SetCell(CF, ByteString.Empty, ByteString.CopyFromUtf8("v"), new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("eq-1"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Qualifier.Length.Should().Be(0);
    }

    [Fact]
    public async Task Empty_qualifier_coexists_with_named_qualifier()
    {
        await Client.MutateRowAsync(TN, "eq-2",
            Mutations.SetCell(CF, ByteString.Empty, ByteString.CopyFromUtf8("empty"), new BigtableVersion(1000)),
            Mutations.SetCell(CF, "named", "named-v", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("eq-2"));
        rows[0].Families[0].Columns.Should().HaveCount(2);
    }

    #endregion

    #region Binary qualifiers

    [Fact]
    public async Task Binary_qualifier_roundtrip()
    {
        var qual = ByteString.CopyFrom(0x00, 0xFF, 0x01);
        await Client.MutateRowAsync(TN, "bq-1",
            Mutations.SetCell(CF, qual, ByteString.CopyFromUtf8("v"), new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("bq-1"));
        rows[0].Families[0].Columns[0].Qualifier.ToByteArray().Should().BeEquivalentTo(qual.ToByteArray());
    }

    [Fact]
    public async Task Binary_qualifier_with_null_bytes()
    {
        var qual = ByteString.CopyFrom(0x00, 0x00, 0x00);
        await Client.MutateRowAsync(TN, "bq-null",
            Mutations.SetCell(CF, qual, ByteString.CopyFromUtf8("v"), new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("bq-null"));
        rows[0].Families[0].Columns[0].Qualifier.ToByteArray().Should().Equal(0x00, 0x00, 0x00);
    }

    #endregion

    #region Qualifier ordering

    [Fact]
    public async Task Qualifiers_sorted_lexicographically()
    {
        await Client.MutateRowAsync(TN, "qo-1",
            Mutations.SetCell(CF, "z", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "m", "3", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("qo-1"));
        var quals = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Numeric_qualifiers_sorted_lexicographically()
    {
        var mutations = new[] { "1", "10", "2", "20", "100" }.Select(q =>
            Mutations.SetCell(CF, q, "v", new BigtableVersion(1000))
        ).ToArray();
        await Client.MutateRowAsync(TN, "qo-num", mutations);
        var rows = await ReadAll(RowSet.FromRowKeys("qo-num"));
        var quals = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().Equal("1", "10", "100", "2", "20");
    }

    [Fact]
    public async Task Many_qualifiers_all_sorted()
    {
        var mutations = Enumerable.Range(0, 26).Select(i =>
            Mutations.SetCell(CF, $"{(char)('z' - i)}", "v", new BigtableVersion(1000))
        ).ToArray();
        await Client.MutateRowAsync(TN, "qo-many", mutations);
        var rows = await ReadAll(RowSet.FromRowKeys("qo-many"));
        var quals = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().BeInAscendingOrder();
    }

    #endregion

    #region ColumnQualifierExact filter

    [Fact]
    public async Task ColumnQualifierExact_single_match()
    {
        await Client.MutateRowAsync(TN, "cqe-1",
            Mutations.SetCell(CF, "target", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "other", "v", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("cqe-1"), RowFilters.ColumnQualifierExact("target"));
        rows[0].Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("target");
    }

    [Fact]
    public async Task ColumnQualifierExact_no_match()
    {
        await Client.MutateRowAsync(TN, "cqe-nm",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("cqe-nm"), RowFilters.ColumnQualifierExact("zzz"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ColumnQualifierExact_across_multiple_rows()
    {
        for (int i = 0; i < 5; i++)
            await Client.MutateRowAsync(TN, $"cqe-mr-{i}",
                Mutations.SetCell(CF, "target", $"v{i}", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "other", "x", new BigtableVersion(1000)));
        var rows = await ReadAll(
            RowSet.FromRowRanges(RowRange.ClosedOpen("cqe-mr-", "cqe-mr-~")),
            RowFilters.ColumnQualifierExact("target"));
        rows.Should().HaveCount(5);
        foreach (var row in rows)
            row.Families[0].Columns.Should().ContainSingle()
                .Which.Qualifier.ToStringUtf8().Should().Be("target");
    }

    #endregion

    #region ColumnRange filter

    [Fact]
    public async Task ColumnRange_closed_range()
    {
        await Client.MutateRowAsync(TN, "cr-1",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "d", "4", new BigtableVersion(1000)));
        var filter = RowFilters.ColumnRange(ColumnRange.Closed(CF, "b", "c"));
        var rows = await ReadAll(RowSet.FromRowKeys("cr-1"), filter);
        var quals = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().Equal("b", "c");
    }

    [Fact]
    public async Task ColumnRange_open_range()
    {
        await Client.MutateRowAsync(TN, "cr-2",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "d", "4", new BigtableVersion(1000)));
        var filter = RowFilters.ColumnRange(ColumnRange.Open(CF, "a", "d"));
        var rows = await ReadAll(RowSet.FromRowKeys("cr-2"), filter);
        var quals = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().Equal("b", "c");
    }

    [Fact]
    public async Task ColumnRange_no_match()
    {
        await Client.MutateRowAsync(TN, "cr-nm",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)));
        var filter = RowFilters.ColumnRange(ColumnRange.Closed(CF, "x", "z"));
        var rows = await ReadAll(RowSet.FromRowKeys("cr-nm"), filter);
        rows.Should().BeEmpty();
    }

    #endregion

    #region ColumnQualifierRegex filter

    [Fact]
    public async Task ColumnQualifierRegex_prefix()
    {
        await Client.MutateRowAsync(TN, "cqr-1",
            Mutations.SetCell(CF, "user_name", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "user_email", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "order_id", "v", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("cqr-1"), RowFilters.ColumnQualifierRegex("user_.*"));
        var quals = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().Contain(new[] { "user_name", "user_email" });
        quals.Should().NotContain("order_id");
    }

    [Fact]
    public async Task ColumnQualifierRegex_alternation()
    {
        await Client.MutateRowAsync(TN, "cqr-alt",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("cqr-alt"), RowFilters.ColumnQualifierRegex("a|c"));
        var quals = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().Equal("a", "c");
    }

    [Fact]
    public async Task ColumnQualifierRegex_star_matches_all()
    {
        await Client.MutateRowAsync(TN, "cqr-all",
            Mutations.SetCell(CF, "x", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "y", "v", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("cqr-all"), RowFilters.ColumnQualifierRegex(".*"));
        rows[0].Families[0].Columns.Should().HaveCount(2);
    }

    #endregion

    #region Large column counts

    [Fact]
    public async Task Row_with_50_columns()
    {
        var mutations = Enumerable.Range(0, 50).Select(i =>
            Mutations.SetCell(CF, $"col-{i:D3}", $"v{i}", new BigtableVersion(1000))
        ).ToArray();
        await Client.MutateRowAsync(TN, "lcc-50", mutations);
        var rows = await ReadAll(RowSet.FromRowKeys("lcc-50"));
        rows[0].Families[0].Columns.Should().HaveCount(50);
    }

    [Fact]
    public async Task Row_with_100_columns()
    {
        var mutations = Enumerable.Range(0, 100).Select(i =>
            Mutations.SetCell(CF, $"col-{i:D3}", $"v{i}", new BigtableVersion(1000))
        ).ToArray();
        await Client.MutateRowAsync(TN, "lcc-100", mutations);
        var rows = await ReadAll(RowSet.FromRowKeys("lcc-100"));
        rows[0].Families[0].Columns.Should().HaveCount(100);
    }

    [Fact]
    public async Task Row_with_100_columns_sorted()
    {
        var mutations = Enumerable.Range(0, 100).Select(i =>
            Mutations.SetCell(CF, $"col-{i:D3}", $"v{i}", new BigtableVersion(1000))
        ).ToArray();
        // Write in reverse
        Array.Reverse(mutations);
        await Client.MutateRowAsync(TN, "lcc-100s", mutations);
        var rows = await ReadAll(RowSet.FromRowKeys("lcc-100s"));
        var quals = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().BeInAscendingOrder();
    }

    #endregion

    #region Value patterns

    [Fact]
    public async Task Empty_value()
    {
        await Client.MutateRowAsync(TN, "vp-empty",
            Mutations.SetCell(CF, "c", ByteString.Empty, new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("vp-empty"));
        rows[0].Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task Large_value_10KB()
    {
        var val = new string('X', 10 * 1024);
        await Client.MutateRowAsync(TN, "vp-100k",
            Mutations.SetCell(CF, "c", val, new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("vp-100k"));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Length.Should().Be(10 * 1024);
    }

    [Fact]
    public async Task Binary_value_roundtrip()
    {
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        await Client.MutateRowAsync(TN, "vp-bin",
            Mutations.SetCell(CF, "c", ByteString.CopyFrom(bytes), new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("vp-bin"));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToByteArray().Should().Equal(bytes);
    }

    [Fact]
    public async Task Unicode_value()
    {
        var val = "Hello 世界 🎉";
        await Client.MutateRowAsync(TN, "vp-uni",
            Mutations.SetCell(CF, "c", val, new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("vp-uni"));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be(val);
    }

    [Fact]
    public async Task ValueExact_filter_with_binary()
    {
        var bytes = new byte[] { 0x01, 0x02, 0x03 };
        await Client.MutateRowAsync(TN, "vp-vef",
            Mutations.SetCell(CF, "c", ByteString.CopyFrom(bytes), new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("vp-vef"),
            RowFilters.ValueExact(ByteString.CopyFrom(bytes)));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task ValueExact_filter_no_match()
    {
        await Client.MutateRowAsync(TN, "vp-vefnm",
            Mutations.SetCell(CF, "c", "hello", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("vp-vefnm"),
            RowFilters.ValueExact("NOPE"));
        rows.Should().BeEmpty();
    }

    #endregion
}

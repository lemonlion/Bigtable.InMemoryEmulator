using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ValueRangeFilterExtendedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "vrfe-tests";
    private const string CF = "cf";

    public ValueRangeFilterExtendedTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        // Seed rows with various values
        await Client.MutateRowAsync(TN, "vrfe-r1", Mutations.SetCell(CF, "val", "apple"));
        await Client.MutateRowAsync(TN, "vrfe-r2", Mutations.SetCell(CF, "val", "banana"));
        await Client.MutateRowAsync(TN, "vrfe-r3", Mutations.SetCell(CF, "val", "cherry"));
        await Client.MutateRowAsync(TN, "vrfe-r4", Mutations.SetCell(CF, "val", "date"));
        await Client.MutateRowAsync(TN, "vrfe-r5", Mutations.SetCell(CF, "val", "elderberry"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null)
    {
        var list = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rows, filter: filter))
            list.Add(r);
        return list;
    }

    [Fact]
    public async Task ValueRange_closed_includes_both_endpoints()
    {
        var filter = RowFilters.ValueRange(ValueRange.Closed("banana", "date"));
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("vrfe-r", "vrfe-s")), filter);
        var values = rows.SelectMany(r => r.Families).SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Select(c => c.Value.ToStringUtf8()).ToList();
        values.Should().BeEquivalentTo(new[] { "banana", "cherry", "date" });
    }

    [Fact]
    public async Task ValueRange_closedopen_excludes_end()
    {
        var filter = RowFilters.ValueRange(ValueRange.ClosedOpen("banana", "date"));
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("vrfe-r", "vrfe-s")), filter);
        var values = rows.SelectMany(r => r.Families).SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Select(c => c.Value.ToStringUtf8()).ToList();
        values.Should().BeEquivalentTo(new[] { "banana", "cherry" });
    }

    [Fact]
    public async Task ValueRange_open_excludes_both()
    {
        var filter = RowFilters.ValueRange(ValueRange.Open("banana", "elderberry"));
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("vrfe-r", "vrfe-s")), filter);
        var values = rows.SelectMany(r => r.Families).SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Select(c => c.Value.ToStringUtf8()).ToList();
        values.Should().BeEquivalentTo(new[] { "cherry", "date" });
    }

    [Fact]
    public async Task ValueRange_openclosed_excludes_start()
    {
        var filter = RowFilters.ValueRange(ValueRange.OpenClosed("apple", "cherry"));
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("vrfe-r", "vrfe-s")), filter);
        var values = rows.SelectMany(r => r.Families).SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Select(c => c.Value.ToStringUtf8()).ToList();
        values.Should().BeEquivalentTo(new[] { "banana", "cherry" });
    }

    [Fact]
    public async Task ValueRange_no_matches()
    {
        var filter = RowFilters.ValueRange(ValueRange.Closed("mango", "pear"));
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("vrfe-r", "vrfe-s")), filter);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ValueRange_single_value_closed()
    {
        var filter = RowFilters.ValueRange(ValueRange.Closed("cherry", "cherry"));
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("vrfe-r", "vrfe-s")), filter);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task ValueRange_all_values()
    {
        var filter = RowFilters.ValueRange(ValueRange.Closed("a", "z"));
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("vrfe-r", "vrfe-s")), filter);
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task ValueRange_chained_with_column_filter()
    {
        var rk = "vrfe-chain";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "100", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "200", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "300", new BigtableVersion(2000)));

        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("a"),
            RowFilters.ValueRange(ValueRange.Closed("100", "200")));
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().ContainSingle();
        cells[0].Value.ToStringUtf8().Should().Be("100");
    }

    [Fact]
    public async Task ValueRange_on_multiple_versions()
    {
        var rk = "vrfe-ver";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "aaa", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "mmm", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "zzz", new BigtableVersion(3000)));

        var filter = RowFilters.ValueRange(ValueRange.Closed("aaa", "mmm"));
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
        var values = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Select(c => c.Value.ToStringUtf8()).ToList();
        values.Should().HaveCount(2);
        values.Should().Contain("aaa");
        values.Should().Contain("mmm");
    }

    [Fact]
    public async Task ValueRange_interleaved_with_exact()
    {
        var rk = "vrfe-inter";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "hello"),
            Mutations.SetCell(CF, "b", "world"),
            Mutations.SetCell(CF, "c", "foo"));

        var filter = RowFilters.Interleave(
            RowFilters.ValueRange(ValueRange.Closed("hello", "hello")),
            RowFilters.ValueExact("foo"));
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
        var values = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Select(c => c.Value.ToStringUtf8()).ToList();
        values.Should().HaveCount(2);
    }

    [Fact]
    public async Task ValueExact_returns_matching_cells_only()
    {
        var rk = "vrfe-exact";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "target"),
            Mutations.SetCell(CF, "b", "other"),
            Mutations.SetCell(CF, "c", "target"));

        var row = await Client.ReadRowAsync(TN, rk, RowFilters.ValueExact("target"));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(2);
    }

    [Fact]
    public async Task ValueExact_no_match_returns_null()
    {
        var rk = "vrfe-exact-no";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a", "something"));

        var row = await Client.ReadRowAsync(TN, rk, RowFilters.ValueExact("notfound"));
        row.Should().BeNull();
    }

    [Fact]
    public async Task ValueRegex_partial_match()
    {
        var rk = "vrfe-regex";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "hello-world"),
            Mutations.SetCell(CF, "b", "goodbye-world"),
            Mutations.SetCell(CF, "c", "hello-there"));

        // Full match: .*world matches strings ending with "world"
        var row = await Client.ReadRowAsync(TN, rk, RowFilters.ValueRegex(".*world"));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(2);
    }

    [Fact]
    public async Task ValueRegex_anchor_free_full_match()
    {
        var rk = "vrfe-regex-full";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "abc"),
            Mutations.SetCell(CF, "b", "abcdef"),
            Mutations.SetCell(CF, "c", "xabc"));

        // Full match "abc" only matches exactly "abc"
        var row = await Client.ReadRowAsync(TN, rk, RowFilters.ValueRegex("abc"));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().ContainSingle();
        row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Single()
            .Value.ToStringUtf8().Should().Be("abc");
    }
}

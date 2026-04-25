using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for RowRange types: open, closed, open-closed, closed-open.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowrange
///   "Specifies a contiguous range of rows."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class RowRangeVariationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "rr-var";

    public RowRangeVariationTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        var tn = TN;
        for (int i = 0; i < 10; i++)
            await Client.MutateRowAsync(tn, $"rr-{i:D4}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<string>> ReadKeys(RowSet rowSet)
    {
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(TN, rowSet))
            keys.Add(row.Key.ToStringUtf8());
        return keys;
    }

    #region ClosedOpen range

    [Fact]
    public async Task ClosedOpen_includes_start_excludes_end()
    {
        var keys = await ReadKeys(RowSet.FromRowRanges(RowRange.ClosedOpen("rr-0002", "rr-0005")));
        keys.Should().BeEquivalentTo(new[] { "rr-0002", "rr-0003", "rr-0004" });
    }

    [Fact]
    public async Task ClosedOpen_single_row()
    {
        var keys = await ReadKeys(RowSet.FromRowRanges(RowRange.ClosedOpen("rr-0005", "rr-0006")));
        keys.Should().ContainSingle().Which.Should().Be("rr-0005");
    }

    [Fact]
    public async Task ClosedOpen_empty_when_start_equals_end()
    {
        var keys = await ReadKeys(RowSet.FromRowRanges(RowRange.ClosedOpen("rr-0005", "rr-0005")));
        keys.Should().BeEmpty();
    }

    #endregion

    #region Closed range

    [Fact]
    public async Task Closed_includes_both_ends()
    {
        var keys = await ReadKeys(RowSet.FromRowRanges(RowRange.Closed("rr-0003", "rr-0006")));
        keys.Should().BeEquivalentTo(new[] { "rr-0003", "rr-0004", "rr-0005", "rr-0006" });
    }

    [Fact]
    public async Task Closed_single_row()
    {
        var keys = await ReadKeys(RowSet.FromRowRanges(RowRange.Closed("rr-0007", "rr-0007")));
        keys.Should().ContainSingle().Which.Should().Be("rr-0007");
    }

    #endregion

    #region OpenClosed range

    [Fact]
    public async Task OpenClosed_excludes_start_includes_end()
    {
        var keys = await ReadKeys(RowSet.FromRowRanges(RowRange.OpenClosed("rr-0002", "rr-0005")));
        keys.Should().BeEquivalentTo(new[] { "rr-0003", "rr-0004", "rr-0005" });
    }

    #endregion

    #region Open range

    [Fact]
    public async Task Open_excludes_both_ends()
    {
        var keys = await ReadKeys(RowSet.FromRowRanges(RowRange.Open("rr-0002", "rr-0005")));
        keys.Should().BeEquivalentTo(new[] { "rr-0003", "rr-0004" });
    }

    #endregion

    #region Unbounded ranges

    [Fact]
    public async Task ClosedOpen_start_only_reads_from_start()
    {
        // No end key = read to end of table
        var keys = await ReadKeys(RowSet.FromRowRanges(RowRange.ClosedOpen("rr-0008", null)));
        keys.Should().BeEquivalentTo(new[] { "rr-0008", "rr-0009" });
    }

    [Fact]
    public async Task ClosedOpen_end_only_reads_to_end()
    {
        // No start key = read from beginning
        var keys = await ReadKeys(RowSet.FromRowRanges(RowRange.ClosedOpen(null, "rr-0003")));
        keys.Should().BeEquivalentTo(new[] { "rr-0000", "rr-0001", "rr-0002" });
    }

    #endregion

    #region Multiple ranges

    [Fact]
    public async Task Multiple_ranges_union()
    {
        var keys = await ReadKeys(RowSet.FromRowRanges(
            RowRange.Closed("rr-0001", "rr-0002"),
            RowRange.Closed("rr-0007", "rr-0008")));
        keys.Should().BeEquivalentTo(new[] { "rr-0001", "rr-0002", "rr-0007", "rr-0008" });
    }

    [Fact]
    public async Task Overlapping_ranges()
    {
        var keys = await ReadKeys(RowSet.FromRowRanges(
            RowRange.Closed("rr-0001", "rr-0004"),
            RowRange.Closed("rr-0003", "rr-0006")));
        // Should return union without duplicates
        keys.Distinct().Should().HaveCount(keys.Count);
        keys.Should().Contain("rr-0001").And.Contain("rr-0006");
    }

    [Fact]
    public async Task Adjacent_ranges()
    {
        var keys = await ReadKeys(RowSet.FromRowRanges(
            RowRange.ClosedOpen("rr-0000", "rr-0005"),
            RowRange.ClosedOpen("rr-0005", "rr-0010")));
        keys.Should().HaveCount(10);
    }

    #endregion

    #region Range with keys

    [Fact]
    public async Task Range_and_specific_keys()
    {
        var rowSet = new RowSet();
        rowSet.RowRanges.Add(new RowRange
        {
            StartKeyClosed = ByteString.CopyFromUtf8("rr-0000"),
            EndKeyClosed = ByteString.CopyFromUtf8("rr-0002")
        });
        rowSet.RowKeys.Add(ByteString.CopyFromUtf8("rr-0009"));
        var keys = await ReadKeys(rowSet);
        keys.Should().Contain("rr-0000").And.Contain("rr-0009");
    }

    #endregion

    #region Empty results

    [Fact]
    public async Task Range_beyond_data()
    {
        var keys = await ReadKeys(RowSet.FromRowRanges(RowRange.Closed("zz-0000", "zz-9999")));
        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task Range_between_rows()
    {
        // All rows are rr-NNNN, range in ss-* space
        var keys = await ReadKeys(RowSet.FromRowRanges(RowRange.Closed("ss-0000", "ss-9999")));
        keys.Should().BeEmpty();
    }

    #endregion
}

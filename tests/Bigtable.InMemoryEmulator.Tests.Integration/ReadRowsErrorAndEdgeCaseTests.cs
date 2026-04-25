using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for ReadRows error handling and edge cases: nonexistent tables,
/// empty results, large scans, and streaming behavior.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsErrorAndEdgeCaseTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "rr-errec";

    public ReadRowsErrorAndEdgeCaseTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        var tn = TN;
        for (int i = 0; i < 20; i++)
            await _fixture.Client.MutateRowAsync(tn, $"re-{i:D4}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    #region Nonexistent table

    [Fact]
    public async Task ReadRow_nonexistent_table_throws()
    {
        var fakeTable = _fixture.GetTableName("nonexistent-read-table");
        var act = () => Client.ReadRowAsync(fakeTable, "r1");
        await act.Should().ThrowAsync<Grpc.Core.RpcException>()
            .Where(e => e.StatusCode == Grpc.Core.StatusCode.NotFound);
    }

    [Fact]
    public async Task ReadRows_nonexistent_table_throws()
    {
        var fakeTable = _fixture.GetTableName("nonexistent-read-table-2");
        var act = async () =>
        {
            await foreach (var _ in Client.ReadRows(fakeTable)) { }
        };
        await act.Should().ThrowAsync<Grpc.Core.RpcException>()
            .Where(e => e.StatusCode == Grpc.Core.StatusCode.NotFound);
    }

    #endregion

    #region Empty results

    [Fact]
    public async Task ReadRow_nonexistent_key_returns_null()
    {
        var row = await Client.ReadRowAsync(TN, "nonexistent-key-xyz");
        row.Should().BeNull();
    }

    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task ReadRows_empty_range_returns_nothing()
    {
        var count = 0;
        await foreach (var _ in Client.ReadRows(TN,
            RowSet.FromRowRanges(RowRange.Closed("zz-start", "zz-end"))))
            count++;
        count.Should().Be(0);
    }

    [Fact]
    public async Task ReadRows_all_keys_nonexistent()
    {
        var count = 0;
        await foreach (var _ in Client.ReadRows(TN,
            RowSet.FromRowKeys("no1", "no2", "no3")))
            count++;
        count.Should().Be(0);
    }

    #endregion

    #region ReadRow with filter

    [Fact]
    public async Task ReadRow_with_passing_filter()
    {
        var row = await Client.ReadRowAsync(TN, "re-0005",
            RowFilters.ValueRegex("v5"));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task ReadRow_with_blocking_filter()
    {
        var row = await Client.ReadRowAsync(TN, "re-0005",
            RowFilters.BlockAllFilter());
        row.Should().BeNull();
    }

    [Fact]
    public async Task ReadRow_with_non_matching_filter()
    {
        var row = await Client.ReadRowAsync(TN, "re-0005",
            RowFilters.ValueRegex("nomatch"));
        row.Should().BeNull();
    }

    [Fact]
    public async Task ReadRow_with_strip_value()
    {
        var row = await Client.ReadRowAsync(TN, "re-0005",
            RowFilters.StripValueTransformer());
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().BeEmpty();
    }

    #endregion

    #region ReadRows ordering guarantees

    [Fact]
    public async Task ReadRows_returns_ascending_order()
    {
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(TN))
            keys.Add(row.Key.ToStringUtf8());
        keys.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task ReadRows_range_returns_ascending()
    {
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(TN,
            RowSet.FromRowRanges(RowRange.Closed("re-0005", "re-0015"))))
            keys.Add(row.Key.ToStringUtf8());
        keys.Should().BeInAscendingOrder();
        keys[0].Should().Be("re-0005");
    }

    #endregion

    #region ReadRows family/column structure

    [Fact]
    public async Task ReadRow_returns_correct_family_structure()
    {
        var row = await Client.ReadRowAsync(TN, "re-0001");
        row!.Families.Should().ContainSingle().Which.Name.Should().Be(CF);
        row.Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("c");
    }

    [Fact]
    public async Task ReadRow_returns_correct_cell_content()
    {
        var row = await Client.ReadRowAsync(TN, "re-0010");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v10");
    }

    [Fact]
    public async Task ReadRow_cell_has_correct_timestamp()
    {
        var row = await Client.ReadRowAsync(TN, "re-0001");
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(1_000_000);
    }

    #endregion

    #region ReadRows count

    [Fact]
    public async Task ReadRows_all_returns_correct_count()
    {
        var count = 0;
        await foreach (var _ in Client.ReadRows(TN))
            count++;
        count.Should().Be(20);
    }

    [Fact]
    public async Task ReadRows_subset_returns_correct_count()
    {
        var count = 0;
        await foreach (var _ in Client.ReadRows(TN,
            RowSet.FromRowRanges(RowRange.Closed("re-0005", "re-0009"))))
            count++;
        count.Should().Be(5);
    }

    #endregion

    #region After delete operations

    [Fact]
    public async Task ReadRow_returns_null_after_delete()
    {
        await Client.MutateRowAsync(TN, "re-temp",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "re-temp", Mutations.DeleteFromRow());
        var row = await Client.ReadRowAsync(TN, "re-temp");
        row.Should().BeNull();
    }

    [Fact]
    public async Task ReadRows_skips_deleted_rows()
    {
        await Client.MutateRowAsync(TN, "re-del1",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "re-del2",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "re-del1", Mutations.DeleteFromRow());
        var count = 0;
        await foreach (var _ in Client.ReadRows(TN,
            RowSet.FromRowKeys("re-del1", "re-del2")))
            count++;
        count.Should().Be(1);
    }

    #endregion
}

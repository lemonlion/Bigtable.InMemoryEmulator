using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Stress tests for MutateRow single-row mutations — all mutation types.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutateRowSingleStressTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "mutate-single";
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public MutateRowSingleStressTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
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

    #region SetCell mutations

    [Fact]
    public async Task SetCell_single_column()
    {
        await Client.MutateRowAsync(TN, "ms-sc1",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("ms-sc1"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v");
    }

    [Fact]
    public async Task SetCell_multiple_columns_same_family()
    {
        await Client.MutateRowAsync(TN, "ms-mc",
            Mutations.SetCell(CF, "a", "va", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "vb", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "vc", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("ms-mc"));
        rows[0].Families[0].Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task SetCell_cross_family()
    {
        await Client.MutateRowAsync(TN, "ms-cf",
            Mutations.SetCell(CF, "a", "va", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "b", "vb", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("ms-cf"));
        rows[0].Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task SetCell_empty_value()
    {
        await Client.MutateRowAsync(TN, "ms-ev",
            Mutations.SetCell(CF, "c", ByteString.Empty, new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("ms-ev"));
        rows[0].Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task SetCell_binary_value()
    {
        var bytes = new byte[] { 0x00, 0xFF, 0x01, 0xFE };
        await Client.MutateRowAsync(TN, "ms-bv",
            Mutations.SetCell(CF, "c", ByteString.CopyFrom(bytes), new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("ms-bv"));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToByteArray().Should().Equal(bytes);
    }

    [Fact]
    public async Task SetCell_unicode_value()
    {
        var val = "你好世界🌍";
        await Client.MutateRowAsync(TN, "ms-uni",
            Mutations.SetCell(CF, "c", val, new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("ms-uni"));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be(val);
    }

    [Fact]
    public async Task SetCell_multiple_versions()
    {
        await Client.MutateRowAsync(TN, "ms-mv",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        var rows = await ReadAll(RowSet.FromRowKeys("ms-mv"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task SetCell_same_version_overwrites()
    {
        await Client.MutateRowAsync(TN, "ms-ow",
            Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ms-ow",
            Mutations.SetCell(CF, "c", "second", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("ms-ow"));
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task SetCell_10_columns()
    {
        var mutations = Enumerable.Range(0, 10)
            .Select(i => Mutations.SetCell(CF, $"col{i:D2}", $"val{i}", new BigtableVersion(1000)))
            .ToArray();
        await Client.MutateRowAsync(TN, "ms-10c", mutations);
        var rows = await ReadAll(RowSet.FromRowKeys("ms-10c"));
        rows[0].Families[0].Columns.Should().HaveCount(10);
    }

    [Fact]
    public async Task SetCell_50_columns()
    {
        var mutations = Enumerable.Range(0, 50)
            .Select(i => Mutations.SetCell(CF, $"col{i:D3}", $"val{i}", new BigtableVersion(1000)))
            .ToArray();
        await Client.MutateRowAsync(TN, "ms-50c", mutations);
        var rows = await ReadAll(RowSet.FromRowKeys("ms-50c"));
        rows[0].Families[0].Columns.Should().HaveCount(50);
    }

    [Fact]
    public async Task SetCell_20_versions_same_column()
    {
        var mutations = Enumerable.Range(1, 20)
            .Select(i => Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)))
            .ToArray();
        await Client.MutateRowAsync(TN, "ms-20v", mutations);
        var rows = await ReadAll(RowSet.FromRowKeys("ms-20v"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(20);
    }

    [Fact]
    public async Task SetCell_4KB_value()
    {
        var val = new string('A', 4096);
        await Client.MutateRowAsync(TN, "ms-4k",
            Mutations.SetCell(CF, "c", val, new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("ms-4k"));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Length.Should().Be(4096);
    }

    #endregion

    #region DeleteFromRow mutations

    [Fact]
    public async Task DeleteFromRow_removes_all()
    {
        await Client.MutateRowAsync(TN, "ms-dfr",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ms-dfr", Mutations.DeleteFromRow());
        var rows = await ReadAll(RowSet.FromRowKeys("ms-dfr"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteFromRow_with_versions()
    {
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "ms-dfrv",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
        await Client.MutateRowAsync(TN, "ms-dfrv", Mutations.DeleteFromRow());
        var rows = await ReadAll(RowSet.FromRowKeys("ms-dfrv"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteFromRow_nonexistent_row_succeeds()
    {
        // Deleting a row that doesn't exist should be a no-op
        await Client.MutateRowAsync(TN, "ms-dfr-noexist", Mutations.DeleteFromRow());
    }

    #endregion

    #region DeleteFromFamily mutations

    [Fact]
    public async Task DeleteFromFamily_removes_family_only()
    {
        await Client.MutateRowAsync(TN, "ms-dff",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "b", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ms-dff", Mutations.DeleteFromFamily(CF));
        var rows = await ReadAll(RowSet.FromRowKeys("ms-dff"));
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be("cf2");
    }

    [Fact]
    public async Task DeleteFromFamily_deletes_all_columns()
    {
        await Client.MutateRowAsync(TN, "ms-dffac",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ms-dffac", Mutations.DeleteFromFamily(CF));
        var rows = await ReadAll(RowSet.FromRowKeys("ms-dffac"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteFromFamily_deletes_all_versions()
    {
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "ms-dffav",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
        await Client.MutateRowAsync(TN, "ms-dffav", Mutations.DeleteFromFamily(CF));
        var rows = await ReadAll(RowSet.FromRowKeys("ms-dffav"));
        rows.Should().BeEmpty();
    }

    #endregion

    #region DeleteFromColumn mutations

    [Fact]
    public async Task DeleteFromColumn_removes_all_versions()
    {
        await Client.MutateRowAsync(TN, "ms-dfc",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "b", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ms-dfc", Mutations.DeleteFromColumn(CF, "a"));
        var rows = await ReadAll(RowSet.FromRowKeys("ms-dfc"));
        rows[0].Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("b");
    }

    [Fact]
    public async Task DeleteFromColumn_with_range()
    {
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "ms-dfcr",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
        await Client.MutateRowAsync(TN, "ms-dfcr",
            Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(4000))));
        var rows = await ReadAll(RowSet.FromRowKeys("ms-dfcr"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(3); // 1, 4, 5
    }

    #endregion

    #region Combined mutations in single MutateRow

    [Fact]
    public async Task SetCell_and_DeleteFromColumn_in_same_call()
    {
        await Client.MutateRowAsync(TN, "ms-combo1",
            Mutations.SetCell(CF, "a", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ms-combo1",
            Mutations.DeleteFromColumn(CF, "a"),
            Mutations.SetCell(CF, "a", "new", new BigtableVersion(2000)));
        var rows = await ReadAll(RowSet.FromRowKeys("ms-combo1"));
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task SetCell_and_DeleteFromRow_in_same_call()
    {
        await Client.MutateRowAsync(TN, "ms-combo2",
            Mutations.SetCell(CF, "a", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ms-combo2",
            Mutations.DeleteFromRow(),
            Mutations.SetCell(CF, "a", "new", new BigtableVersion(2000)));
        var rows = await ReadAll(RowSet.FromRowKeys("ms-combo2"));
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task SetCell_both_families_in_same_call()
    {
        await Client.MutateRowAsync(TN, "ms-combo3",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "b", "v2", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("ms-combo3"));
        rows[0].Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task DeleteFromFamily_and_SetCell_in_other_family()
    {
        await Client.MutateRowAsync(TN, "ms-combo4",
            Mutations.SetCell(CF, "a", "old", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "b", "keep", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ms-combo4",
            Mutations.DeleteFromFamily(CF),
            Mutations.SetCell(CF2, "c", "new", new BigtableVersion(2000)));
        var rows = await ReadAll(RowSet.FromRowKeys("ms-combo4"));
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be("cf2");
    }

    [Fact]
    public async Task Multiple_SetCells_interleaved_with_deletes()
    {
        await Client.MutateRowAsync(TN, "ms-combo5",
            Mutations.SetCell(CF, "a", "a1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "b1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "c1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ms-combo5",
            Mutations.DeleteFromColumn(CF, "b"),
            Mutations.SetCell(CF, "d", "d1", new BigtableVersion(2000)));
        var rows = await ReadAll(RowSet.FromRowKeys("ms-combo5"));
        var quals = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().Contain(new[] { "a", "c", "d" });
        quals.Should().NotContain("b");
    }

    #endregion

    #region Row key patterns

    [Fact]
    public async Task Empty_string_row_key_fails()
    {
        // Empty row key should not be allowed
        var act = () => Client.MutateRowAsync(TN, "",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Single_character_row_key()
    {
        await Client.MutateRowAsync(TN, "x",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("x"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Special_character_row_keys()
    {
        var keys = new[] { "ms-key:1", "ms-key#2", "ms-key.3", "ms-key/4", "ms-key|5" };
        foreach (var key in keys)
            await Client.MutateRowAsync(TN, key,
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        foreach (var key in keys)
        {
            var rows = await ReadAll(RowSet.FromRowKeys(key));
            rows.Should().ContainSingle();
        }
    }

    [Fact]
    public async Task Binary_row_key()
    {
        var key = ByteString.CopyFrom(0x00, 0x01, 0xFF, 0xFE);
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys(key));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task UTF8_row_key()
    {
        await Client.MutateRowAsync(TN, "ms-世界",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("ms-世界"));
        rows.Should().ContainSingle();
    }

    #endregion

    #region Idempotency and ordering

    [Fact]
    public async Task Sequential_writes_to_same_row()
    {
        for (int i = 0; i < 10; i++)
            await Client.MutateRowAsync(TN, "ms-seq",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion((i + 1) * 1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("ms-seq"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(10);
    }

    [Fact]
    public async Task Write_read_write_read_consistency()
    {
        await Client.MutateRowAsync(TN, "ms-wrwr",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        var r1 = await ReadAll(RowSet.FromRowKeys("ms-wrwr"));
        r1[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v1");

        await Client.MutateRowAsync(TN, "ms-wrwr",
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));
        var r2 = await ReadAll(RowSet.FromRowKeys("ms-wrwr"));
        r2[0].Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task Write_many_different_rows()
    {
        for (int i = 0; i < 20; i++)
            await Client.MutateRowAsync(TN, $"ms-many-{i:D3}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("ms-many-", "ms-many~")));
        rows.Should().HaveCount(20);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeInAscendingOrder();
    }

    #endregion
}

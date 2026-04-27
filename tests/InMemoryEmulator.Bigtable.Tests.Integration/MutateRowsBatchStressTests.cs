using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Stress tests for MutateRows batch operations — large batches, partial failures,
/// duplicate keys, mixed mutation types.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowsrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutateRowsBatchStressTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "mutatebatch-stress";
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public MutateRowsBatchStressTests(EmulatorSession session) => _fixture = session.CreateFixture();
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

    #region Batch size variations

    [Fact]
    public async Task MutateRows_single_entry()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("b-single", Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("b-single"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task MutateRows_5_entries()
    {
        var entries = Enumerable.Range(0, 5).Select(i =>
            Mutations.CreateEntry($"b5-{i:D2}", Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll();
        rows.Count(r => r.Key.ToStringUtf8().StartsWith("b5-")).Should().Be(5);
    }

    [Fact]
    public async Task MutateRows_20_entries()
    {
        var entries = Enumerable.Range(0, 20).Select(i =>
            Mutations.CreateEntry($"b20-{i:D2}", Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll();
        rows.Count(r => r.Key.ToStringUtf8().StartsWith("b20-")).Should().Be(20);
    }

    [Fact]
    public async Task MutateRows_100_entries()
    {
        var entries = Enumerable.Range(0, 100).Select(i =>
            Mutations.CreateEntry($"b100-{i:D3}", Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll();
        rows.Count(r => r.Key.ToStringUtf8().StartsWith("b100-")).Should().Be(100);
    }

    #endregion

    #region Multi-mutation entries

    [Fact]
    public async Task MutateRows_entry_with_3_set_cells()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("bm3",
                Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("bm3"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task MutateRows_entry_with_set_and_delete()
    {
        // Pre-write
        await Client.MutateRowAsync(TN, "bsd", Mutations.SetCell(CF, "old", "x", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("bsd",
                Mutations.DeleteFromColumn(CF, "old", new BigtableVersionRange(new BigtableVersion(0), new BigtableVersion(2000))),
                Mutations.SetCell(CF, "new", "y", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("bsd"));
        rows.Should().ContainSingle();
        var cols = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("new");
        cols.Should().NotContain("old");
    }

    [Fact]
    public async Task MutateRows_entry_with_delete_from_row()
    {
        await Client.MutateRowAsync(TN, "bdr", Mutations.SetCell(CF, "c", "x", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("bdr", Mutations.DeleteFromRow())
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("bdr"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task MutateRows_entry_with_delete_from_family()
    {
        await Client.MutateRowAsync(TN, "bdf",
            Mutations.SetCell(CF, "c", "x", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "y", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("bdf", Mutations.DeleteFromFamily(CF))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("bdf"));
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be(CF2);
    }

    [Fact]
    public async Task MutateRows_entry_with_multiple_delete_from_columns()
    {
        await Client.MutateRowAsync(TN, "bmdc",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("bmdc",
                Mutations.DeleteFromColumn(CF, "a", new BigtableVersionRange(new BigtableVersion(0), new BigtableVersion(2000))),
                Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(new BigtableVersion(0), new BigtableVersion(2000))))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("bmdc"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("b");
    }

    [Fact]
    public async Task MutateRows_entry_cross_family_mutations()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("bcf",
                Mutations.SetCell(CF, "c1", "v1", new BigtableVersion(1000)),
                Mutations.SetCell(CF2, "c2", "v2", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("bcf"));
        rows.Should().ContainSingle();
        rows[0].Families.Select(f => f.Name).Should().Contain(new[] { CF, CF2 });
    }

    #endregion

    #region Same-row entries

    [Fact]
    public async Task MutateRows_same_row_two_entries_different_columns()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("bsame2", Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000))),
            Mutations.CreateEntry("bsame2", Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("bsame2"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns.Should().HaveCount(2);
    }

    [Fact]
    public async Task MutateRows_same_row_same_column_different_timestamps()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("bsscd", Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000))),
            Mutations.CreateEntry("bsscd", Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("bsscd"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task MutateRows_same_row_same_column_same_timestamp_last_entry_wins()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("bsss", Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000))),
            Mutations.CreateEntry("bsss", Mutations.SetCell(CF, "c", "second", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("bsss"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task MutateRows_same_row_write_then_delete_in_later_entry()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("bswd", Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000))),
            Mutations.CreateEntry("bswd", Mutations.DeleteFromRow())
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("bswd"));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task MutateRows_same_row_delete_then_write_in_later_entry()
    {
        await Client.MutateRowAsync(TN, "bsdw", Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("bsdw", Mutations.DeleteFromRow()),
            Mutations.CreateEntry("bsdw", Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("bsdw"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    #endregion

    #region Multiple versions in batch

    [Fact]
    public async Task MutateRows_10_versions_of_same_cell_across_entries()
    {
        var entries = Enumerable.Range(1, 10).Select(v =>
            Mutations.CreateEntry("bv10", Mutations.SetCell(CF, "c", $"v{v}", new BigtableVersion(v * 1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("bv10"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(10);
    }

    [Fact]
    public async Task MutateRows_versions_stored_newest_first()
    {
        var entries = Enumerable.Range(1, 5).Select(v =>
            Mutations.CreateEntry("bvsort", Mutations.SetCell(CF, "c", $"v{v}", new BigtableVersion(v * 1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("bvsort"));
        var ts = rows[0].Families[0].Columns[0].Cells.Select(c => c.TimestampMicros).ToList();
        ts.Should().BeInDescendingOrder();
    }

    #endregion

    #region Cross-family batch operations

    [Fact]
    public async Task MutateRows_different_families_per_entry()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("bxf1", Mutations.SetCell(CF, "c", "cf1", new BigtableVersion(1000))),
            Mutations.CreateEntry("bxf2", Mutations.SetCell(CF2, "c", "cf2", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);

        var r1 = await ReadAll(RowSet.FromRowKeys("bxf1"));
        r1.Should().ContainSingle();
        r1[0].Families[0].Name.Should().Be(CF);

        var r2 = await ReadAll(RowSet.FromRowKeys("bxf2"));
        r2.Should().ContainSingle();
        r2[0].Families[0].Name.Should().Be(CF2);
    }

    [Fact]
    public async Task MutateRows_both_families_in_single_entry()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("bxfs",
                Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
                Mutations.SetCell(CF2, "c", "v2", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("bxfs"));
        rows.Should().ContainSingle();
        rows[0].Families.Should().HaveCount(2);
    }

    #endregion

    #region Ordering verification

    [Fact]
    public async Task MutateRows_rows_stored_in_lexicographic_order()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("border-z", Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000))),
            Mutations.CreateEntry("border-a", Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000))),
            Mutations.CreateEntry("border-m", Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var range = RowSet.FromRowRanges(RowRange.ClosedOpen("border-a", "border-zz"));
        var rows = await ReadAll(range);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task MutateRows_columns_stored_in_lexicographic_order()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("bordcol",
                Mutations.SetCell(CF, "z", "1", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "a", "2", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "m", "3", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("bordcol"));
        var cols = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().BeInAscendingOrder();
    }

    #endregion

    #region Idempotency

    [Fact]
    public async Task MutateRows_same_batch_twice_is_idempotent()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("bidem", Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("bidem"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle();
    }

    [Fact]
    public async Task MutateRows_delete_of_nonexistent_row_succeeds()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("bneverexist", Mutations.DeleteFromRow())
        };
        // Should not throw
        await Client.MutateRowsAsync(TN, entries);
    }

    #endregion

    #region Large values

    [Fact]
    public async Task MutateRows_1KB_values()
    {
        var val = new string('X', 1024);
        var entries = Enumerable.Range(0, 5).Select(i =>
            Mutations.CreateEntry($"blv1k-{i}", Mutations.SetCell(CF, "c", val, new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("blv1k-0", "blv1k-z")));
        rows.Should().HaveCount(5);
        foreach (var row in rows)
            row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Length.Should().Be(1024);
    }

    [Fact]
    public async Task MutateRows_10KB_values()
    {
        var val = new string('Y', 10240);
        var entries = Enumerable.Range(0, 3).Select(i =>
            Mutations.CreateEntry($"blv10k-{i}", Mutations.SetCell(CF, "c", val, new BigtableVersion(1000)))
        ).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowRanges(RowRange.ClosedOpen("blv10k-0", "blv10k-z")));
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task MutateRows_empty_value()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("bev",
                Mutations.SetCell(CF, "c", ByteString.Empty, new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("bev"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    #endregion

    #region Binary values

    [Fact]
    public async Task MutateRows_binary_value_roundtrip()
    {
        var bytes = new byte[] { 0x00, 0xFF, 0x01, 0xFE, 0x80 };
        var entries = new[]
        {
            Mutations.CreateEntry("bbin",
                Mutations.SetCell(CF, "c", ByteString.CopyFrom(bytes), new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("bbin"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells[0].Value.ToByteArray().Should().Equal(bytes);
    }

    #endregion

    #region Many columns

    [Fact]
    public async Task MutateRows_entry_with_50_columns()
    {
        var mutations = Enumerable.Range(0, 50).Select(i =>
            Mutations.SetCell(CF, $"col-{i:D3}", $"v{i}", new BigtableVersion(1000))
        ).ToArray();
        var entries = new[] { Mutations.CreateEntry("bmc50", mutations) };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("bmc50"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns.Should().HaveCount(50);
    }

    [Fact]
    public async Task MutateRows_columns_ordered_after_batch()
    {
        var mutations = new[]
        {
            Mutations.SetCell(CF, "zzz", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "aaa", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "mmm", "3", new BigtableVersion(1000))
        };
        var entries = new[] { Mutations.CreateEntry("bcolord", mutations) };
        await Client.MutateRowsAsync(TN, entries);
        var rows = await ReadAll(RowSet.FromRowKeys("bcolord"));
        var cols = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().BeInAscendingOrder();
    }

    #endregion
}

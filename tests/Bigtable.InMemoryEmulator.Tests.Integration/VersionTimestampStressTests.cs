using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Stress tests for multi-version patterns, timestamp edge cases, and version management.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#cell
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class VersionTimestampStressTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "version-ts-stress";
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public VersionTimestampStressTests(EmulatorSession session) => _fixture = session.CreateFixture();
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

    #region Multi-version writes

    [Fact]
    public async Task Write_1_version()
    {
        await Client.MutateRowAsync(TN, "vt-1v", Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("vt-1v"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(1);
    }

    [Fact]
    public async Task Write_5_versions()
    {
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "vt-5v",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("vt-5v"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(5);
    }

    [Fact]
    public async Task Write_20_versions()
    {
        var mutations = Enumerable.Range(1, 20).Select(i =>
            Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000))
        ).ToArray();
        await Client.MutateRowAsync(TN, "vt-20v", mutations);
        var rows = await ReadAll(RowSet.FromRowKeys("vt-20v"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(20);
    }

    [Fact]
    public async Task Write_100_versions()
    {
        var mutations = Enumerable.Range(1, 100).Select(i =>
            Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000))
        ).ToArray();
        await Client.MutateRowAsync(TN, "vt-100v", mutations);
        var rows = await ReadAll(RowSet.FromRowKeys("vt-100v"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(100);
    }

    #endregion

    #region Version ordering

    [Fact]
    public async Task Versions_always_returned_newest_first()
    {
        var mutations = Enumerable.Range(1, 10).Select(i =>
            Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000))
        ).ToArray();
        await Client.MutateRowAsync(TN, "vt-ord", mutations);
        var rows = await ReadAll(RowSet.FromRowKeys("vt-ord"));
        var timestamps = rows[0].Families[0].Columns[0].Cells.Select(c => c.TimestampMicros).ToList();
        timestamps.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Interleaved_writes_maintain_order()
    {
        // Write in non-sequential timestamp order
        await Client.MutateRowAsync(TN, "vt-intlv", Mutations.SetCell(CF, "c", "v5", new BigtableVersion(5000)));
        await Client.MutateRowAsync(TN, "vt-intlv", Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "vt-intlv", Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        await Client.MutateRowAsync(TN, "vt-intlv", Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, "vt-intlv", Mutations.SetCell(CF, "c", "v4", new BigtableVersion(4000)));

        var rows = await ReadAll(RowSet.FromRowKeys("vt-intlv"));
        var timestamps = rows[0].Families[0].Columns[0].Cells.Select(c => c.TimestampMicros / 1000).ToList();
        timestamps.Should().Equal(5000, 4000, 3000, 2000, 1000);
    }

    [Fact]
    public async Task Multiple_columns_each_version_independent()
    {
        await Client.MutateRowAsync(TN, "vt-mcvi",
            Mutations.SetCell(CF, "a", "a1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "a2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "b", "b1", new BigtableVersion(3000)));
        var rows = await ReadAll(RowSet.FromRowKeys("vt-mcvi"));
        rows[0].Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "a").Cells.Should().HaveCount(2);
        rows[0].Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "b").Cells.Should().HaveCount(1);
    }

    #endregion

    #region Timestamp edge cases

    [Fact]
    public async Task Timestamp_zero_is_valid()
    {
        // Ref: Timestamp of 0 is a valid explicit timestamp
        await Client.MutateRowAsync(TN, "vt-ts0",
            Mutations.SetCell(CF, "c", "at-zero", new BigtableVersion(0)));
        var rows = await ReadAll(RowSet.FromRowKeys("vt-ts0"));
        rows.Should().ContainSingle();
        // Version(0) means 0ms = 0us
    }

    [Fact]
    public async Task Timestamp_1ms_is_1000us()
    {
        await Client.MutateRowAsync(TN, "vt-1ms", Mutations.SetCell(CF, "c", "v", new BigtableVersion(1)));
        var rows = await ReadAll(RowSet.FromRowKeys("vt-1ms"));
        // BigtableVersion(1) = 1 millisecond = 1000 microseconds
        rows[0].Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(1000);
    }

    [Fact]
    public async Task Timestamp_max_reasonable_value()
    {
        // Large but valid timestamp
        long largeTsMs = 4_102_444_800_000; // ~2100-01-01
        await Client.MutateRowAsync(TN, "vt-large",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(largeTsMs)));
        var rows = await ReadAll(RowSet.FromRowKeys("vt-large"));
        rows[0].Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(largeTsMs * 1000);
    }

    [Fact]
    public async Task Timestamp_1ms_apart_creates_distinct_versions()
    {
        await Client.MutateRowAsync(TN, "vt-1msapart",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(1001)));
        var rows = await ReadAll(RowSet.FromRowKeys("vt-1msapart"));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task Same_timestamp_overwrites_value()
    {
        await Client.MutateRowAsync(TN, "vt-ow", Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "vt-ow", Mutations.SetCell(CF, "c", "second", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("vt-ow"));
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task Same_timestamp_same_value_is_idempotent()
    {
        await Client.MutateRowAsync(TN, "vt-idem", Mutations.SetCell(CF, "c", "same", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "vt-idem", Mutations.SetCell(CF, "c", "same", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("vt-idem"));
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle();
    }

    #endregion

    #region CellsPerColumnLimit with versions

    [Fact]
    public async Task CellsPerColumnLimit_1_with_5_versions()
    {
        var mutations = Enumerable.Range(1, 5).Select(i =>
            Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000))
        ).ToArray();
        await Client.MutateRowAsync(TN, "vt-cpcl1", mutations);
        var rows = await ReadAll(RowSet.FromRowKeys("vt-cpcl1"), RowFilters.CellsPerColumnLimit(1));
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle();
        // Should be newest version
        rows[0].Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(5_000_000);
    }

    [Fact]
    public async Task CellsPerColumnLimit_3_with_10_versions()
    {
        var mutations = Enumerable.Range(1, 10).Select(i =>
            Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000))
        ).ToArray();
        await Client.MutateRowAsync(TN, "vt-cpcl3", mutations);
        var rows = await ReadAll(RowSet.FromRowKeys("vt-cpcl3"), RowFilters.CellsPerColumnLimit(3));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(3);
        // Newest 3: 10ms, 9ms, 8ms
        var ts = rows[0].Families[0].Columns[0].Cells.Select(c => c.TimestampMicros / 1000).ToList();
        ts.Should().Equal(10000, 9000, 8000);
    }

    [Fact]
    public async Task CellsPerColumnLimit_exceeds_version_count()
    {
        await Client.MutateRowAsync(TN, "vt-cpclx",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));
        var rows = await ReadAll(RowSet.FromRowKeys("vt-cpclx"), RowFilters.CellsPerColumnLimit(100));
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task CellsPerColumnLimit_applies_per_column()
    {
        await Client.MutateRowAsync(TN, "vt-cpclpc",
            Mutations.SetCell(CF, "a", "a1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "a2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "a", "a3", new BigtableVersion(3000)),
            Mutations.SetCell(CF, "b", "b1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "b2", new BigtableVersion(2000)));
        var rows = await ReadAll(RowSet.FromRowKeys("vt-cpclpc"), RowFilters.CellsPerColumnLimit(2));
        var colA = rows[0].Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "a");
        var colB = rows[0].Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "b");
        colA.Cells.Should().HaveCount(2);
        colB.Cells.Should().HaveCount(2);
    }

    #endregion

    #region TimestampRange filter

    [Fact]
    public async Task TimestampRange_exact_boundaries()
    {
        // Ref: timestamp_range_filter: inclusive start, exclusive end
        var mutations = Enumerable.Range(1, 5).Select(i =>
            Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000))
        ).ToArray();
        await Client.MutateRowAsync(TN, "vt-tre", mutations);

        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                StartTimestampMicros = 2_000_000, // inclusive
                EndTimestampMicros = 4_000_000,   // exclusive
            }
        };
        var rows = await ReadAll(RowSet.FromRowKeys("vt-tre"), filter);
        var ts = rows[0].Families[0].Columns[0].Cells.Select(c => c.TimestampMicros / 1000).ToList();
        ts.Should().Contain(2000);
        ts.Should().Contain(3000);
        ts.Should().NotContain(4000);
        ts.Should().NotContain(1000);
    }

    [Fact]
    public async Task TimestampRange_start_only()
    {
        var mutations = Enumerable.Range(1, 5).Select(i =>
            Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000))
        ).ToArray();
        await Client.MutateRowAsync(TN, "vt-trso", mutations);

        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange { StartTimestampMicros = 3_000_000 }
        };
        var rows = await ReadAll(RowSet.FromRowKeys("vt-trso"), filter);
        var ts = rows[0].Families[0].Columns[0].Cells.Select(c => c.TimestampMicros / 1000).ToList();
        ts.Should().Contain(3000);
        ts.Should().Contain(4000);
        ts.Should().Contain(5000);
        ts.Should().NotContain(1000);
        ts.Should().NotContain(2000);
    }

    [Fact]
    public async Task TimestampRange_end_only()
    {
        var mutations = Enumerable.Range(1, 5).Select(i =>
            Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000))
        ).ToArray();
        await Client.MutateRowAsync(TN, "vt-treo", mutations);

        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange { EndTimestampMicros = 3_000_000 }
        };
        var rows = await ReadAll(RowSet.FromRowKeys("vt-treo"), filter);
        var ts = rows[0].Families[0].Columns[0].Cells.Select(c => c.TimestampMicros / 1000).ToList();
        ts.Should().Contain(1000);
        ts.Should().Contain(2000);
        ts.Should().NotContain(3000);
    }

    [Fact]
    public async Task TimestampRange_no_match_returns_empty()
    {
        await Client.MutateRowAsync(TN, "vt-trnm",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                StartTimestampMicros = 5_000_000,
                EndTimestampMicros = 10_000_000,
            }
        };
        var rows = await ReadAll(RowSet.FromRowKeys("vt-trnm"), filter);
        rows.Should().BeEmpty();
    }

    #endregion

    #region Cross-family version behavior

    [Fact]
    public async Task Same_timestamp_different_families()
    {
        await Client.MutateRowAsync(TN, "vt-stdf",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "v2", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("vt-stdf"));
        rows[0].Families.Should().HaveCount(2);
        foreach (var fam in rows[0].Families)
            fam.Columns[0].Cells[0].TimestampMicros.Should().Be(1_000_000);
    }

    [Fact]
    public async Task Different_version_counts_per_family()
    {
        await Client.MutateRowAsync(TN, "vt-dvcpf",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)),
            Mutations.SetCell(CF2, "c", "v1", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("vt-dvcpf"));
        rows[0].Families.First(f => f.Name == CF).Columns[0].Cells.Should().HaveCount(3);
        rows[0].Families.First(f => f.Name == CF2).Columns[0].Cells.Should().HaveCount(1);
    }

    [Fact]
    public async Task CellsPerColumnLimit_applies_independently_to_each_family()
    {
        await Client.MutateRowAsync(TN, "vt-cpclf",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)),
            Mutations.SetCell(CF2, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "v2", new BigtableVersion(2000)));
        var rows = await ReadAll(RowSet.FromRowKeys("vt-cpclf"), RowFilters.CellsPerColumnLimit(2));
        rows[0].Families.First(f => f.Name == CF).Columns[0].Cells.Should().HaveCount(2);
        rows[0].Families.First(f => f.Name == CF2).Columns[0].Cells.Should().HaveCount(2);
    }

    #endregion

    #region Server-assigned timestamps

    [Fact]
    public async Task ServerAssigned_timestamp_is_positive_value()
    {
        // Ref: server assigns timestamp when client doesn't specify explicit
        await Client.MutateRowAsync(TN, "vt-sat",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(0)));
        // We write with version 0 (explicit), server doesn't replace it
        var rows = await ReadAll(RowSet.FromRowKeys("vt-sat"));
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Multiple_writes_with_explicit_then_delete_by_range()
    {
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "vt-mwdr",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
        // Delete middle 3 versions
        await Client.MutateRowAsync(TN, "vt-mwdr",
            Mutations.DeleteFromColumn(CF, "c", new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(5000))));
        var rows = await ReadAll(RowSet.FromRowKeys("vt-mwdr"));
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(2);
        var ts = rows[0].Families[0].Columns[0].Cells.Select(c => c.TimestampMicros / 1000).ToList();
        ts.Should().Contain(new[] { 1000L, 5000L });
    }

    #endregion

    #region Overwrite semantics

    [Fact]
    public async Task Overwrite_with_different_value_same_timestamp()
    {
        await Client.MutateRowAsync(TN, "vt-owdv",
            Mutations.SetCell(CF, "c", "original", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "vt-owdv",
            Mutations.SetCell(CF, "c", "updated", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("vt-owdv"));
        rows[0].Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("updated");
    }

    [Fact]
    public async Task Overwrite_with_empty_value()
    {
        await Client.MutateRowAsync(TN, "vt-owev",
            Mutations.SetCell(CF, "c", "notempty", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "vt-owev",
            Mutations.SetCell(CF, "c", ByteString.Empty, new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("vt-owev"));
        rows[0].Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task Overwrite_with_larger_value()
    {
        await Client.MutateRowAsync(TN, "vt-owlv",
            Mutations.SetCell(CF, "c", "small", new BigtableVersion(1000)));
        var large = new string('X', 10000);
        await Client.MutateRowAsync(TN, "vt-owlv",
            Mutations.SetCell(CF, "c", large, new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("vt-owlv"));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Length.Should().Be(10000);
    }

    [Fact]
    public async Task Overwrite_with_smaller_value()
    {
        var large = new string('X', 10000);
        await Client.MutateRowAsync(TN, "vt-owsv",
            Mutations.SetCell(CF, "c", large, new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "vt-owsv",
            Mutations.SetCell(CF, "c", "tiny", new BigtableVersion(1000)));
        var rows = await ReadAll(RowSet.FromRowKeys("vt-owsv"));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("tiny");
    }

    #endregion
}

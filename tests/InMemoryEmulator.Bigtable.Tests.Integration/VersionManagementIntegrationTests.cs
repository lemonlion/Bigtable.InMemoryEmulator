using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Comprehensive version management integration tests — multiple versions per cell,
/// ordering, overwrites, read-back precision, and GC interaction.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#cell
///   "Cells are returned in descending timestamp order."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class VersionManagementIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "version-tests";
    private const string CF = "cf";

    public VersionManagementIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    #region Multiple versions

    [Fact]
    public async Task Write_10_versions_all_returned()
    {
        var rk = new BigtableByteString("ver-10");
        for (int i = 1; i <= 10; i++)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
        }
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells.Should().HaveCount(10);
    }

    [Fact]
    public async Task Versions_returned_in_descending_timestamp_order()
    {
        // Ref: "Cells are returned in descending timestamp order."
        var rk = new BigtableByteString("ver-order");
        var timestamps = new[] { 5000L, 1000L, 3000L, 7000L, 2000L };
        foreach (var ts in timestamps)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, "c", $"t{ts}", new BigtableVersion(ts)));
        }
        var row = await Client.ReadRowAsync(TN, rk);
        var cellTimestamps = row!.Families[0].Columns[0].Cells
            .Select(c => c.TimestampMicros).ToList();
        cellTimestamps.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Same_timestamp_overwrites_value()
    {
        var rk = new BigtableByteString("ver-overwrite");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "original", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "updated", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("updated");
    }

    [Fact]
    public async Task Interleaved_timestamp_writes_maintain_order()
    {
        // Write timestamps in non-sequential order
        var rk = new BigtableByteString("ver-interleave");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "v5", new BigtableVersion(5000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "v4", new BigtableVersion(4000)));

        var row = await Client.ReadRowAsync(TN, rk);
        var values = row!.Families[0].Columns[0].Cells
            .Select(c => c.Value.ToStringUtf8()).ToList();
        values.Should().Equal("v5", "v4", "v3", "v2", "v1");
    }

    #endregion

    #region Version-specific operations

    [Fact]
    public async Task Delete_specific_version_by_timestamp()
    {
        var rk = new BigtableByteString("ver-del-ts");
        for (int i = 1; i <= 3; i++)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
        }

        // Delete the middle version (timestamp 2000)
        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(2001))));

        var row = await Client.ReadRowAsync(TN, rk);
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2);
        cells.Select(c => c.TimestampMicros).Should().NotContain(2000_000);
    }

    [Fact]
    public async Task Delete_version_range()
    {
        var rk = new BigtableByteString("ver-del-range");
        for (int i = 1; i <= 5; i++)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
        }

        // Delete versions with timestamps [2000, 4000) — versions at ts 2000 and 3000
        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(4000))));

        var row = await Client.ReadRowAsync(TN, rk);
        var timestamps = row!.Families[0].Columns[0].Cells
            .Select(c => c.TimestampMicros).ToList();
        timestamps.Should().Contain(1000_000); // ts 1000 (in micros)
        timestamps.Should().Contain(4000_000);
        timestamps.Should().Contain(5000_000);
        timestamps.Should().NotContain(2000_000);
        timestamps.Should().NotContain(3000_000);
    }

    [Fact]
    public async Task Delete_all_versions_of_column()
    {
        var rk = new BigtableByteString("ver-del-all");
        for (int i = 1; i <= 3; i++)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
        }
        // Also write another column to keep the row alive
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "other", "keeper", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "c"));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        var cols = row!.Families[0].Columns;
        cols.Should().NotContain(c => c.Qualifier.ToStringUtf8() == "c");
        cols.Should().Contain(c => c.Qualifier.ToStringUtf8() == "other");
    }

    #endregion

    #region Timestamp precision

    [Fact]
    public async Task Timestamp_millisecond_precision()
    {
        var rk = new BigtableByteString("ver-ts-ms");
        // BigtableVersion(1000) → 1000ms → 1_000_000 micros
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "val", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(1_000_000);
    }

    [Fact]
    public async Task Timestamp_large_value_preserved()
    {
        var rk = new BigtableByteString("ver-ts-large");
        // Timestamp for 2024-01-01 UTC in ms
        var jan2024Ms = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "val", new BigtableVersion(jan2024Ms)));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(jan2024Ms * 1000);
    }

    [Fact]
    public async Task Timestamp_zero_stored_correctly()
    {
        // Ref: Timestamp 0 is a valid explicit timestamp (only -1 means server-assigned)
        var rk = new BigtableByteString("ver-ts-0");
        var request = new MutateRowRequest
        {
            TableName = TN.ToString(),
            RowKey = ByteString.CopyFromUtf8("ver-ts-0"),
            Mutations =
            {
                new Mutation
                {
                    SetCell = new Mutation.Types.SetCell
                    {
                        FamilyName = CF,
                        ColumnQualifier = ByteString.CopyFromUtf8("c"),
                        Value = ByteString.CopyFromUtf8("val"),
                        TimestampMicros = 0,
                    }
                }
            }
        };
        await _fixture.ServiceApiClient.MutateRowAsync(request);
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(0);
    }

    [Fact]
    public async Task Close_timestamps_are_distinct_versions()
    {
        // Timestamps 1ms apart should be distinct versions
        var rk = new BigtableByteString("ver-close");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(1001)));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    #endregion

    #region Version interactions with filters

    [Fact]
    public async Task CellsPerColumnLimit_returns_newest_N()
    {
        var rk = new BigtableByteString("ver-cpcl");
        for (int i = 1; i <= 5; i++)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
        }
        var filter = RowFilters.CellsPerColumnLimit(2);
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys(rk), filter: filter))
        {
            rows.Add(row);
        }
        rows.Should().ContainSingle();
        var cells = rows[0].Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2);
        cells[0].Value.ToStringUtf8().Should().Be("v5"); // newest
        cells[1].Value.ToStringUtf8().Should().Be("v4");
    }

    [Fact]
    public async Task TimestampRange_filters_versions()
    {
        var rk = new BigtableByteString("ver-tsrange");
        for (int i = 1; i <= 5; i++)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
        }
        // Filter: timestamps [2000ms, 4000ms) in micros → [2_000_000, 4_000_000)
        var filter = RowFilters.TimestampRange(
            new DateTime(1970, 1, 1, 0, 0, 2, DateTimeKind.Utc),
            new DateTime(1970, 1, 1, 0, 0, 4, DateTimeKind.Utc));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys(rk), filter: filter))
        {
            rows.Add(row);
        }
        rows.Should().ContainSingle();
        var cells = rows[0].Families[0].Columns[0].Cells;
        // Timestamps 2000ms (2_000_000μs) and 3000ms (3_000_000μs) should be included
        // 4000ms is exclusive
        cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task ValueRegex_with_multiple_versions()
    {
        var rk = new BigtableByteString("ver-vregex");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "match-yes", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "no", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "match-also", new BigtableVersion(3000)));

        var filter = RowFilters.ValueRegex("match.*");
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys(rk), filter: filter))
        {
            rows.Add(row);
        }
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    #endregion

    #region Write-then-read consistency

    [Fact]
    public async Task Write_and_immediate_read_consistent()
    {
        var rk = new BigtableByteString("ver-wnr");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "val", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("val");
    }

    [Fact]
    public async Task Overwrite_and_read_returns_latest()
    {
        var rk = new BigtableByteString("ver-owlr");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Delete_then_write_creates_new_version()
    {
        var rk = new BigtableByteString("ver-dw");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "c"));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v2");
    }

    [Fact]
    public async Task Delete_row_then_write_creates_fresh_row()
    {
        var rk = new BigtableByteString("ver-drw");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromRow());
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v2");
    }

    #endregion

    #region Multiple columns with versions

    [Fact]
    public async Task Multiple_columns_each_with_versions()
    {
        var rk = new BigtableByteString("ver-multicol");
        for (int i = 1; i <= 3; i++)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, "a", $"a{i}", new BigtableVersion(i * 1000)),
                Mutations.SetCell(CF, "b", $"b{i}", new BigtableVersion(i * 1000)));
        }
        var row = await Client.ReadRowAsync(TN, rk);
        var family = row!.Families[0];
        family.Columns.Should().HaveCount(2);
        foreach (var col in family.Columns)
        {
            col.Cells.Should().HaveCount(3);
            col.Cells.Select(c => c.TimestampMicros).Should().BeInDescendingOrder();
        }
    }

    [Fact]
    public async Task Columns_ordered_lexicographically()
    {
        // Ref: "Columns within a family are ordered lexicographically by qualifier"
        var rk = new BigtableByteString("ver-colorder");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "z", "vz", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "va", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "m", "vm", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, rk);
        var qualifiers = row!.Families[0].Columns
            .Select(c => c.Qualifier.ToStringUtf8()).ToList();
        qualifiers.Should().BeInAscendingOrder();
    }

    #endregion
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Systematic tests for timestamp/version management — creation, ordering,
/// reading, and deletion with version ranges.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#cell
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class TimestampVersionMatrixTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "tvm-tests";
    private const string CF = "cf";

    public TimestampVersionMatrixTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Single_version_readable()
    {
        await Client.MutateRowAsync(TN, "tvm-single",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "tvm-single");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
    }

    [Fact]
    public async Task Multiple_versions_stored_descending()
    {
        await Client.MutateRowAsync(TN, "tvm-multi",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "tvm-multi",
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, "tvm-multi",
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));

        var row = await Client.ReadRowAsync(TN, "tvm-multi");
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(3);
        // Cells returned in descending timestamp order
        cells[0].TimestampMicros.Should().BeGreaterThan(cells[1].TimestampMicros);
        cells[1].TimestampMicros.Should().BeGreaterThan(cells[2].TimestampMicros);
    }

    [Fact]
    public async Task Same_timestamp_overwrites_value()
    {
        await Client.MutateRowAsync(TN, "tvm-overwrite",
            Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "tvm-overwrite",
            Mutations.SetCell(CF, "c", "second", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "tvm-overwrite");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task Timestamp_microseconds_precision()
    {
        // BigtableVersion(1) = 1 millisecond = 1000 microseconds
        await Client.MutateRowAsync(TN, "tvm-precision",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1)));

        var row = await Client.ReadRowAsync(TN, "tvm-precision");
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(1000);
    }

    [Fact]
    public async Task Large_timestamp_value()
    {
        var ts = new BigtableVersion(999999999);
        await Client.MutateRowAsync(TN, "tvm-large-ts",
            Mutations.SetCell(CF, "c", "v", ts));

        var row = await Client.ReadRowAsync(TN, "tvm-large-ts");
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(999999999L * 1000);
    }

    [Fact]
    public async Task Timestamp_from_datetime()
    {
        var dt = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        await Client.MutateRowAsync(TN, "tvm-datetime",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(dt)));

        var row = await Client.ReadRowAsync(TN, "tvm-datetime");
        var cell = row!.Families[0].Columns[0].Cells[0];
        cell.TimestampMicros.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CellsPerColumnLimit_1_returns_latest()
    {
        await Client.MutateRowAsync(TN, "tvm-cpcl",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "tvm-cpcl",
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.CellsPerColumnLimit(1),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("tvm-cpcl") } }
        };
        await foreach (var row in Client.ReadRows(request))
            row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task TimestampRange_filter_includes_matching()
    {
        var ts1 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var ts2 = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var ts3 = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc);

        await Client.MutateRowAsync(TN, "tvm-tsfilter",
            Mutations.SetCell(CF, "c", "jan", new BigtableVersion(ts1)));
        await Client.MutateRowAsync(TN, "tvm-tsfilter",
            Mutations.SetCell(CF, "c", "jun", new BigtableVersion(ts2)));
        await Client.MutateRowAsync(TN, "tvm-tsfilter",
            Mutations.SetCell(CF, "c", "dec", new BigtableVersion(ts3)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.TimestampRange(ts1, ts2.AddDays(1)),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("tvm-tsfilter") } }
        };
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                vals.Add(cell.Value.ToStringUtf8());

        vals.Should().Contain("jan");
        vals.Should().Contain("jun");
        vals.Should().NotContain("dec");
    }

    [Fact]
    public async Task Delete_specific_version_keeps_others()
    {
        await Client.MutateRowAsync(TN, "tvm-delver",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "tvm-delver",
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, "tvm-delver",
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));

        // Delete version range: [2000, 3000) = deletes v2 only
        await Client.MutateRowAsync(TN, "tvm-delver",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(3000))));

        var row = await Client.ReadRowAsync(TN, "tvm-delver");
        var vals = row!.Families[0].Columns[0].Cells.Select(c => c.Value.ToStringUtf8()).ToList();
        vals.Should().Contain("v1");
        vals.Should().Contain("v3");
        vals.Should().NotContain("v2");
    }

    [Fact]
    public async Task Delete_all_versions_of_column()
    {
        await Client.MutateRowAsync(TN, "tvm-delall",
            Mutations.SetCell(CF, "target", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "tvm-delall",
            Mutations.SetCell(CF, "target", "v2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, "tvm-delall",
            Mutations.SetCell(CF, "keep", "v", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "tvm-delall",
            Mutations.DeleteFromColumn(CF, "target"));

        var row = await Client.ReadRowAsync(TN, "tvm-delall");
        row!.Families[0].Columns.Should().HaveCount(1);
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("keep");
    }

    [Fact]
    public async Task Five_versions_with_cells_per_column_2()
    {
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "tvm-5ver",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.CellsPerColumnLimit(2),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("tvm-5ver") } }
        };
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                vals.Add(cell.Value.ToStringUtf8());

        vals.Should().HaveCount(2);
        vals.Should().Contain("v5");
        vals.Should().Contain("v4");
    }

    [Fact]
    public async Task Version_per_column_independent()
    {
        await Client.MutateRowAsync(TN, "tvm-indver",
            Mutations.SetCell(CF, "a", "a1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "tvm-indver",
            Mutations.SetCell(CF, "a", "a2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, "tvm-indver",
            Mutations.SetCell(CF, "b", "b1", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "tvm-indver");
        var colA = row!.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "a");
        var colB = row.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "b");
        colA.Cells.Should().HaveCount(2);
        colB.Cells.Should().HaveCount(1);
    }

    [Fact]
    public async Task Overwrite_with_same_timestamp_across_columns()
    {
        await Client.MutateRowAsync(TN, "tvm-samets",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "tvm-samets",
            Mutations.SetCell(CF, "a", "1-new", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2-new", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "tvm-samets");
        foreach (var col in row!.Families[0].Columns)
        {
            col.Cells.Should().HaveCount(1);
            col.Cells[0].Value.ToStringUtf8().Should().EndWith("-new");
        }
    }

    [Fact]
    public async Task Insert_versions_out_of_order()
    {
        await Client.MutateRowAsync(TN, "tvm-ooo",
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        await Client.MutateRowAsync(TN, "tvm-ooo",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "tvm-ooo",
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, "tvm-ooo");
        var cells = row!.Families[0].Columns[0].Cells;
        cells[0].Value.ToStringUtf8().Should().Be("v3");
        cells[1].Value.ToStringUtf8().Should().Be("v2");
        cells[2].Value.ToStringUtf8().Should().Be("v1");
    }

    [Fact]
    public async Task Delete_version_range_then_set_new()
    {
        await Client.MutateRowAsync(TN, "tvm-delreset",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "tvm-delreset",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(1000), new BigtableVersion(2000))));

        await Client.MutateRowAsync(TN, "tvm-delreset",
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(3000)));

        var row = await Client.ReadRowAsync(TN, "tvm-delreset");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Ten_versions_stored_and_readable()
    {
        for (int i = 1; i <= 10; i++)
            await Client.MutateRowAsync(TN, "tvm-10ver",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));

        var row = await Client.ReadRowAsync(TN, "tvm-10ver");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(10);
    }

    [Fact]
    public async Task Timestamp_range_filter_no_start()
    {
        var ts = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        await Client.MutateRowAsync(TN, "tvm-nostart",
            Mutations.SetCell(CF, "c", "early", new BigtableVersion(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc))));
        await Client.MutateRowAsync(TN, "tvm-nostart",
            Mutations.SetCell(CF, "c", "late", new BigtableVersion(new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc))));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.TimestampRange(null, ts),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("tvm-nostart") } }
        };
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                vals.Add(cell.Value.ToStringUtf8());

        vals.Should().Contain("early");
        vals.Should().NotContain("late");
    }

    [Fact]
    public async Task Timestamp_range_filter_no_end()
    {
        var ts = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        await Client.MutateRowAsync(TN, "tvm-noend",
            Mutations.SetCell(CF, "c", "early", new BigtableVersion(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc))));
        await Client.MutateRowAsync(TN, "tvm-noend",
            Mutations.SetCell(CF, "c", "late", new BigtableVersion(new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc))));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.TimestampRange(ts, null),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("tvm-noend") } }
        };
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                vals.Add(cell.Value.ToStringUtf8());

        vals.Should().Contain("late");
        vals.Should().NotContain("early");
    }

    [Fact]
    public async Task CellsPerRowOffset_skips_newest_cells()
    {
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "tvm-offset",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.CellsPerRowOffset(2),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("tvm-offset") } }
        };
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                vals.Add(cell.Value.ToStringUtf8());

        vals.Should().HaveCount(3); // v3, v2, v1
        vals.Should().NotContain("v5");
        vals.Should().NotContain("v4");
    }

    [Fact]
    public async Task Delete_from_family_removes_all_versions()
    {
        await Client.MutateRowAsync(TN, "tvm-delfam",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "tvm-delfam",
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, "tvm-delfam",
            Mutations.SetCell("cf2", "d", "keep", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "tvm-delfam",
            Mutations.DeleteFromFamily(CF));

        var row = await Client.ReadRowAsync(TN, "tvm-delfam");
        row!.Families.Should().HaveCount(1);
        row.Families[0].Name.Should().Be("cf2");
    }

    [Fact]
    public async Task Version_timestamp_with_batch_mutations()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("tvm-batch",
                Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)))
        };

        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "tvm-batch");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
    }
}

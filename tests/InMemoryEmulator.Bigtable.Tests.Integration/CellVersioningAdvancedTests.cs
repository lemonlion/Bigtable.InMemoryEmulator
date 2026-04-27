using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for cell versioning — multiple versions per cell, ordering,
/// timestamp precision, and interactions with reads.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#cell
///   "Cells are returned in descending timestamp order."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CellVersioningAdvancedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "cell-ver-adv";
    private const string CF = "cf";

    public CellVersioningAdvancedTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task Single_version_returns_one_cell()
    {
        var rk = new BigtableByteString("cv-single");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "val", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
    }

    // Ref: "Cells are returned in descending timestamp order."
    [Fact]
    public async Task Two_versions_returned_in_descending_order()
    {
        var rk = new BigtableByteString("cv-2ver");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "new", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, rk);
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2);
        cells[0].Value.ToStringUtf8().Should().Be("new");
        cells[1].Value.ToStringUtf8().Should().Be("old");
    }

    [Fact]
    public async Task Five_versions_all_returned()
    {
        var rk = new BigtableByteString("cv-5ver");
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, "col", $"v{i}", new BigtableVersion(i * 1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(5);
        // Descending order: v5, v4, v3, v2, v1
        cells[0].Value.ToStringUtf8().Should().Be("v5");
        cells[4].Value.ToStringUtf8().Should().Be("v1");
    }

    [Fact]
    public async Task Same_timestamp_overwrites_value()
    {
        var rk = new BigtableByteString("cv-overwrite");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "first", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "second", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(1);
        cells[0].Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task Versions_written_out_of_order_still_sorted()
    {
        var rk = new BigtableByteString("cv-ooo");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v3", new BigtableVersion(3000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v2", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, rk);
        var cells = row!.Families[0].Columns[0].Cells;
        cells[0].Value.ToStringUtf8().Should().Be("v3");
        cells[1].Value.ToStringUtf8().Should().Be("v2");
        cells[2].Value.ToStringUtf8().Should().Be("v1");
    }

    [Fact]
    public async Task Multiple_versions_in_single_mutation()
    {
        var rk = new BigtableByteString("cv-singlemut");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "col", "v3", new BigtableVersion(3000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task CellsPerColumnLimit_1_returns_latest()
    {
        var rk = new BigtableByteString("cv-cpcl1");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "new", new BigtableVersion(2000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("cv-cpcl1") } },
            Filter = RowFilters.CellsPerColumnLimit(1)
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(1);
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task CellsPerColumnLimit_2_returns_two_latest()
    {
        var rk = new BigtableByteString("cv-cpcl2");
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, "col", $"v{i}", new BigtableVersion(i * 1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("cv-cpcl2") } },
            Filter = RowFilters.CellsPerColumnLimit(2)
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        var cells = rows[0].Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2);
        cells[0].Value.ToStringUtf8().Should().Be("v5");
        cells[1].Value.ToStringUtf8().Should().Be("v4");
    }

    [Fact]
    public async Task TimestampRange_filters_versions()
    {
        var rk = new BigtableByteString("cv-tsfilter");
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, "col", $"v{i}", new BigtableVersion(i * 1000)));

        // Get versions 2000ms and 3000ms (inclusive start, exclusive end)
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("cv-tsfilter") } },
            Filter = RowFilters.TimestampRange(
                new DateTime(1970, 1, 1, 0, 0, 2, DateTimeKind.Utc),
                new DateTime(1970, 1, 1, 0, 0, 4, DateTimeKind.Utc))
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        var cells = rows[0].Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2);
        cells.Select(c => c.Value.ToStringUtf8()).Should().BeEquivalentTo(new[] { "v3", "v2" });
    }

    [Fact]
    public async Task Different_columns_have_independent_versions()
    {
        var rk = new BigtableByteString("cv-indep");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "a1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "a2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "b", "b1", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        var colA = row!.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "a");
        var colB = row.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "b");
        colA.Cells.Should().HaveCount(2);
        colB.Cells.Should().HaveCount(1);
    }

    [Fact]
    public async Task Version_timestamp_is_preserved_exactly()
    {
        var rk = new BigtableByteString("cv-tspres");
        var ts = new BigtableVersion(12345000); // 12345 seconds in microseconds
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "val", ts));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(12345000L * 1000);
    }

    [Fact]
    public async Task Ten_versions_all_returned_in_order()
    {
        var rk = new BigtableByteString("cv-10ver");
        for (int i = 1; i <= 10; i++)
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, "col", $"v{i:D2}", new BigtableVersion(i * 1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(10);
        cells[0].Value.ToStringUtf8().Should().Be("v10");
        cells[9].Value.ToStringUtf8().Should().Be("v01");
    }

    [Fact]
    public async Task Empty_value_with_timestamp_is_valid_cell()
    {
        var rk = new BigtableByteString("cv-empty");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().BeEmpty();
    }

    [Fact]
    public async Task Large_value_stored_correctly()
    {
        var rk = new BigtableByteString("cv-large");
        var largeVal = new string('x', 10000);
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", largeVal, new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be(largeVal);
    }

    [Fact]
    public async Task Binary_value_round_trips()
    {
        var rk = new BigtableByteString("cv-binary");
        var bytes = new byte[256];
        for (int i = 0; i < 256; i++) bytes[i] = (byte)i;

        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, ByteString.CopyFromUtf8("col"),
                ByteString.CopyFrom(bytes), new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells[0].Value.ToByteArray().Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public async Task Delete_specific_version_preserves_others()
    {
        var rk = new BigtableByteString("cv-delver");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "keep1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "delete", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "keep2", new BigtableVersion(3000)));

        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "col",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(2001))));

        var row = await Client.ReadRowAsync(TN, rk);
        var values = row!.Families[0].Columns[0].Cells.Select(c => c.Value.ToStringUtf8()).ToList();
        values.Should().BeEquivalentTo(new[] { "keep2", "keep1" });
    }

    [Fact]
    public async Task Timestamp_microseconds_granularity()
    {
        // BigtableVersion(1) = 1 millisecond = 1000 microseconds
        var rk = new BigtableByteString("cv-micro");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "val", new BigtableVersion(1)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(1000);
    }
}

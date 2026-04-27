using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for multi-version CRUD patterns — create, read, update, delete
/// across multiple cell versions.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#cell
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MultiVersionCrudTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "mvc-tests";
    private const string CF = "cf";

    public MultiVersionCrudTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Create_read_update_delete_single_column()
    {
        // Create
        await Client.MutateRowAsync(TN, "mvc-crud",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));

        // Read
        var row = await Client.ReadRowAsync(TN, "mvc-crud");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v1");

        // Update (new version)
        await Client.MutateRowAsync(TN, "mvc-crud",
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));

        row = await Client.ReadRowAsync(TN, "mvc-crud");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);

        // Delete
        await Client.MutateRowAsync(TN, "mvc-crud", Mutations.DeleteFromColumn(CF, "c"));
        row = await Client.ReadRowAsync(TN, "mvc-crud");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Overwrite_same_version_replaces_value()
    {
        await Client.MutateRowAsync(TN, "mvc-overwrite",
            Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "mvc-overwrite",
            Mutations.SetCell(CF, "c", "second", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "mvc-overwrite");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task Multiple_versions_read_with_limit()
    {
        for (int i = 1; i <= 10; i++)
            await Client.MutateRowAsync(TN, "mvc-mvl",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.CellsPerColumnLimit(3),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("mvc-mvl") } }
        };
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                vals.Add(cell.Value.ToStringUtf8());

        vals.Should().HaveCount(3);
        vals.Should().Contain("v10");
        vals.Should().Contain("v9");
        vals.Should().Contain("v8");
    }

    [Fact]
    public async Task Delete_middle_version_keeps_edges()
    {
        await Client.MutateRowAsync(TN, "mvc-del-mid",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "mvc-del-mid",
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, "mvc-del-mid",
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));

        // Delete [2000, 3000) — removes v2
        await Client.MutateRowAsync(TN, "mvc-del-mid",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(3000))));

        var row = await Client.ReadRowAsync(TN, "mvc-del-mid");
        var vals = row!.Families[0].Columns[0].Cells.Select(c => c.Value.ToStringUtf8()).ToList();
        vals.Should().HaveCount(2);
        vals.Should().Contain("v1");
        vals.Should().Contain("v3");
    }

    [Fact]
    public async Task Multi_column_multi_version()
    {
        for (int col = 0; col < 3; col++)
            for (int ver = 1; ver <= 3; ver++)
                await Client.MutateRowAsync(TN, "mvc-mcmv",
                    Mutations.SetCell(CF, $"col{col}", $"v{ver}", new BigtableVersion(ver * 1000)));

        var row = await Client.ReadRowAsync(TN, "mvc-mcmv");
        row!.Families[0].Columns.Should().HaveCount(3);
        foreach (var c in row.Families[0].Columns)
            c.Cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task ReadModifyWrite_append_creates_new_version()
    {
        await Client.MutateRowAsync(TN, "mvc-rmw-append",
            Mutations.SetCell(CF, "c", "base", new BigtableVersion(1000)));

        await Client.ReadModifyWriteRowAsync(TN, "mvc-rmw-append",
            ReadModifyWriteRules.Append(CF, "c", "-appended"));

        var row = await Client.ReadRowAsync(TN, "mvc-rmw-append");
        // Should have the appended version
        var latestVal = row!.Families[0].Columns
            .First(c => c.Qualifier.ToStringUtf8() == "c")
            .Cells.OrderByDescending(c => c.TimestampMicros).First()
            .Value.ToStringUtf8();
        latestVal.Should().Contain("appended");
    }

    [Fact]
    public async Task ReadModifyWrite_increment_on_new_cell()
    {
        await Client.ReadModifyWriteRowAsync(TN, "mvc-rmw-inc",
            ReadModifyWriteRules.Increment(CF, "counter", 42));

        var row = await Client.ReadRowAsync(TN, "mvc-rmw-inc");
        var bytes = row!.Families[0].Columns[0].Cells[0].Value.ToByteArray();
        var val = BitConverter.ToInt64(bytes.Reverse().ToArray(), 0);
        val.Should().Be(42);
    }

    [Fact]
    public async Task Batch_multi_version_creation()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mvc-batch-mv",
                Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "mvc-batch-mv");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task Version_ordering_after_mixed_operations()
    {
        await Client.MutateRowAsync(TN, "mvc-mixed",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "mvc-mixed",
            Mutations.SetCell(CF, "c", "v5", new BigtableVersion(5000)));
        await Client.MutateRowAsync(TN, "mvc-mixed",
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));

        var row = await Client.ReadRowAsync(TN, "mvc-mixed");
        var cells = row!.Families[0].Columns[0].Cells;
        // Should be descending by timestamp
        cells[0].Value.ToStringUtf8().Should().Be("v5");
        cells[1].Value.ToStringUtf8().Should().Be("v3");
        cells[2].Value.ToStringUtf8().Should().Be("v1");
    }

    [Fact]
    public async Task Delete_then_write_same_column_new_version()
    {
        await Client.MutateRowAsync(TN, "mvc-delwrite",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "mvc-delwrite",
            Mutations.DeleteFromColumn(CF, "c"));

        await Client.MutateRowAsync(TN, "mvc-delwrite",
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(5000)));

        var row = await Client.ReadRowAsync(TN, "mvc-delwrite");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task CheckAndMutate_with_versions()
    {
        await Client.MutateRowAsync(TN, "mvc-cam",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "mvc-cam",
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "mvc-cam",
            RowFilters.Chain(
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueExact("v2")),
            trueMutations: new[] { Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)) });
        result.PredicateMatched.Should().BeTrue();

        var row = await Client.ReadRowAsync(TN, "mvc-cam");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task Multi_family_multi_version()
    {
        await Client.MutateRowAsync(TN, "mvc-mfmv",
            Mutations.SetCell(CF, "c", "cf-v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "cf2-v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "mvc-mfmv",
            Mutations.SetCell(CF, "c", "cf-v2", new BigtableVersion(2000)),
            Mutations.SetCell("cf2", "c", "cf2-v2", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, "mvc-mfmv");
        row!.Families.Should().HaveCount(2);
        foreach (var fam in row.Families)
            fam.Columns[0].Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task Delete_specific_family_keeps_other_versions()
    {
        await Client.MutateRowAsync(TN, "mvc-del-fam",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell("cf2", "d", "keep1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "d", "keep2", new BigtableVersion(2000)));

        await Client.MutateRowAsync(TN, "mvc-del-fam", Mutations.DeleteFromFamily(CF));

        var row = await Client.ReadRowAsync(TN, "mvc-del-fam");
        row!.Families.Should().HaveCount(1);
        row.Families[0].Name.Should().Be("cf2");
        row.Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task Timestamp_filter_across_versions()
    {
        var ts1 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var ts2 = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var ts3 = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc);

        await Client.MutateRowAsync(TN, "mvc-ts-filter",
            Mutations.SetCell(CF, "c", "jan", new BigtableVersion(ts1)));
        await Client.MutateRowAsync(TN, "mvc-ts-filter",
            Mutations.SetCell(CF, "c", "jun", new BigtableVersion(ts2)));
        await Client.MutateRowAsync(TN, "mvc-ts-filter",
            Mutations.SetCell(CF, "c", "dec", new BigtableVersion(ts3)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.TimestampRange(ts2, ts3),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("mvc-ts-filter") } }
        };
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                vals.Add(cell.Value.ToStringUtf8());

        vals.Should().HaveCount(1);
        vals[0].Should().Be("jun");
    }

    [Fact]
    public async Task Twenty_versions_all_readable()
    {
        for (int i = 1; i <= 20; i++)
            await Client.MutateRowAsync(TN, "mvc-20ver",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));

        var row = await Client.ReadRowAsync(TN, "mvc-20ver");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(20);
        // First cell should be latest (v20)
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v20");
    }
}

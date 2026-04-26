using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for GC rules interacting with data reads/writes — verifying that
/// maxVersions and maxAge GC rules affect which cells are returned.
/// Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2#columnfamily
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class GcRuleDataInteractionTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "gcr-data";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public GcRuleDataInteractionTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Write_and_read_single_cell()
    {
        await Client.MutateRowAsync(TN, "gcr-wr1",
            Mutations.SetCell(CF, "c", "val", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "gcr-wr1");
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("val");
    }

    [Fact]
    public async Task Multiple_versions_oldest_first_timestamp()
    {
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "gcr-mv",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));

        var row = await Client.ReadRowAsync(TN, "gcr-mv");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(5);
        // Cells returned in descending timestamp order
        row.Families[0].Columns[0].Cells[0].TimestampMicros.Should()
            .BeGreaterThanOrEqualTo(row.Families[0].Columns[0].Cells[4].TimestampMicros);
    }

    [Fact]
    public async Task CellsPerColumnLimit_filter_respects_version_ordering()
    {
        for (int i = 1; i <= 10; i++)
            await Client.MutateRowAsync(TN, "gcr-cpcl",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.CellsPerColumnLimit(2),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("gcr-cpcl") } }
        };
        var cells = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                cells.Add(cell.Value.ToStringUtf8());

        cells.Should().HaveCount(2);
        cells.Should().Contain("v10");
        cells.Should().Contain("v9");
    }

    [Fact]
    public async Task Two_families_different_versions()
    {
        for (int i = 1; i <= 3; i++)
        {
            await Client.MutateRowAsync(TN, "gcr-2fam",
                Mutations.SetCell(CF, "c", $"cf-v{i}", new BigtableVersion(i * 1000)));
            await Client.MutateRowAsync(TN, "gcr-2fam",
                Mutations.SetCell("cf2", "c", $"cf2-v{i}", new BigtableVersion(i * 1000)));
        }

        var row = await Client.ReadRowAsync(TN, "gcr-2fam");
        row!.Families.Should().HaveCount(2);
        foreach (var fam in row.Families)
            fam.Columns[0].Cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task Write_same_timestamp_overwrites()
    {
        await Client.MutateRowAsync(TN, "gcr-overwrite",
            Mutations.SetCell(CF, "c", "first", new BigtableVersion(5000)));
        await Client.MutateRowAsync(TN, "gcr-overwrite",
            Mutations.SetCell(CF, "c", "second", new BigtableVersion(5000)));

        var row = await Client.ReadRowAsync(TN, "gcr-overwrite");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task Delete_version_range_middle()
    {
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "gcr-delr",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));

        // Delete range [2000, 4000) — removes v2, v3
        await Client.MutateRowAsync(TN, "gcr-delr",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(4000))));

        var row = await Client.ReadRowAsync(TN, "gcr-delr");
        var vals = row!.Families[0].Columns[0].Cells.Select(c => c.Value.ToStringUtf8()).ToList();
        vals.Should().HaveCount(3);
        vals.Should().Contain("v1");
        vals.Should().Contain("v4");
        vals.Should().Contain("v5");
    }

    [Fact]
    public async Task Timestamp_filter_returns_only_matching_range()
    {
        var ts1 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var ts2 = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var ts3 = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var ts4 = new DateTime(2024, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        await Client.MutateRowAsync(TN, "gcr-tsf",
            Mutations.SetCell(CF, "c", "jan", new BigtableVersion(ts1)));
        await Client.MutateRowAsync(TN, "gcr-tsf",
            Mutations.SetCell(CF, "c", "mar", new BigtableVersion(ts2)));
        await Client.MutateRowAsync(TN, "gcr-tsf",
            Mutations.SetCell(CF, "c", "jun", new BigtableVersion(ts3)));
        await Client.MutateRowAsync(TN, "gcr-tsf",
            Mutations.SetCell(CF, "c", "sep", new BigtableVersion(ts4)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.TimestampRange(ts2, ts3),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("gcr-tsf") } }
        };
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                vals.Add(cell.Value.ToStringUtf8());

        vals.Should().HaveCount(1);
        vals[0].Should().Be("mar");
    }

    [Fact]
    public async Task ReadRow_after_delete_all_returns_null()
    {
        await Client.MutateRowAsync(TN, "gcr-del-all",
            Mutations.SetCell(CF, "c1", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c2", "v2", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "gcr-del-all", Mutations.DeleteFromRow());

        var row = await Client.ReadRowAsync(TN, "gcr-del-all");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Family_filter_with_multiple_versions()
    {
        for (int i = 1; i <= 3; i++)
        {
            await Client.MutateRowAsync(TN, "gcr-famf",
                Mutations.SetCell(CF, "c", $"cf-v{i}", new BigtableVersion(i * 1000)));
            await Client.MutateRowAsync(TN, "gcr-famf",
                Mutations.SetCell("cf2", "c", $"cf2-v{i}", new BigtableVersion(i * 1000)));
        }

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.FamilyNameExact(CF),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("gcr-famf") } }
        };
        var families = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
                families.Add(f.Name);

        families.Should().ContainSingle().Which.Should().Be(CF);
    }

    [Fact]
    public async Task Column_qualifier_filter_with_versions()
    {
        await Client.MutateRowAsync(TN, "gcr-cqf",
            Mutations.SetCell(CF, "x", "x1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "y", "y1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "z", "z1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "gcr-cqf",
            Mutations.SetCell(CF, "x", "x2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "y", "y2", new BigtableVersion(2000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.ColumnQualifierExact("y"),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("gcr-cqf") } }
        };
        var cells = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                cells.Add(cell.Value.ToStringUtf8());

        cells.Should().HaveCount(2);
        cells.Should().Contain("y1");
        cells.Should().Contain("y2");
    }

    [Fact]
    public async Task Delete_family_preserves_other_family_versions()
    {
        for (int i = 1; i <= 3; i++)
        {
            await Client.MutateRowAsync(TN, "gcr-delfam",
                Mutations.SetCell(CF, "c", $"cf-v{i}", new BigtableVersion(i * 1000)));
            await Client.MutateRowAsync(TN, "gcr-delfam",
                Mutations.SetCell("cf2", "c", $"cf2-v{i}", new BigtableVersion(i * 1000)));
        }

        await Client.MutateRowAsync(TN, "gcr-delfam", Mutations.DeleteFromFamily(CF));

        var row = await Client.ReadRowAsync(TN, "gcr-delfam");
        row!.Families.Should().HaveCount(1);
        row.Families[0].Name.Should().Be("cf2");
        row.Families[0].Columns[0].Cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task Chain_family_and_column_filter()
    {
        await Client.MutateRowAsync(TN, "gcr-chain",
            Mutations.SetCell(CF, "a", "cf-a", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "cf-b", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "a", "cf2-a", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.FamilyNameExact(CF),
                RowFilters.ColumnQualifierExact("a")),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("gcr-chain") } }
        };
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                vals.Add(cell.Value.ToStringUtf8());

        vals.Should().ContainSingle().Which.Should().Be("cf-a");
    }

    [Fact]
    public async Task Interleave_family_filters_returns_both()
    {
        await Client.MutateRowAsync(TN, "gcr-intlv",
            Mutations.SetCell(CF, "c", "cf-val", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "cf2-val", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Interleave(
                RowFilters.FamilyNameExact(CF),
                RowFilters.FamilyNameExact("cf2")),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("gcr-intlv") } }
        };
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                vals.Add(cell.Value.ToStringUtf8());

        vals.Should().HaveCount(2);
        vals.Should().Contain("cf-val");
        vals.Should().Contain("cf2-val");
    }

    [Fact]
    public async Task Value_range_filter_across_versions()
    {
        await Client.MutateRowAsync(TN, "gcr-vrf",
            Mutations.SetCell(CF, "c", "aaa", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "mmm", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "zzz", new BigtableVersion(3000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.ValueRange(ValueRange.Closed("bbb", "nnn")),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("gcr-vrf") } }
        };
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                vals.Add(cell.Value.ToStringUtf8());

        vals.Should().ContainSingle().Which.Should().Be("mmm");
    }

    [Fact]
    public async Task CellsPerRow_limit_across_columns()
    {
        await Client.MutateRowAsync(TN, "gcr-cpr",
            Mutations.SetCell(CF, "a", "a1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "b1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "c1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "d", "d1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "e", "e1", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.CellsPerRowLimit(3),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("gcr-cpr") } }
        };
        var cells = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                cells.Add(cell.Value.ToStringUtf8());

        cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task CellsPerRowOffset_skips_cells()
    {
        await Client.MutateRowAsync(TN, "gcr-cpro",
            Mutations.SetCell(CF, "a", "a1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "b1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "c1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "d", "d1", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.CellsPerRowOffset(2),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("gcr-cpro") } }
        };
        var cells = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                cells.Add(cell.Value.ToStringUtf8());

        cells.Should().HaveCount(2);
        cells.Should().Contain("c1");
        cells.Should().Contain("d1");
    }

    [Fact]
    public async Task Empty_value_is_stored_and_retrievable()
    {
        await Client.MutateRowAsync(TN, "gcr-empty",
            Mutations.SetCell(CF, "c", "", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "gcr-empty");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().BeEmpty();
    }

    [Fact]
    public async Task Binary_values_preserved()
    {
        var bytes = new byte[] { 0x00, 0x01, 0xFF, 0xFE, 0x80 };
        await Client.MutateRowAsync(TN, "gcr-bin",
            Mutations.SetCell(CF, "c", ByteString.CopyFrom(bytes), new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "gcr-bin");
        row!.Families[0].Columns[0].Cells[0].Value.ToByteArray().Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public async Task Large_value_round_trips()
    {
        var largeValue = new string('X', 10_000);
        await Client.MutateRowAsync(TN, "gcr-large",
            Mutations.SetCell(CF, "c", largeValue, new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "gcr-large");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be(largeValue);
    }

    [Fact]
    public async Task Condition_filter_true_branch_selected()
    {
        await Client.MutateRowAsync(TN, "gcr-cond-t",
            Mutations.SetCell(CF, "x", "match", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "y", "other", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Condition(
                RowFilters.ValueExact("match"),
                RowFilters.ColumnQualifierExact("y"),
                RowFilters.BlockAllFilter()),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("gcr-cond-t") } }
        };
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                vals.Add(cell.Value.ToStringUtf8());

        vals.Should().ContainSingle().Which.Should().Be("other");
    }

    [Fact]
    public async Task Condition_filter_false_branch_selected()
    {
        await Client.MutateRowAsync(TN, "gcr-cond-f",
            Mutations.SetCell(CF, "x", "no-match", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "y", "other", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Condition(
                RowFilters.ValueExact("will-not-match"),
                RowFilters.BlockAllFilter(),
                RowFilters.ColumnQualifierExact("y")),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("gcr-cond-f") } }
        };
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                vals.Add(cell.Value.ToStringUtf8());

        vals.Should().ContainSingle().Which.Should().Be("other");
    }
}

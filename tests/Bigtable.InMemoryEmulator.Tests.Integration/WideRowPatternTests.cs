using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for wide rows — many columns per row, many versions per column,
/// and interactions between them.
/// Ref: https://cloud.google.com/bigtable/docs/schema-design#types_of_row_keys
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class WideRowPatternTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "wide-row";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public WideRowPatternTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Hundred_columns_single_row()
    {
        var mutations = Enumerable.Range(0, 100)
            .Select(i => Mutations.SetCell(CF, $"col{i:D3}", $"val{i}", new BigtableVersion(1000)))
            .ToArray();
        await Client.MutateRowAsync(TN, "wr-100col", mutations);

        var row = await Client.ReadRowAsync(TN, "wr-100col");
        row!.Families[0].Columns.Should().HaveCount(100);
    }

    [Fact]
    public async Task CellsPerRowLimit_on_wide_row()
    {
        var mutations = Enumerable.Range(0, 50)
            .Select(i => Mutations.SetCell(CF, $"col{i:D3}", $"val{i}", new BigtableVersion(1000)))
            .ToArray();
        await Client.MutateRowAsync(TN, "wr-cpr-wide", mutations);

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.CellsPerRowLimit(5),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("wr-cpr-wide") } }
        };
        var cellCount = 0;
        await foreach (var row in Client.ReadRows(request))
            cellCount += row.Families.Sum(f => f.Columns.Sum(c => c.Cells.Count));

        cellCount.Should().Be(5);
    }

    [Fact]
    public async Task CellsPerRowOffset_on_wide_row()
    {
        var mutations = Enumerable.Range(0, 20)
            .Select(i => Mutations.SetCell(CF, $"col{i:D3}", $"val{i}", new BigtableVersion(1000)))
            .ToArray();
        await Client.MutateRowAsync(TN, "wr-cpro-wide", mutations);

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.CellsPerRowOffset(15),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("wr-cpro-wide") } }
        };
        var cellCount = 0;
        await foreach (var row in Client.ReadRows(request))
            cellCount += row.Families.Sum(f => f.Columns.Sum(c => c.Cells.Count));

        cellCount.Should().Be(5);
    }

    [Fact]
    public async Task ColumnRange_on_wide_row()
    {
        var mutations = Enumerable.Range(0, 30)
            .Select(i => Mutations.SetCell(CF, $"col{i:D3}", $"val{i}", new BigtableVersion(1000)))
            .ToArray();
        await Client.MutateRowAsync(TN, "wr-colr", mutations);

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.ColumnRange(ColumnRange.Closed(CF, "col010", "col015")),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("wr-colr") } }
        };
        var cols = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
                cols.Add(c.Qualifier.ToStringUtf8());

        cols.Should().HaveCount(6); // col010 through col015
    }

    [Fact]
    public async Task Delete_single_column_from_wide_row()
    {
        var mutations = Enumerable.Range(0, 10)
            .Select(i => Mutations.SetCell(CF, $"col{i}", $"val{i}", new BigtableVersion(1000)))
            .ToArray();
        await Client.MutateRowAsync(TN, "wr-del1", mutations);

        await Client.MutateRowAsync(TN, "wr-del1", Mutations.DeleteFromColumn(CF, "col5"));

        var row = await Client.ReadRowAsync(TN, "wr-del1");
        row!.Families[0].Columns.Should().HaveCount(9);
        row.Families[0].Columns.Should().NotContain(c => c.Qualifier.ToStringUtf8() == "col5");
    }

    [Fact]
    public async Task Multiple_versions_per_column_in_wide_row()
    {
        // 10 columns × 5 versions = 50 cells
        for (int col = 0; col < 10; col++)
            for (int ver = 1; ver <= 5; ver++)
                await Client.MutateRowAsync(TN, "wr-mvwide",
                    Mutations.SetCell(CF, $"col{col}", $"v{ver}", new BigtableVersion(ver * 1000)));

        var row = await Client.ReadRowAsync(TN, "wr-mvwide");
        row!.Families[0].Columns.Should().HaveCount(10);
        foreach (var col in row.Families[0].Columns)
            col.Cells.Should().HaveCount(5);
    }

    [Fact]
    public async Task CellsPerColumnLimit_on_multi_version_wide_row()
    {
        for (int col = 0; col < 5; col++)
            for (int ver = 1; ver <= 10; ver++)
                await Client.MutateRowAsync(TN, "wr-cpcl-wide",
                    Mutations.SetCell(CF, $"col{col}", $"v{ver}", new BigtableVersion(ver * 1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.CellsPerColumnLimit(2),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("wr-cpcl-wide") } }
        };
        var cellCount = 0;
        await foreach (var row in Client.ReadRows(request))
            cellCount += row.Families.Sum(f => f.Columns.Sum(c => c.Cells.Count));

        cellCount.Should().Be(10); // 5 columns × 2 versions
    }

    [Fact]
    public async Task ColumnQualifierRegex_on_wide_row()
    {
        var mutations = Enumerable.Range(0, 20)
            .Select(i => Mutations.SetCell(CF, $"col{i:D2}", $"val{i}", new BigtableVersion(1000)))
            .ToArray();
        await Client.MutateRowAsync(TN, "wr-cqr", mutations);

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.ColumnQualifierRegex("col0[0-4]"),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("wr-cqr") } }
        };
        var cols = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
                cols.Add(c.Qualifier.ToStringUtf8());

        cols.Should().HaveCount(5); // col00 through col04
    }

    [Fact]
    public async Task StripValue_preserves_column_structure()
    {
        var mutations = Enumerable.Range(0, 5)
            .Select(i => Mutations.SetCell(CF, $"col{i}", $"val{i}", new BigtableVersion(1000)))
            .ToArray();
        await Client.MutateRowAsync(TN, "wr-strip", mutations);

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.StripValueTransformer(),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("wr-strip") } }
        };
        await foreach (var row in Client.ReadRows(request))
        {
            row.Families[0].Columns.Should().HaveCount(5);
            foreach (var col in row.Families[0].Columns)
                col.Cells[0].Value.Length.Should().Be(0);
        }
    }

    [Fact]
    public async Task Batch_add_columns_to_wide_row()
    {
        // Initial columns
        await Client.MutateRowAsync(TN, "wr-batch-add",
            Mutations.SetCell(CF, "existing", "val", new BigtableVersion(1000)));

        // Batch add more
        var entries = new[]
        {
            Mutations.CreateEntry("wr-batch-add",
                Enumerable.Range(0, 20)
                    .Select(i => Mutations.SetCell(CF, $"new{i:D2}", $"v{i}", new BigtableVersion(2000)))
                    .ToArray())
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "wr-batch-add");
        row!.Families[0].Columns.Should().HaveCount(21); // 1 existing + 20 new
    }

    [Fact]
    public async Task Delete_from_family_on_wide_row()
    {
        var mutations = Enumerable.Range(0, 50)
            .Select(i => Mutations.SetCell(CF, $"col{i:D3}", $"val{i}", new BigtableVersion(1000)))
            .ToArray();
        await Client.MutateRowAsync(TN, "wr-delfam", mutations);

        await Client.MutateRowAsync(TN, "wr-delfam", Mutations.DeleteFromFamily(CF));

        var row = await Client.ReadRowAsync(TN, "wr-delfam");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Columns_returned_in_sorted_order()
    {
        await Client.MutateRowAsync(TN, "wr-sort",
            Mutations.SetCell(CF, "z", "vz", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "va", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "m", "vm", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "wr-sort");
        var cols = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().ContainInOrder("a", "m", "z");
    }

    [Fact]
    public async Task Offset_plus_limit_on_wide_row()
    {
        var mutations = Enumerable.Range(0, 20)
            .Select(i => Mutations.SetCell(CF, $"col{i:D2}", $"val{i}", new BigtableVersion(1000)))
            .ToArray();
        await Client.MutateRowAsync(TN, "wr-off-lim", mutations);

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.CellsPerRowOffset(5),
                RowFilters.CellsPerRowLimit(3)),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("wr-off-lim") } }
        };
        var cellCount = 0;
        await foreach (var row in Client.ReadRows(request))
            cellCount += row.Families.Sum(f => f.Columns.Sum(c => c.Cells.Count));

        cellCount.Should().Be(3);
    }

    [Fact]
    public async Task Value_regex_filters_wide_row_columns()
    {
        await Client.MutateRowAsync(TN, "wr-vr",
            Mutations.SetCell(CF, "a", "match-me", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "no-match", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "match-me-too", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.ValueRegex("match-me.*"),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("wr-vr") } }
        };
        var cols = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
                cols.Add(c.Qualifier.ToStringUtf8());

        cols.Should().HaveCount(2);
        cols.Should().Contain("a");
        cols.Should().Contain("c");
    }
}

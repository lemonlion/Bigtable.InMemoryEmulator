using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Integration tests for filter chain combinations — multiple filters
/// composed in chains and interleaves to verify correct evaluation order.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class FilterChainCompositionTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "filt-chain-comp";
    private const string CF = "cf";

    public FilterChainCompositionTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task SeedRow(string key, string column, string value, long tsMillis = 1000)
    {
        await Client.MutateRowAsync(TN, new BigtableByteString(key),
            Mutations.SetCell(CF, column, value, new BigtableVersion(tsMillis)));
    }

    // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
    //   "Chain: Applies filters in order, passing output of one as input to next."
    [Fact]
    public async Task Chain_family_then_column_narrows_result()
    {
        await SeedRow("fcc-r1", "target", "hit");
        await SeedRow("fcc-r1", "other", "miss");

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fcc-r1") } },
            Filter = RowFilters.Chain(
                RowFilters.FamilyNameRegex(CF),
                RowFilters.ColumnQualifierExact("target"))
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows.Should().HaveCount(1);
        rows[0].Families[0].Columns.Should().HaveCount(1);
        rows[0].Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("target");
    }

    [Fact]
    public async Task Chain_column_then_value_exact()
    {
        await SeedRow("fcc-cv1", "status", "active");
        await SeedRow("fcc-cv1", "status2", "inactive");

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fcc-cv1") } },
            Filter = RowFilters.Chain(
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.ValueExact("active"))
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows.Should().HaveCount(1);
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("active");
    }

    [Fact]
    public async Task Chain_cells_per_column_then_value_filter()
    {
        // Create multiple versions
        await Client.MutateRowAsync(TN, new BigtableByteString("fcc-cpc"),
            Mutations.SetCell(CF, "col", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, new BigtableByteString("fcc-cpc"),
            Mutations.SetCell(CF, "col", "new", new BigtableVersion(2000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fcc-cpc") } },
            Filter = RowFilters.Chain(
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueExact("new"))
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows.Should().HaveCount(1);
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(1);
    }

    // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
    //   "Interleave: Applies each filter independently, then merges results."
    [Fact]
    public async Task Interleave_two_column_filters_returns_union()
    {
        await SeedRow("fcc-intlv", "a", "1");
        await SeedRow("fcc-intlv", "b", "2");
        await SeedRow("fcc-intlv", "c", "3");

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fcc-intlv") } },
            Filter = RowFilters.Interleave(
                RowFilters.ColumnQualifierExact("a"),
                RowFilters.ColumnQualifierExact("c"))
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows.Should().HaveCount(1);
        var cols = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().HaveCount(2);
        cols.Should().Contain("a");
        cols.Should().Contain("c");
    }

    [Fact]
    public async Task Interleave_three_value_filters()
    {
        await SeedRow("fcc-intlv3-a", "col", "alpha");
        await SeedRow("fcc-intlv3-b", "col", "beta");
        await SeedRow("fcc-intlv3-c", "col", "gamma");
        await SeedRow("fcc-intlv3-d", "col", "delta");

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Interleave(
                RowFilters.ValueExact("alpha"),
                RowFilters.ValueExact("beta"),
                RowFilters.ValueExact("gamma"))
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows.Select(r => r.Key.ToStringUtf8())
            .Should().BeEquivalentTo(new[] { "fcc-intlv3-a", "fcc-intlv3-b", "fcc-intlv3-c" });
    }

    [Fact]
    public async Task Chain_inside_interleave()
    {
        await SeedRow("fcc-ci", "status", "active");
        await SeedRow("fcc-ci", "name", "test");

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fcc-ci") } },
            Filter = RowFilters.Interleave(
                RowFilters.Chain(
                    RowFilters.ColumnQualifierExact("status"),
                    RowFilters.ValueExact("active")),
                RowFilters.ColumnQualifierExact("name"))
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows.Should().HaveCount(1);
        var cols = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("status");
        cols.Should().Contain("name");
    }

    [Fact]
    public async Task Interleave_inside_chain()
    {
        await SeedRow("fcc-ic", "a", "x");
        await SeedRow("fcc-ic", "b", "y");
        await SeedRow("fcc-ic", "c", "z");

        // First interleave selects columns a and c, then chain applies value filter
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fcc-ic") } },
            Filter = RowFilters.Chain(
                RowFilters.Interleave(
                    RowFilters.ColumnQualifierExact("a"),
                    RowFilters.ColumnQualifierExact("c")),
                RowFilters.ValueExact("z"))
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows.Should().HaveCount(1);
        rows[0].Families[0].Columns.Should().HaveCount(1);
        rows[0].Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("c");
    }

    [Fact]
    public async Task Condition_filter_true_branch()
    {
        await SeedRow("fcc-cond-t", "col", "yes");

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fcc-cond-t") } },
            Filter = RowFilters.Condition(
                RowFilters.ValueExact("yes"),
                RowFilters.StripValueTransformer(),
                RowFilters.BlockAllFilter())
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows.Should().HaveCount(1);
        // True branch strips value
        rows[0].Families[0].Columns[0].Cells[0].Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Condition_filter_false_branch()
    {
        await SeedRow("fcc-cond-f", "col", "no");

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fcc-cond-f") } },
            Filter = RowFilters.Condition(
                RowFilters.ValueExact("yes"),
                RowFilters.BlockAllFilter(),
                RowFilters.PassAllFilter())
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        // False branch passes all
        rows.Should().HaveCount(1);
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("no");
    }

    [Fact]
    public async Task Chain_with_strip_value_transformer()
    {
        await SeedRow("fcc-strip", "col", "secret");

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fcc-strip") } },
            Filter = RowFilters.Chain(
                RowFilters.ColumnQualifierExact("col"),
                RowFilters.StripValueTransformer())
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows.Should().HaveCount(1);
        rows[0].Families[0].Columns[0].Cells[0].Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Chain_with_cells_per_row_limit()
    {
        await SeedRow("fcc-cprl", "a", "1");
        await SeedRow("fcc-cprl", "b", "2");
        await SeedRow("fcc-cprl", "c", "3");

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fcc-cprl") } },
            Filter = RowFilters.CellsPerRowLimit(2)
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows.Should().HaveCount(1);
        var totalCells = rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();
        totalCells.Should().Be(2);
    }

    [Fact]
    public async Task Chain_with_cells_per_row_offset()
    {
        await SeedRow("fcc-cpro", "a", "1");
        await SeedRow("fcc-cpro", "b", "2");
        await SeedRow("fcc-cpro", "c", "3");

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fcc-cpro") } },
            Filter = RowFilters.CellsPerRowOffset(1)
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows.Should().HaveCount(1);
        var totalCells = rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();
        totalCells.Should().Be(2);
    }

    [Fact]
    public async Task Chain_offset_then_limit()
    {
        for (int i = 0; i < 5; i++)
            await SeedRow("fcc-ol", $"col{i}", $"v{i}");

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fcc-ol") } },
            Filter = RowFilters.Chain(
                RowFilters.CellsPerRowOffset(1),
                RowFilters.CellsPerRowLimit(2))
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows.Should().HaveCount(1);
        var totalCells = rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();
        totalCells.Should().Be(2);
    }

    [Fact]
    public async Task Chain_with_column_range()
    {
        await SeedRow("fcc-colr", "a", "1");
        await SeedRow("fcc-colr", "b", "2");
        await SeedRow("fcc-colr", "c", "3");
        await SeedRow("fcc-colr", "d", "4");

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fcc-colr") } },
            Filter = RowFilters.ColumnRange(ColumnRange.ClosedOpen(CF, "b", "d"))
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows.Should().HaveCount(1);
        var cols = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().BeEquivalentTo(new[] { "b", "c" });
    }

    [Fact]
    public async Task Chain_column_range_and_value_filter()
    {
        await SeedRow("fcc-crv", "a", "keep");
        await SeedRow("fcc-crv", "b", "skip");
        await SeedRow("fcc-crv", "c", "keep");

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fcc-crv") } },
            Filter = RowFilters.Chain(
                RowFilters.ColumnRange(ColumnRange.Closed(CF, "a", "c")),
                RowFilters.ValueExact("keep"))
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows.Should().HaveCount(1);
        var cols = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().BeEquivalentTo(new[] { "a", "c" });
    }

    [Fact]
    public async Task Timestamp_range_filter_inclusive_exclusive()
    {
        // Ref: TimestampRange: start_timestamp_micros is inclusive, end is exclusive
        var rk = new BigtableByteString("fcc-tsrange");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v3", new BigtableVersion(3000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fcc-tsrange") } },
            // Start inclusive at 2000ms (= 2000000us), end exclusive at 3000ms (= 3000000us)
            Filter = RowFilters.TimestampRange(
                new DateTime(1970, 1, 1, 0, 0, 2, DateTimeKind.Utc),
                new DateTime(1970, 1, 1, 0, 0, 3, DateTimeKind.Utc))
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows.Should().HaveCount(1);
        rows[0].Families[0].Columns[0].Cells.Should().HaveCount(1);
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v2");
    }

    [Fact]
    public async Task Value_range_filter()
    {
        await SeedRow("fcc-vr-a", "col", "apple");
        await SeedRow("fcc-vr-b", "col", "banana");
        await SeedRow("fcc-vr-c", "col", "cherry");
        await SeedRow("fcc-vr-d", "col", "date");

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.RowKeyRegex("fcc-vr-.*"),
                RowFilters.ValueRange(ValueRange.ClosedOpen("banana", "date")))
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        var values = rows.SelectMany(r => r.Families)
            .SelectMany(f => f.Columns)
            .SelectMany(c => c.Cells)
            .Select(c => c.Value.ToStringUtf8()).ToList();
        values.Should().Contain("banana");
        values.Should().Contain("cherry");
        values.Should().NotContain("apple");
        values.Should().NotContain("date");
    }

    [Fact]
    public async Task Row_key_regex_with_value_filter()
    {
        await SeedRow("fcc-rkv-match1", "col", "target");
        await SeedRow("fcc-rkv-match2", "col", "target");
        await SeedRow("fcc-rkv-other", "col", "other");
        await SeedRow("fcc-rkv-match3", "col", "nope");

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.RowKeyRegex("fcc-rkv-match.*"),
                RowFilters.ValueExact("target"))
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Double_chain_nested()
    {
        await SeedRow("fcc-dc", "status", "active");
        await SeedRow("fcc-dc", "name", "test");

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fcc-dc") } },
            Filter = RowFilters.Chain(
                RowFilters.Chain(
                    RowFilters.FamilyNameRegex(CF),
                    RowFilters.ColumnQualifierExact("status")),
                RowFilters.ValueExact("active"))
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows.Should().HaveCount(1);
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("active");
    }

    [Fact]
    public async Task BlockAll_filter_returns_no_rows()
    {
        await SeedRow("fcc-block", "col", "val");

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fcc-block") } },
            Filter = RowFilters.BlockAllFilter()
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task PassAll_filter_returns_all_cells()
    {
        await SeedRow("fcc-pass", "a", "1");
        await SeedRow("fcc-pass", "b", "2");

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fcc-pass") } },
            Filter = RowFilters.PassAllFilter()
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows.Should().HaveCount(1);
        rows[0].Families[0].Columns.Should().HaveCount(2);
    }
}

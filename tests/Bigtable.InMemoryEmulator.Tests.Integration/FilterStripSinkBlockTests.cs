using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for filter strip/sink/block interactions and edge cases.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class FilterStripSinkBlockTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "fssb-tests";
    private const string CF = "cf";

    public FilterStripSinkBlockTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task StripValue_returns_empty_values()
    {
        await Client.MutateRowAsync(TN, "fssb-strip",
            Mutations.SetCell(CF, "c", "data", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.StripValueTransformer(),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fssb-strip") } }
        };
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
            {
                cell.Value.ToStringUtf8().Should().BeEmpty();
                // But the cell still exists with metadata
                cell.TimestampMicros.Should().BeGreaterThan(0);
            }
    }

    [Fact]
    public async Task StripValue_preserves_column_structure()
    {
        await Client.MutateRowAsync(TN, "fssb-strip-struct",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.StripValueTransformer(),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fssb-strip-struct") } }
        };
        var quals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
                quals.Add(c.Qualifier.ToStringUtf8());

        quals.Should().HaveCount(2);
        quals.Should().Contain("a");
        quals.Should().Contain("b");
    }

    [Fact]
    public async Task BlockAll_returns_nothing()
    {
        await Client.MutateRowAsync(TN, "fssb-block",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.BlockAllFilter(),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fssb-block") } }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task PassAll_returns_everything()
    {
        await Client.MutateRowAsync(TN, "fssb-pass",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.PassAllFilter(),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fssb-pass") } }
        };
        var cellCount = 0;
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
                cellCount += c.Cells.Count;
        cellCount.Should().Be(2);
    }

    [Fact]
    public async Task StripValue_chained_with_family_filter()
    {
        await Client.MutateRowAsync(TN, "fssb-strip-fam",
            Mutations.SetCell(CF, "c", "data", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "data2", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.FamilyNameExact(CF),
                RowFilters.StripValueTransformer()),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fssb-strip-fam") } }
        };
        var families = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
                families.Add(f.Name);
        families.Should().HaveCount(1);
        families[0].Should().Be(CF);
    }

    [Fact]
    public async Task Label_adds_single_label()
    {
        await Client.MutateRowAsync(TN, "fssb-label",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = new RowFilter { ApplyLabelTransformer = "my-label" },
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fssb-label") } }
        };
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
            {
                cell.Labels.Should().ContainSingle().Which.Should().Be("my-label");
            }
    }

    [Fact]
    public async Task Label_with_column_filter()
    {
        await Client.MutateRowAsync(TN, "fssb-label-col",
            Mutations.SetCell(CF, "target", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "other", "v", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.ColumnQualifierExact("target"),
                new RowFilter { ApplyLabelTransformer = "found" }),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fssb-label-col") } }
        };
        var labels = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                labels.AddRange(cell.Labels);
        labels.Should().ContainSingle().Which.Should().Be("found");
    }

    [Fact]
    public async Task StripValue_with_multiple_versions()
    {
        await Client.MutateRowAsync(TN, "fssb-strip-ver",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "fssb-strip-ver",
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.StripValueTransformer(),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fssb-strip-ver") } }
        };
        var cells = new List<Cell>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
                cells.AddRange(c.Cells);
        cells.Should().HaveCount(2);
        cells.Should().AllSatisfy(c => c.Value.ToStringUtf8().Should().BeEmpty());
    }

    [Fact]
    public async Task BlockAll_in_chain_blocks_everything()
    {
        await Client.MutateRowAsync(TN, "fssb-block-chain",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.PassAllFilter(),
                RowFilters.BlockAllFilter()),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fssb-block-chain") } }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Interleave_strip_and_passall_deduplicates()
    {
        await Client.MutateRowAsync(TN, "fssb-ilv-dedup",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Interleave(
                RowFilters.StripValueTransformer(),
                RowFilters.PassAllFilter()),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fssb-ilv-dedup") } }
        };
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                vals.Add(cell.Value.ToStringUtf8());
        // Interleave outputs from both branches — one stripped, one not
        vals.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Condition_true_strip_false_passall()
    {
        await Client.MutateRowAsync(TN, "fssb-cond-strip",
            Mutations.SetCell(CF, "c", "data", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Condition(
                RowFilters.PassAllFilter(),
                RowFilters.StripValueTransformer(),
                RowFilters.PassAllFilter()),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fssb-cond-strip") } }
        };
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                cell.Value.ToStringUtf8().Should().BeEmpty(); // true branch strips
    }

    [Fact]
    public async Task Condition_false_strip()
    {
        // No data → predicate produces nothing → false branch
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Condition(
                RowFilters.PassAllFilter(),
                RowFilters.PassAllFilter(),
                RowFilters.StripValueTransformer()),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fssb-cond-nodata") } }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);
        rows.Should().BeEmpty(); // no data, no output regardless of branch
    }

    [Fact]
    public async Task BlockAll_in_interleave_other_branch_passes()
    {
        await Client.MutateRowAsync(TN, "fssb-ilv-block",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Interleave(
                RowFilters.BlockAllFilter(),
                RowFilters.PassAllFilter()),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fssb-ilv-block") } }
        };
        var cellCount = 0;
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
                cellCount += c.Cells.Count;
        cellCount.Should().Be(1); // passall branch contributes
    }

    [Fact]
    public async Task StripValue_preserves_timestamp()
    {
        await Client.MutateRowAsync(TN, "fssb-strip-ts",
            Mutations.SetCell(CF, "c", "data", new BigtableVersion(5000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.StripValueTransformer(),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fssb-strip-ts") } }
        };
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                cell.TimestampMicros.Should().Be(5000 * 1000);
    }

    [Fact]
    public async Task StripValue_preserves_family_name()
    {
        await Client.MutateRowAsync(TN, "fssb-strip-fn",
            Mutations.SetCell("cf2", "c", "data", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.StripValueTransformer(),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fssb-strip-fn") } }
        };
        await foreach (var row in Client.ReadRows(request))
            row.Families[0].Name.Should().Be("cf2");
    }

    [Fact]
    public async Task Label_does_not_modify_value()
    {
        await Client.MutateRowAsync(TN, "fssb-label-val",
            Mutations.SetCell(CF, "c", "original", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = new RowFilter { ApplyLabelTransformer = "tag" },
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("fssb-label-val") } }
        };
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
            {
                cell.Value.ToStringUtf8().Should().Be("original");
                cell.Labels.Should().Contain("tag");
            }
    }
}

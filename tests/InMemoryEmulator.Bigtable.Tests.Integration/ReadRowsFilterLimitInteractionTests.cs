using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for ReadRows with various filter + limit + range interactions.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsFilterLimitInteractionTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "rr-filt-lim";
    private const string CF = "cf";

    public ReadRowsFilterLimitInteractionTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        // Seed 20 rows with varying values
        var entries = Enumerable.Range(0, 20).Select(i =>
            Mutations.CreateEntry(
                new BigtableByteString($"rfli-{i:D3}"),
                Mutations.SetCell(CF, "val", (i % 2 == 0 ? "even" : "odd"), new BigtableVersion(1000)),
                Mutations.SetCell(CF, "num", i.ToString(), new BigtableVersion(1000))))
            .ToList();
        await Client.MutateRowsAsync(TN, entries);
    }
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<Row>> Read(ReadRowsRequest req)
    {
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(req))
            rows.Add(row);
        return rows;
    }

    [Fact]
    public async Task Limit_applied_after_filter()
    {
        // Filter to even rows, then limit 3
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.ColumnQualifierExact("val"),
                RowFilters.ValueExact("even")),
            RowsLimit = 3
        };
        var rows = await Read(req);
        rows.Should().HaveCount(3);
        // All should be even indices (0, 2, 4)
        foreach (var r in rows)
        {
            r.Families.SelectMany(f => f.Columns)
                .First(c => c.Qualifier.ToStringUtf8() == "val")
                .Cells[0].Value.ToStringUtf8().Should().Be("even");
        }
    }

    [Fact]
    public async Task Filter_with_no_matches_and_limit()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.ValueExact("nonexistent"),
            RowsLimit = 10
        };
        var rows = await Read(req);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Limit_0_means_no_limit()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 0
        };
        var rows = await Read(req);
        rows.Should().HaveCount(20);
    }

    [Fact]
    public async Task CellsPerRowLimit_1_and_rows_limit()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.CellsPerRowLimit(1),
            RowsLimit = 5
        };
        var rows = await Read(req);
        rows.Should().HaveCount(5);
        foreach (var r in rows)
        {
            var totalCells = r.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();
            totalCells.Should().Be(1);
        }
    }

    [Fact]
    public async Task Column_filter_and_limit()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.ColumnQualifierExact("num"),
            RowsLimit = 3
        };
        var rows = await Read(req);
        rows.Should().HaveCount(3);
        foreach (var r in rows)
        {
            r.Families[0].Columns.Should().HaveCount(1);
            r.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("num");
        }
    }

    [Fact]
    public async Task Range_and_limit()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges = { new RowRange
                {
                    StartKeyClosed = ByteString.CopyFromUtf8("rfli-005"),
                    EndKeyClosed = ByteString.CopyFromUtf8("rfli-015")
                }}
            },
            RowsLimit = 5
        };
        var rows = await Read(req);
        rows.Should().HaveCount(5);
        rows[0].Key.ToStringUtf8().Should().Be("rfli-005");
    }

    [Fact]
    public async Task Range_and_filter_and_limit()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges = { new RowRange
                {
                    StartKeyClosed = ByteString.CopyFromUtf8("rfli-000"),
                    EndKeyClosed = ByteString.CopyFromUtf8("rfli-019")
                }}
            },
            Filter = RowFilters.Chain(
                RowFilters.ColumnQualifierExact("val"),
                RowFilters.ValueExact("odd")),
            RowsLimit = 4
        };
        var rows = await Read(req);
        rows.Should().HaveCount(4);
    }

    [Fact]
    public async Task StripValue_still_returns_row_structure()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.StripValueTransformer(),
            RowsLimit = 3
        };
        var rows = await Read(req);
        rows.Should().HaveCount(3);
        // Values should be stripped (empty)
        foreach (var r in rows)
            foreach (var f in r.Families)
                foreach (var c in f.Columns)
                    foreach (var cell in c.Cells)
                        cell.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task CellsPerRowOffset_skips_first_cell()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("rfli-000") } },
            Filter = RowFilters.CellsPerRowOffset(1)
        };
        var rows = await Read(req);
        rows.Should().HaveCount(1);
        // Row has 2 cells (val + num), offset 1 means 1 remaining
        var totalCells = rows[0].Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Count();
        totalCells.Should().Be(1);
    }

    [Fact]
    public async Task Value_regex_across_multiple_rows()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.ColumnQualifierExact("num"),
                RowFilters.ValueRegex("1[0-9]"))
        };
        var rows = await Read(req);
        // Should match rows with num values 10, 11, 12, ..., 19
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task Multiple_keys_with_filter()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowKeys =
                {
                    ByteString.CopyFromUtf8("rfli-000"),
                    ByteString.CopyFromUtf8("rfli-001"),
                    ByteString.CopyFromUtf8("rfli-002")
                }
            },
            Filter = RowFilters.Chain(
                RowFilters.ColumnQualifierExact("val"),
                RowFilters.ValueExact("even"))
        };
        var rows = await Read(req);
        rows.Should().HaveCount(2); // 000 (even) and 002 (even)
    }

    [Fact]
    public async Task RowKeyRegex_with_limit()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.RowKeyRegex("rfli-01.*"),
            RowsLimit = 3
        };
        var rows = await Read(req);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task BlockAll_with_limit_returns_empty()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.BlockAllFilter(),
            RowsLimit = 100
        };
        var rows = await Read(req);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task PassAll_with_exact_limit()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.PassAllFilter(),
            RowsLimit = 20
        };
        var rows = await Read(req);
        rows.Should().HaveCount(20);
    }

    [Fact]
    public async Task Condition_filter_with_limit()
    {
        var req = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Condition(
                RowFilters.Chain(RowFilters.ColumnQualifierExact("val"), RowFilters.ValueExact("even")),
                RowFilters.PassAllFilter(),
                RowFilters.BlockAllFilter()),
            RowsLimit = 5
        };
        var rows = await Read(req);
        // Only even rows pass the condition filter
        rows.Should().HaveCount(5);
    }
}

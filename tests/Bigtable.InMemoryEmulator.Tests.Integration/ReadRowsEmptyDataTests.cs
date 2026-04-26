using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for reading empty data, nonexistent rows, and empty result scenarios.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsEmptyDataTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "rred-tests";
    private const string CF = "cf";

    public ReadRowsEmptyDataTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task ReadRow_nonexistent_key_returns_null()
    {
        var row = await Client.ReadRowAsync(TN, "rred-ghost");
        row.Should().BeNull();
    }

    [Fact]
    public async Task ReadRows_empty_range_returns_nothing()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges = { new RowRange
                {
                    StartKeyClosed = ByteString.CopyFromUtf8("rred-empty-a"),
                    EndKeyOpen = ByteString.CopyFromUtf8("rred-empty-b")
                }}
            }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadRows_nonexistent_keys_returned_empty()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowKeys =
                {
                    ByteString.CopyFromUtf8("rred-ghost-1"),
                    ByteString.CopyFromUtf8("rred-ghost-2")
                }
            }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadRows_filter_excludes_all_data()
    {
        await Client.MutateRowAsync(TN, "rred-filter-all",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.BlockAllFilter(),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("rred-filter-all") } }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadRows_family_filter_no_match()
    {
        await Client.MutateRowAsync(TN, "rred-fam-nm",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.FamilyNameExact("nonexistent"),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("rred-fam-nm") } }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadRows_column_filter_no_match()
    {
        await Client.MutateRowAsync(TN, "rred-col-nm",
            Mutations.SetCell(CF, "exists", "v", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.ColumnQualifierExact("nonexistent"),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("rred-col-nm") } }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadRows_value_filter_no_match()
    {
        await Client.MutateRowAsync(TN, "rred-val-nm",
            Mutations.SetCell(CF, "c", "abc", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.ValueExact("xyz"),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("rred-val-nm") } }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadRows_timestamp_filter_no_match()
    {
        var ts = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        await Client.MutateRowAsync(TN, "rred-ts-nm",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(ts)));

        var earlyEnd = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.TimestampRange(null, earlyEnd),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("rred-ts-nm") } }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadRow_after_delete_returns_null()
    {
        await Client.MutateRowAsync(TN, "rred-del",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "rred-del", Mutations.DeleteFromRow());

        var row = await Client.ReadRowAsync(TN, "rred-del");
        row.Should().BeNull();
    }

    [Fact]
    public async Task ReadRows_rows_limit_0_means_unlimited()
    {
        for (int i = 0; i < 5; i++)
            await Client.MutateRowAsync(TN, $"rred-lim0-{i:D2}",
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 0,
            Rows = new RowSet
            {
                RowRanges = { new RowRange
                {
                    StartKeyClosed = ByteString.CopyFromUtf8("rred-lim0-"),
                    EndKeyOpen = ByteString.CopyFromUtf8("rred-lim0-~")
                }}
            }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);
        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task ReadRows_with_row_key_regex_no_match()
    {
        await Client.MutateRowAsync(TN, "rred-regex-nm",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.RowKeyRegex("xyzzy.*"),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("rred-regex-nm") } }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadRows_column_range_no_match()
    {
        await Client.MutateRowAsync(TN, "rred-cr-nm",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.ColumnRange(ColumnRange.Closed(CF, "x", "z")),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("rred-cr-nm") } }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadRows_value_range_no_match()
    {
        await Client.MutateRowAsync(TN, "rred-vr-nm",
            Mutations.SetCell(CF, "c", "abc", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.ValueRange(ValueRange.Closed("xyz", "zzz")),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("rred-vr-nm") } }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadRows_of_all_deleted_columns_returns_empty()
    {
        await Client.MutateRowAsync(TN, "rred-del-cols",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "rred-del-cols",
            Mutations.DeleteFromColumn(CF, "a"),
            Mutations.DeleteFromColumn(CF, "b"));

        var row = await Client.ReadRowAsync(TN, "rred-del-cols");
        row.Should().BeNull();
    }

    [Fact]
    public async Task ReadRows_mixed_existing_and_nonexistent()
    {
        await Client.MutateRowAsync(TN, "rred-mixed-a",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowKeys =
                {
                    ByteString.CopyFromUtf8("rred-mixed-a"),
                    ByteString.CopyFromUtf8("rred-mixed-ghost")
                }
            }
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());
        keys.Should().HaveCount(1);
        keys[0].Should().Be("rred-mixed-a");
    }

    [Fact]
    public async Task ReadRows_cells_per_row_limit_on_empty()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.CellsPerRowLimit(1),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("rred-cprl-empty") } }
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadRow_after_family_delete_still_has_other_family()
    {
        await Client.MutateRowAsync(TN, "rred-fam-del",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "b", "v2", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "rred-fam-del", Mutations.DeleteFromFamily(CF));

        var row = await Client.ReadRowAsync(TN, "rred-fam-del");
        row.Should().NotBeNull();
        row!.Families.Should().HaveCount(1);
        row.Families[0].Name.Should().Be("cf2");
    }
}

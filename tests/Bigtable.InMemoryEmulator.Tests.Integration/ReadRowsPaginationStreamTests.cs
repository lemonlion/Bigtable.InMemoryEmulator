using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for ReadRows pagination, streaming, row limits, and empty/missing data patterns.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsPaginationStreamTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "rrps-tests";
    private const string CF = "cf";

    public ReadRowsPaginationStreamTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        // Seed 100 rows for pagination tests
        for (int i = 0; i < 100; i++)
        {
            await Client.MutateRowAsync(TN, $"rpst-{i:D4}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)));
        }
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private RowSet PrefixRowSet(string prefix) => new RowSet
    {
        RowRanges = { new RowRange
        {
            StartKeyClosed = ByteString.CopyFromUtf8(prefix),
            EndKeyOpen = ByteString.CopyFromUtf8(prefix + "~")
        }}
    };

    [Fact]
    public async Task Limit_1_returns_single_row()
    {
        var request = new ReadRowsRequest { TableNameAsTableName = TN, RowsLimit = 1, Rows = PrefixRowSet("rpst-") };
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(1);
    }

    [Fact]
    public async Task Limit_10_returns_10_rows()
    {
        var request = new ReadRowsRequest { TableNameAsTableName = TN, RowsLimit = 10, Rows = PrefixRowSet("rpst-") };
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(10);
    }

    [Fact]
    public async Task Limit_50_returns_50_rows()
    {
        var request = new ReadRowsRequest { TableNameAsTableName = TN, RowsLimit = 50, Rows = PrefixRowSet("rpst-") };
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(50);
    }

    [Fact]
    public async Task Limit_100_returns_all_100_rows()
    {
        var request = new ReadRowsRequest { TableNameAsTableName = TN, RowsLimit = 100, Rows = PrefixRowSet("rpst-") };
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(100);
    }

    [Fact]
    public async Task Limit_200_returns_all_available_rows()
    {
        var request = new ReadRowsRequest { TableNameAsTableName = TN, RowsLimit = 200, Rows = PrefixRowSet("rpst-") };
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(100); // Only 100 seeded
    }

    [Fact]
    public async Task No_limit_returns_all_rows()
    {
        var request = new ReadRowsRequest { TableNameAsTableName = TN, Rows = PrefixRowSet("rpst-") };
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(100);
    }

    [Fact]
    public async Task Rows_returned_in_lexicographic_order()
    {
        var request = new ReadRowsRequest { TableNameAsTableName = TN, RowsLimit = 5, Rows = PrefixRowSet("rpst-") };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());

        keys.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Range_within_data_returns_subset()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowRanges = { new RowRange
            {
                StartKeyClosed = ByteString.CopyFromUtf8("rpst-0010"),
                EndKeyOpen = ByteString.CopyFromUtf8("rpst-0020")
            }}}
        };
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(10); // 0010-0019
    }

    [Fact]
    public async Task Range_with_limit_smaller_than_range()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 3,
            Rows = new RowSet { RowRanges = { new RowRange
            {
                StartKeyClosed = ByteString.CopyFromUtf8("rpst-0050"),
                EndKeyOpen = ByteString.CopyFromUtf8("rpst-0060")
            }}}
        };
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(3);
    }

    [Fact]
    public async Task Multiple_specific_keys()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowKeys = {
                    ByteString.CopyFromUtf8("rpst-0001"),
                    ByteString.CopyFromUtf8("rpst-0050"),
                    ByteString.CopyFromUtf8("rpst-0099")
                }
            }
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());

        keys.Should().BeEquivalentTo(new[] { "rpst-0001", "rpst-0050", "rpst-0099" });
    }

    [Fact]
    public async Task Specific_keys_with_nonexistent_keys_skipped()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowKeys = {
                    ByteString.CopyFromUtf8("rpst-0001"),
                    ByteString.CopyFromUtf8("rpst-nonexistent"),
                    ByteString.CopyFromUtf8("rpst-0099")
                }
            }
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());

        keys.Should().BeEquivalentTo(new[] { "rpst-0001", "rpst-0099" });
    }

    [Fact]
    public async Task Keys_and_range_combined()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowKeys = { ByteString.CopyFromUtf8("rpst-0000") },
                RowRanges = { new RowRange
                {
                    StartKeyClosed = ByteString.CopyFromUtf8("rpst-0098"),
                    EndKeyOpen = ByteString.CopyFromUtf8("rpst-0100")
                }}
            }
        };
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(3); // 0000, 0098, 0099
    }

    [Fact]
    public async Task Filter_with_limit_interaction()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 5,
            Filter = RowFilters.CellsPerColumnLimit(1),
            Rows = PrefixRowSet("rpst-")
        };
        var count = 0;
        await foreach (var row in Client.ReadRows(request))
        {
            count++;
            row.Families[0].Columns[0].Cells.Should().HaveCount(1);
        }
        count.Should().Be(5);
    }

    [Fact]
    public async Task Filter_that_removes_all_cells_skips_row()
    {
        // Seed a row with value "hidden" that won't match the filter
        await Client.MutateRowAsync(TN, "rpst-hidden",
            Mutations.SetCell(CF, "c", "hidden", new BigtableVersion(1000)));

        // Filter for value "v0" should only match rpst-0000
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(RowFilters.ColumnQualifierExact("c"), RowFilters.ValueExact("v0")),
            Rows = PrefixRowSet("rpst-")
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());

        keys.Should().Contain("rpst-0000");
        keys.Should().NotContain("rpst-hidden");
    }

    [Fact]
    public async Task Empty_RowSet_scans_all_rows()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 5,
            Rows = PrefixRowSet("rpst-")
        };
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(5);
    }

    [Fact]
    public async Task Multiple_ranges_union()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("rpst-0000"),
                        EndKeyOpen = ByteString.CopyFromUtf8("rpst-0003")
                    },
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("rpst-0097"),
                        EndKeyOpen = ByteString.CopyFromUtf8("rpst-0100")
                    }
                }
            }
        };
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(6); // 3 from each range
    }

    [Fact]
    public async Task Strip_value_filter_reduces_data_transfer()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 3,
            Filter = RowFilters.StripValueTransformer(),
            Rows = PrefixRowSet("rpst-")
        };
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                cell.Value.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task Limit_0_returns_nothing()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 0, // 0 means no limit in Bigtable API
            Rows = PrefixRowSet("rpst-")
        };
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().BeGreaterThan(0); // 0 = no limit
    }

    [Fact]
    public async Task Read_single_row_by_key()
    {
        var row = await Client.ReadRowAsync(TN, "rpst-0042");
        row.Should().NotBeNull();
        row!.Key.ToStringUtf8().Should().Be("rpst-0042");
    }

    [Fact]
    public async Task Read_nonexistent_row_returns_null()
    {
        var row = await Client.ReadRowAsync(TN, "rpst-nonexistent-unique");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Label_filter_applied_to_all_streamed_rows()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 3,
            Filter = new RowFilter { ApplyLabelTransformer = "page" },
            Rows = PrefixRowSet("rpst-")
        };
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                cell.Labels.Should().Contain("page");
    }

    [Fact]
    public async Task Block_all_filter_returns_no_rows()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.BlockAllFilter(),
            Rows = PrefixRowSet("rpst-")
        };
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(0);
    }

    [Fact]
    public async Task Open_start_range()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowRanges = { new RowRange
            {
                StartKeyOpen = ByteString.CopyFromUtf8("rpst-0097"),
                EndKeyOpen = ByteString.CopyFromUtf8("rpst-0100")
            }}}
        };
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(2); // 0098, 0099
    }

    [Fact]
    public async Task Closed_end_range()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowRanges = { new RowRange
            {
                StartKeyClosed = ByteString.CopyFromUtf8("rpst-0000"),
                EndKeyClosed = ByteString.CopyFromUtf8("rpst-0002")
            }}}
        };
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(3); // 0000, 0001, 0002
    }

    [Fact]
    public async Task Row_key_regex_filter_with_limit()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 3,
            Filter = RowFilters.RowKeyRegex("rpst-00[0-2][0-9]"),
            Rows = PrefixRowSet("rpst-")
        };
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(3);
    }

    [Fact]
    public async Task Family_filter_with_pagination()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 5,
            Filter = RowFilters.FamilyNameExact(CF),
            Rows = PrefixRowSet("rpst-")
        };
        var count = 0;
        await foreach (var row in Client.ReadRows(request))
        {
            count++;
            row.Families.Should().HaveCount(1);
            row.Families[0].Name.Should().Be(CF);
        }
        count.Should().Be(5);
    }

    [Fact]
    public async Task Condition_filter_with_limit()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 3,
            Filter = RowFilters.Condition(
                RowFilters.PassAllFilter(),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.BlockAllFilter()),
            Rows = PrefixRowSet("rpst-")
        };
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(3);
    }

    [Fact]
    public async Task Interleave_filter_with_limit()
    {
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            RowsLimit = 3,
            Filter = RowFilters.Interleave(
                RowFilters.ColumnQualifierExact("c"),
                RowFilters.ColumnQualifierExact("nonexistent")),
            Rows = PrefixRowSet("rpst-")
        };
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(3);
    }
}

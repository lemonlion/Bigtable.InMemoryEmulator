using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for batch mutation edge cases: per-entry status codes, partial failures,
/// large batches, duplicate keys, and mixed mutation types.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowsrequest
///   "Each entry is applied atomically but not across entries."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class BatchMutationStatusTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "batch-sts";

    public BatchMutationStatusTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private BigtableServiceApiClient Api => _fixture.ServiceApiClient;
    private TableName TN => _fixture.GetTableName(Table);

    #region Valid batch operations

    [Fact]
    public async Task Batch_single_entry()
    {
        var entries = new[] { Mutations.CreateEntry("bs-single", Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000))) };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "bs-single");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v");
    }

    [Fact]
    public async Task Batch_multiple_entries_different_keys()
    {
        var entries = Enumerable.Range(0, 10).Select(i =>
            Mutations.CreateEntry($"bs-multi-{i}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        for (int i = 0; i < 10; i++)
        {
            var row = await Client.ReadRowAsync(TN, $"bs-multi-{i}");
            row.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Batch_multiple_mutations_per_entry()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("bs-multmut",
                Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
                Mutations.SetCell("cf2", "c", "3", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "bs-multmut");
        row!.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Batch_same_key_multiple_entries()
    {
        // Ref: "Each entry is applied atomically but not across entries"
        // Same key in multiple entries is valid
        var entries = new[]
        {
            Mutations.CreateEntry("bs-samekey",
                Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000))),
            Mutations.CreateEntry("bs-samekey",
                Mutations.SetCell(CF, "c", "second", new BigtableVersion(2000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "bs-samekey");
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2);
    }

    #endregion

    #region Batch with deletes

    [Fact]
    public async Task Batch_set_then_delete()
    {
        // Set in one entry, delete in another
        var entries = new[]
        {
            Mutations.CreateEntry("bs-setdel",
                Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000))),
            Mutations.CreateEntry("bs-setdel",
                Mutations.DeleteFromRow())
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "bs-setdel");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Batch_delete_then_set()
    {
        await Client.MutateRowAsync(TN, "bs-delset",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));
        var entries = new[]
        {
            Mutations.CreateEntry("bs-delset", Mutations.DeleteFromRow()),
            Mutations.CreateEntry("bs-delset",
                Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "bs-delset");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Batch_delete_family_and_set_other()
    {
        await Client.MutateRowAsync(TN, "bs-delfam",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "v2", new BigtableVersion(1000)));
        var entries = new[]
        {
            Mutations.CreateEntry("bs-delfam",
                Mutations.DeleteFromFamily(CF),
                Mutations.SetCell("cf2", "c2", "new", new BigtableVersion(2000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "bs-delfam");
        row!.Families.Should().AllSatisfy(f => f.Name.Should().Be("cf2"));
    }

    #endregion

    #region Batch with column version range deletes

    [Fact]
    public async Task Batch_delete_column_version_range()
    {
        await Client.MutateRowAsync(TN, "bs-delver",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        var entries = new[]
        {
            Mutations.CreateEntry("bs-delver",
                Mutations.DeleteFromColumn(CF, "c",
                    new BigtableVersionRange(new BigtableVersion(1000), new BigtableVersion(3000))))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "bs-delver");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("v3");
    }

    #endregion

    #region Large batches

    [Fact]
    public async Task Batch_50_entries()
    {
        var entries = Enumerable.Range(0, 50).Select(i =>
            Mutations.CreateEntry($"bs-50-{i:D3}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        // Verify first and last
        var first = await Client.ReadRowAsync(TN, "bs-50-000");
        first.Should().NotBeNull();
        var last = await Client.ReadRowAsync(TN, "bs-50-049");
        last.Should().NotBeNull();
    }

    [Fact]
    public async Task Batch_100_entries()
    {
        var entries = Enumerable.Range(0, 100).Select(i =>
            Mutations.CreateEntry($"bs-100-{i:D3}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)))).ToArray();
        await Client.MutateRowsAsync(TN, entries);
        var count = 0;
        await foreach (var row in Client.ReadRows(TN,
            RowSet.FromRowRanges(RowRange.ClosedOpen("bs-100-", "bs-100."))))
            count++;
        count.Should().Be(100);
    }

    #endregion

    #region Mixed mutation types in single entry

    [Fact]
    public async Task Entry_with_set_and_delete_column()
    {
        await Client.MutateRowAsync(TN, "bs-mix1",
            Mutations.SetCell(CF, "old", "x", new BigtableVersion(1000)));
        var entries = new[]
        {
            Mutations.CreateEntry("bs-mix1",
                Mutations.DeleteFromColumn(CF, "old"),
                Mutations.SetCell(CF, "new", "y", new BigtableVersion(2000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "bs-mix1");
        row!.Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Entry_with_multiple_set_cells()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("bs-multiset",
                Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "d", "4", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "e", "5", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "bs-multiset");
        row!.Families[0].Columns.Should().HaveCount(5);
    }

    #endregion

    #region Per-entry status via raw API

    [Fact]
    public async Task Raw_api_all_valid_entries_succeed()
    {
        var request = new MutateRowsRequest
        {
            TableNameAsTableName = TN,
            Entries =
            {
                new MutateRowsRequest.Types.Entry
                {
                    RowKey = ByteString.CopyFromUtf8("bs-raw1"),
                    Mutations = { new Mutation { SetCell = new Mutation.Types.SetCell
                    {
                        FamilyName = CF, ColumnQualifier = ByteString.CopyFromUtf8("c"),
                        Value = ByteString.CopyFromUtf8("v"), TimestampMicros = 1_000_000
                    }}}
                },
                new MutateRowsRequest.Types.Entry
                {
                    RowKey = ByteString.CopyFromUtf8("bs-raw2"),
                    Mutations = { new Mutation { SetCell = new Mutation.Types.SetCell
                    {
                        FamilyName = CF, ColumnQualifier = ByteString.CopyFromUtf8("c"),
                        Value = ByteString.CopyFromUtf8("v"), TimestampMicros = 1_000_000
                    }}}
                }
            }
        };
        var stream = Api.MutateRows(request);
        var entries = new List<MutateRowsResponse.Types.Entry>();
        await foreach (var resp in stream.GetResponseStream())
            entries.AddRange(resp.Entries);
        entries.Should().HaveCount(2);
        entries.Should().OnlyContain(e => e.Status.Code == 0);
    }

    [Fact]
    public async Task Raw_api_entry_with_invalid_family_gets_error()
    {
        var request = new MutateRowsRequest
        {
            TableNameAsTableName = TN,
            Entries =
            {
                new MutateRowsRequest.Types.Entry
                {
                    RowKey = ByteString.CopyFromUtf8("bs-raw-bad"),
                    Mutations = { new Mutation { SetCell = new Mutation.Types.SetCell
                    {
                        FamilyName = "nonexistent_family", ColumnQualifier = ByteString.CopyFromUtf8("c"),
                        Value = ByteString.CopyFromUtf8("v"), TimestampMicros = 1_000_000
                    }}}
                }
            }
        };
        var stream = Api.MutateRows(request);
        var entries = new List<MutateRowsResponse.Types.Entry>();
        await foreach (var resp in stream.GetResponseStream())
            entries.AddRange(resp.Entries);
        entries.Should().ContainSingle();
        entries[0].Status.Code.Should().NotBe(0);
    }

    #endregion
}

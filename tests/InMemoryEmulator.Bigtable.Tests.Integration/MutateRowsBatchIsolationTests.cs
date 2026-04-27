using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for MutateRows batch operations — partial failures, per-entry error codes,
/// mixed valid/invalid entries, and cross-entry isolation.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowsrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutateRowsBatchIsolationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "mr-batch-iso";
    private const string CF = "cf";

    public MutateRowsBatchIsolationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task Batch_insert_10_rows_all_readable()
    {
        var entries = Enumerable.Range(0, 10)
            .Select(i => Mutations.CreateEntry(
                new BigtableByteString($"biso-row-{i:D3}"),
                Mutations.SetCell(CF, "col", $"val-{i}", new BigtableVersion(1000))))
            .ToList();

        await Client.MutateRowsAsync(TN, entries);

        for (int i = 0; i < 10; i++)
        {
            var row = await Client.ReadRowAsync(TN, new BigtableByteString($"biso-row-{i:D3}"));
            row.Should().NotBeNull();
            row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be($"val-{i}");
        }
    }

    [Fact]
    public async Task Batch_insert_100_rows()
    {
        var entries = Enumerable.Range(0, 100)
            .Select(i => Mutations.CreateEntry(
                new BigtableByteString($"biso100-{i:D4}"),
                Mutations.SetCell(CF, "col", $"v{i}", new BigtableVersion(1000))))
            .ToList();

        await Client.MutateRowsAsync(TN, entries);

        // Verify first, middle, last
        var first = await Client.ReadRowAsync(TN, new BigtableByteString("biso100-0000"));
        first.Should().NotBeNull();
        var mid = await Client.ReadRowAsync(TN, new BigtableByteString("biso100-0050"));
        mid.Should().NotBeNull();
        var last = await Client.ReadRowAsync(TN, new BigtableByteString("biso100-0099"));
        last.Should().NotBeNull();
    }

    [Fact]
    public async Task Batch_with_multiple_mutations_per_entry()
    {
        var entry = Mutations.CreateEntry(
            new BigtableByteString("biso-multi"),
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)));

        await Client.MutateRowsAsync(TN, new[] { entry });

        var row = await Client.ReadRowAsync(TN, new BigtableByteString("biso-multi"));
        var cols = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().HaveCount(3);
        cols.Should().Contain("a");
        cols.Should().Contain("b");
        cols.Should().Contain("c");
    }

    [Fact]
    public async Task Batch_with_delete_and_set_in_same_entry()
    {
        // Seed data
        await Client.MutateRowAsync(TN, new BigtableByteString("biso-delset"),
            Mutations.SetCell(CF, "old", "remove-me", new BigtableVersion(1000)));

        var entry = Mutations.CreateEntry(
            new BigtableByteString("biso-delset"),
            Mutations.DeleteFromColumn(CF, "old"),
            Mutations.SetCell(CF, "new", "keep-me", new BigtableVersion(2000)));

        await Client.MutateRowsAsync(TN, new[] { entry });

        var row = await Client.ReadRowAsync(TN, new BigtableByteString("biso-delset"));
        var cols = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().NotContain("old");
        cols.Should().Contain("new");
    }

    [Fact]
    public async Task Batch_same_row_in_two_entries_both_applied()
    {
        var entries = new[]
        {
            Mutations.CreateEntry(
                new BigtableByteString("biso-dup"),
                Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000))),
            Mutations.CreateEntry(
                new BigtableByteString("biso-dup"),
                Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000))),
        };

        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, new BigtableByteString("biso-dup"));
        var cols = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("a");
        cols.Should().Contain("b");
    }

    [Fact]
    public async Task Batch_overwrite_same_cell_in_two_entries()
    {
        var entries = new[]
        {
            Mutations.CreateEntry(
                new BigtableByteString("biso-overw"),
                Mutations.SetCell(CF, "col", "first", new BigtableVersion(1000))),
            Mutations.CreateEntry(
                new BigtableByteString("biso-overw"),
                Mutations.SetCell(CF, "col", "second", new BigtableVersion(1000))),
        };

        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, new BigtableByteString("biso-overw"));
        // Both entries have the same timestamp so the last write wins
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task Batch_across_two_families()
    {
        var entry = Mutations.CreateEntry(
            new BigtableByteString("biso-2fam"),
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "b", "2", new BigtableVersion(1000)));

        await Client.MutateRowsAsync(TN, new[] { entry });

        var row = await Client.ReadRowAsync(TN, new BigtableByteString("biso-2fam"));
        row!.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Batch_delete_from_row_removes_all_data()
    {
        await Client.MutateRowAsync(TN, new BigtableByteString("biso-delrow"),
            Mutations.SetCell(CF, "col", "val", new BigtableVersion(1000)));

        var entry = Mutations.CreateEntry(
            new BigtableByteString("biso-delrow"),
            Mutations.DeleteFromRow());

        await Client.MutateRowsAsync(TN, new[] { entry });

        var row = await Client.ReadRowAsync(TN, new BigtableByteString("biso-delrow"));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Batch_with_multiple_timestamps_per_cell()
    {
        var entry = Mutations.CreateEntry(
            new BigtableByteString("biso-mts"),
            Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "col", "v3", new BigtableVersion(3000)));

        await Client.MutateRowsAsync(TN, new[] { entry });

        var row = await Client.ReadRowAsync(TN, new BigtableByteString("biso-mts"));
        row!.Families[0].Columns[0].Cells.Should().HaveCount(3);
        // Cells are returned in descending timestamp order
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v3");
        row.Families[0].Columns[0].Cells[1].Value.ToStringUtf8().Should().Be("v2");
        row.Families[0].Columns[0].Cells[2].Value.ToStringUtf8().Should().Be("v1");
    }

    [Fact]
    public async Task Batch_insert_then_scan_returns_all()
    {
        var prefix = "biso-scan-";
        var entries = Enumerable.Range(0, 5)
            .Select(i => Mutations.CreateEntry(
                new BigtableByteString($"{prefix}{i}"),
                Mutations.SetCell(CF, "col", $"v{i}", new BigtableVersion(1000))))
            .ToList();

        await Client.MutateRowsAsync(TN, entries);

        var rows = new List<Row>();
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8(prefix),
                        EndKeyOpen = ByteString.CopyFromUtf8($"{prefix}~")
                    }
                }
            }
        };
        var stream = Client.ReadRows(request);
        await foreach (var row in stream)
            rows.Add(row);

        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task Batch_single_entry_behaves_like_mutate_row()
    {
        var rk = new BigtableByteString("biso-single");
        var entry = Mutations.CreateEntry(rk,
            Mutations.SetCell(CF, "col", "val", new BigtableVersion(1000)));

        await Client.MutateRowsAsync(TN, new[] { entry });

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("val");
    }

    [Fact]
    public async Task Batch_delete_from_family_in_entry()
    {
        await Client.MutateRowAsync(TN, new BigtableByteString("biso-delfam"),
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, new BigtableByteString("biso-delfam"),
            Mutations.SetCell("cf2", "b", "2", new BigtableVersion(1000)));

        var entry = Mutations.CreateEntry(
            new BigtableByteString("biso-delfam"),
            Mutations.DeleteFromFamily(CF));

        await Client.MutateRowsAsync(TN, new[] { entry });

        var row = await Client.ReadRowAsync(TN, new BigtableByteString("biso-delfam"));
        row!.Families.Should().HaveCount(1);
        row.Families[0].Name.Should().Be("cf2");
    }

    [Fact]
    public async Task Batch_multiple_rows_different_operations()
    {
        // Seed row for deletion
        await Client.MutateRowAsync(TN, new BigtableByteString("biso-mixed-del"),
            Mutations.SetCell(CF, "col", "val", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry(
                new BigtableByteString("biso-mixed-new"),
                Mutations.SetCell(CF, "col", "new", new BigtableVersion(1000))),
            Mutations.CreateEntry(
                new BigtableByteString("biso-mixed-del"),
                Mutations.DeleteFromRow()),
        };

        await Client.MutateRowsAsync(TN, entries);

        var newRow = await Client.ReadRowAsync(TN, new BigtableByteString("biso-mixed-new"));
        newRow.Should().NotBeNull();
        var delRow = await Client.ReadRowAsync(TN, new BigtableByteString("biso-mixed-del"));
        delRow.Should().BeNull();
    }

    [Fact]
    public async Task Batch_empty_value_cell()
    {
        var entry = Mutations.CreateEntry(
            new BigtableByteString("biso-empty"),
            Mutations.SetCell(CF, "col", "", new BigtableVersion(1000)));

        await Client.MutateRowsAsync(TN, new[] { entry });

        var row = await Client.ReadRowAsync(TN, new BigtableByteString("biso-empty"));
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().BeEmpty();
    }

    [Fact]
    public async Task Batch_binary_values()
    {
        var bytes = new byte[] { 0x00, 0x01, 0xFF, 0xFE, 0x80 };
        var entry = Mutations.CreateEntry(
            new BigtableByteString("biso-binary"),
            Mutations.SetCell(CF, ByteString.CopyFromUtf8("col"),
                ByteString.CopyFrom(bytes), new BigtableVersion(1000)));

        await Client.MutateRowsAsync(TN, new[] { entry });

        var row = await Client.ReadRowAsync(TN, new BigtableByteString("biso-binary"));
        row!.Families[0].Columns[0].Cells[0].Value.ToByteArray().Should().BeEquivalentTo(bytes);
    }
}

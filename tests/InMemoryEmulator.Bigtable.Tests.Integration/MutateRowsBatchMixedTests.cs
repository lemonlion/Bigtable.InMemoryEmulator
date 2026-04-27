using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for MutateRows (batch) with varying entry counts, error conditions,
/// and mixed mutation types per entry.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowsrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutateRowsBatchMixedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "mrb-mix-tests";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public MutateRowsBatchMixedTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Single_entry_single_mutation()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mrb-s1", Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "mrb-s1");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Single_entry_multiple_mutations()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mrb-sm",
                Mutations.SetCell(CF, "a", "va", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "b", "vb", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "c", "vc", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "mrb-sm");
        row!.Families[0].Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task Multiple_entries_same_row_key()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mrb-msr", Mutations.SetCell(CF, "a", "va", new BigtableVersion(1000))),
            Mutations.CreateEntry("mrb-msr", Mutations.SetCell(CF, "b", "vb", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "mrb-msr");
        row!.Families[0].Columns.Should().HaveCount(2);
    }

    [Fact]
    public async Task Ten_entries_different_rows()
    {
        var entries = Enumerable.Range(0, 10)
            .Select(i => Mutations.CreateEntry($"mrb-10-{i:D3}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000))))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);

        for (int i = 0; i < 10; i++)
        {
            var row = await Client.ReadRowAsync(TN, $"mrb-10-{i:D3}");
            row.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Fifty_entries_batch()
    {
        var entries = Enumerable.Range(0, 50)
            .Select(i => Mutations.CreateEntry($"mrb-50-{i:D3}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000))))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);

        // Spot check a few
        (await Client.ReadRowAsync(TN, "mrb-50-000")).Should().NotBeNull();
        (await Client.ReadRowAsync(TN, "mrb-50-025")).Should().NotBeNull();
        (await Client.ReadRowAsync(TN, "mrb-50-049")).Should().NotBeNull();
    }

    [Fact]
    public async Task Entry_with_delete_mutation()
    {
        await Client.MutateRowAsync(TN, "mrb-del",
            Mutations.SetCell(CF, "c", "to-delete", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("mrb-del", Mutations.DeleteFromRow())
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "mrb-del");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Mixed_set_and_delete_entries()
    {
        await Client.MutateRowAsync(TN, "mrb-mix-del",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("mrb-mix-del", Mutations.DeleteFromRow()),
            Mutations.CreateEntry("mrb-mix-new", Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)))
        };
        await Client.MutateRowsAsync(TN, entries);

        (await Client.ReadRowAsync(TN, "mrb-mix-del")).Should().BeNull();
        (await Client.ReadRowAsync(TN, "mrb-mix-new")).Should().NotBeNull();
    }

    [Fact]
    public async Task Entry_with_multi_family_mutations()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mrb-mf",
                Mutations.SetCell(CF, "a", "cf-val", new BigtableVersion(1000)),
                Mutations.SetCell("cf2", "b", "cf2-val", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "mrb-mf");
        row!.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Entry_with_multiple_versions_same_column()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mrb-mv",
                Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "mrb-mv");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task Entry_with_delete_column_and_set_new()
    {
        await Client.MutateRowAsync(TN, "mrb-dcn",
            Mutations.SetCell(CF, "old", "old-val", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "keep", "keep-val", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("mrb-dcn",
                Mutations.DeleteFromColumn(CF, "old"),
                Mutations.SetCell(CF, "new", "new-val", new BigtableVersion(2000)))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "mrb-dcn");
        var cols = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().NotContain("old");
        cols.Should().Contain("keep");
        cols.Should().Contain("new");
    }

    [Fact]
    public async Task Entry_with_delete_family_mutation()
    {
        await Client.MutateRowAsync(TN, "mrb-delfam",
            Mutations.SetCell(CF, "c", "cf-val", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "cf2-val", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("mrb-delfam", Mutations.DeleteFromFamily(CF))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "mrb-delfam");
        row!.Families.Should().HaveCount(1);
        row.Families[0].Name.Should().Be("cf2");
    }

    [Fact]
    public async Task Idempotent_batch_same_entries_twice()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mrb-idem",
                Mutations.SetCell(CF, "c", "val", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "mrb-idem");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
    }

    [Fact]
    public async Task Batch_creates_rows_that_are_readable_immediately()
    {
        var entries = Enumerable.Range(0, 5)
            .Select(i => Mutations.CreateEntry($"mrb-imm-{i}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000))))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("mrb-imm-"),
                        EndKeyOpen = ByteString.CopyFromUtf8("mrb-imm.")
                    }
                }
            }
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());

        keys.Should().HaveCount(5);
    }

    [Fact]
    public async Task Delete_version_range_in_batch()
    {
        await Client.MutateRowAsync(TN, "mrb-dvr",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));

        var entries = new[]
        {
            Mutations.CreateEntry("mrb-dvr",
                Mutations.DeleteFromColumn(CF, "c",
                    new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(3000))))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "mrb-dvr");
        var vals = row!.Families[0].Columns[0].Cells.Select(c => c.Value.ToStringUtf8()).ToList();
        vals.Should().Contain("v1");
        vals.Should().Contain("v3");
        vals.Should().NotContain("v2");
    }

    [Fact]
    public async Task Batch_with_empty_string_values()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mrb-empty",
                Mutations.SetCell(CF, "c", "", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "mrb-empty");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().BeEmpty();
    }

    [Fact]
    public async Task Batch_with_binary_values()
    {
        var bytes = new byte[] { 0x00, 0xDE, 0xAD, 0xBE, 0xEF };
        var entries = new[]
        {
            Mutations.CreateEntry("mrb-bin",
                Mutations.SetCell(CF, "c", ByteString.CopyFrom(bytes), new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "mrb-bin");
        row!.Families[0].Columns[0].Cells[0].Value.ToByteArray().Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public async Task Sequential_batches_accumulate()
    {
        var entries1 = new[]
        {
            Mutations.CreateEntry("mrb-acc",
                Mutations.SetCell(CF, "a", "va", new BigtableVersion(1000)))
        };
        var entries2 = new[]
        {
            Mutations.CreateEntry("mrb-acc",
                Mutations.SetCell(CF, "b", "vb", new BigtableVersion(2000)))
        };
        await Client.MutateRowsAsync(TN, entries1);
        await Client.MutateRowsAsync(TN, entries2);

        var row = await Client.ReadRowAsync(TN, "mrb-acc");
        row!.Families[0].Columns.Should().HaveCount(2);
    }
}

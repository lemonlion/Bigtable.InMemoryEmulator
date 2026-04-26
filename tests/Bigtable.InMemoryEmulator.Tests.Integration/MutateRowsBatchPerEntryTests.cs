using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Google.Rpc;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for MutateRows per-entry error handling, status codes,
/// and response structure.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowsrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutateRowsBatchPerEntryTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "mbpe-tests";
    private const string CF = "cf";

    public MutateRowsBatchPerEntryTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task All_valid_entries_succeed()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mbpe-ok-1", Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000))),
            Mutations.CreateEntry("mbpe-ok-2", Mutations.SetCell(CF, "c", "v2", new BigtableVersion(1000))),
            Mutations.CreateEntry("mbpe-ok-3", Mutations.SetCell(CF, "c", "v3", new BigtableVersion(1000)))
        };

        var response = await Client.MutateRowsAsync(TN, entries);
        response.Entries.Should().HaveCount(3);
        response.Entries.Should().OnlyContain(e => e.Status.Code == (int)Code.Ok);
    }

    [Fact]
    public async Task Response_entry_indices_match_request_order()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mbpe-idx-a", Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000))),
            Mutations.CreateEntry("mbpe-idx-b", Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000))),
            Mutations.CreateEntry("mbpe-idx-c", Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)))
        };

        var response = await Client.MutateRowsAsync(TN, entries);
        for (int i = 0; i < 3; i++)
            response.Entries[i].Index.Should().Be(i);
    }

    [Fact]
    public async Task Valid_entry_with_nonexistent_family_entry_fails_independently()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mbpe-mixed-1", Mutations.SetCell(CF, "c", "ok", new BigtableVersion(1000))),
            Mutations.CreateEntry("mbpe-mixed-2", Mutations.SetCell("nonexistent_family", "c", "fail", new BigtableVersion(1000)))
        };

        var response = await Client.MutateRowsAsync(TN, entries);
        response.Entries.Should().HaveCount(2);
        response.Entries[0].Status.Code.Should().Be((int)Code.Ok);
        response.Entries[1].Status.Code.Should().NotBe((int)Code.Ok);
    }

    [Fact]
    public async Task Failed_entry_does_not_block_succeeded_entry_data()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mbpe-partial-ok", Mutations.SetCell(CF, "c", "written", new BigtableVersion(1000))),
            Mutations.CreateEntry("mbpe-partial-fail", Mutations.SetCell("bad_family", "c", "fail", new BigtableVersion(1000)))
        };

        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "mbpe-partial-ok");
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("written");
    }

    [Fact]
    public async Task Single_entry_batch_succeeds()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mbpe-single", Mutations.SetCell(CF, "c", "solo", new BigtableVersion(1000)))
        };

        var response = await Client.MutateRowsAsync(TN, entries);
        response.Entries.Should().HaveCount(1);
        response.Entries[0].Status.Code.Should().Be((int)Code.Ok);
    }

    [Fact]
    public async Task Multiple_mutations_per_entry_all_applied()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mbpe-multi",
                Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)))
        };

        var response = await Client.MutateRowsAsync(TN, entries);
        response.Entries[0].Status.Code.Should().Be((int)Code.Ok);

        var row = await Client.ReadRowAsync(TN, "mbpe-multi");
        row!.Families[0].Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task Batch_with_delete_and_set_entries()
    {
        // Pre-seed
        await Client.MutateRowAsync(TN, "mbpe-delset",
            Mutations.SetCell(CF, "old", "data", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("mbpe-delset", Mutations.DeleteFromRow()),
            Mutations.CreateEntry("mbpe-delset-new", Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)))
        };

        await Client.MutateRowsAsync(TN, entries);

        var oldRow = await Client.ReadRowAsync(TN, "mbpe-delset");
        oldRow.Should().BeNull();

        var newRow = await Client.ReadRowAsync(TN, "mbpe-delset-new");
        newRow.Should().NotBeNull();
    }

    [Fact]
    public async Task Batch_with_delete_from_family()
    {
        await Client.MutateRowAsync(TN, "mbpe-delfam",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "b", "2", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("mbpe-delfam", Mutations.DeleteFromFamily(CF))
        };

        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "mbpe-delfam");
        row.Should().NotBeNull();
        row!.Families.Should().HaveCount(1);
        row.Families[0].Name.Should().Be("cf2");
    }

    [Fact]
    public async Task Batch_with_delete_from_column()
    {
        await Client.MutateRowAsync(TN, "mbpe-delcol",
            Mutations.SetCell(CF, "keep", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "remove", "2", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("mbpe-delcol", Mutations.DeleteFromColumn(CF, "remove"))
        };

        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "mbpe-delcol");
        row!.Families[0].Columns.Should().HaveCount(1);
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("keep");
    }

    [Fact]
    public async Task Two_entries_same_row_key_both_applied()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mbpe-samerow", Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000))),
            Mutations.CreateEntry("mbpe-samerow", Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)))
        };

        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "mbpe-samerow");
        row!.Families[0].Columns.Should().HaveCount(2);
    }

    [Fact]
    public async Task Batch_10_entries_all_succeed()
    {
        var entries = Enumerable.Range(0, 10)
            .Select(i => Mutations.CreateEntry($"mbpe-ten-{i:D2}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000))))
            .ToArray();

        var response = await Client.MutateRowsAsync(TN, entries);
        response.Entries.Should().HaveCount(10);
        response.Entries.Should().OnlyContain(e => e.Status.Code == (int)Code.Ok);
    }

    [Fact]
    public async Task Batch_50_entries_all_succeed()
    {
        var entries = Enumerable.Range(0, 50)
            .Select(i => Mutations.CreateEntry($"mbpe-fifty-{i:D3}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000))))
            .ToArray();

        var response = await Client.MutateRowsAsync(TN, entries);
        response.Entries.Should().HaveCount(50);
        response.Entries.Should().OnlyContain(e => e.Status.Code == (int)Code.Ok);
    }

    [Fact]
    public async Task Batch_creates_new_rows()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mbpe-new-1", Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000))),
            Mutations.CreateEntry("mbpe-new-2", Mutations.SetCell(CF, "c", "v2", new BigtableVersion(1000)))
        };

        await Client.MutateRowsAsync(TN, entries);

        var row1 = await Client.ReadRowAsync(TN, "mbpe-new-1");
        var row2 = await Client.ReadRowAsync(TN, "mbpe-new-2");
        row1.Should().NotBeNull();
        row2.Should().NotBeNull();
    }

    [Fact]
    public async Task Batch_overwrites_existing_version()
    {
        await Client.MutateRowAsync(TN, "mbpe-overwrite",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("mbpe-overwrite", Mutations.SetCell(CF, "c", "new", new BigtableVersion(1000)))
        };

        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "mbpe-overwrite");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Batch_adds_new_version()
    {
        await Client.MutateRowAsync(TN, "mbpe-addver",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("mbpe-addver", Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)))
        };

        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "mbpe-addver");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task Batch_set_then_delete_same_entry_order_matters()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mbpe-setdel",
                Mutations.SetCell(CF, "c", "data", new BigtableVersion(1000)),
                Mutations.DeleteFromRow())
        };

        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "mbpe-setdel");
        row.Should().BeNull(); // Delete comes after set
    }

    [Fact]
    public async Task Batch_delete_then_set_same_entry_recreates()
    {
        await Client.MutateRowAsync(TN, "mbpe-delset2",
            Mutations.SetCell(CF, "old", "old", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("mbpe-delset2",
                Mutations.DeleteFromRow(),
                Mutations.SetCell(CF, "new", "new", new BigtableVersion(2000)))
        };

        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "mbpe-delset2");
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Batch_with_binary_row_keys()
    {
        var key = ByteString.CopyFrom(0x00, 0x01, 0x02);
        var entries = new[]
        {
            Mutations.CreateEntry(new BigtableByteString(key), Mutations.SetCell(CF, "c", "binary", new BigtableVersion(1000)))
        };

        var response = await Client.MutateRowsAsync(TN, entries);
        response.Entries[0].Status.Code.Should().Be((int)Code.Ok);

        var row = await Client.ReadRowAsync(TN, new BigtableByteString(key));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Batch_with_two_families()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mbpe-2fam",
                Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
                Mutations.SetCell("cf2", "b", "2", new BigtableVersion(1000)))
        };

        var response = await Client.MutateRowsAsync(TN, entries);
        response.Entries[0].Status.Code.Should().Be((int)Code.Ok);

        var row = await Client.ReadRowAsync(TN, "mbpe-2fam");
        row!.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Batch_delete_nonexistent_row_succeeds_silently()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mbpe-del-noexist", Mutations.DeleteFromRow())
        };

        var response = await Client.MutateRowsAsync(TN, entries);
        response.Entries[0].Status.Code.Should().Be((int)Code.Ok);
    }

    [Fact]
    public async Task Batch_delete_nonexistent_column_succeeds_silently()
    {
        await Client.MutateRowAsync(TN, "mbpe-del-nocol",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("mbpe-del-nocol", Mutations.DeleteFromColumn(CF, "nonexistent_col"))
        };

        var response = await Client.MutateRowsAsync(TN, entries);
        response.Entries[0].Status.Code.Should().Be((int)Code.Ok);
    }

    [Fact]
    public async Task Batch_large_value_succeeds()
    {
        var largeValue = new string('x', 50000);
        var entries = new[]
        {
            Mutations.CreateEntry("mbpe-largeval", Mutations.SetCell(CF, "c", largeValue, new BigtableVersion(1000)))
        };

        var response = await Client.MutateRowsAsync(TN, entries);
        response.Entries[0].Status.Code.Should().Be((int)Code.Ok);
    }

    [Fact]
    public async Task Batch_entries_readable_after_success()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mbpe-read-1", Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000))),
            Mutations.CreateEntry("mbpe-read-2", Mutations.SetCell(CF, "c", "v2", new BigtableVersion(1000))),
            Mutations.CreateEntry("mbpe-read-3", Mutations.SetCell(CF, "c", "v3", new BigtableVersion(1000)))
        };

        await Client.MutateRowsAsync(TN, entries);

        for (int i = 1; i <= 3; i++)
        {
            var row = await Client.ReadRowAsync(TN, $"mbpe-read-{i}");
            row.Should().NotBeNull();
            row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be($"v{i}");
        }
    }

    [Fact]
    public async Task Batch_with_empty_value()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mbpe-emptyval", Mutations.SetCell(CF, "c", "", new BigtableVersion(1000)))
        };

        var response = await Client.MutateRowsAsync(TN, entries);
        response.Entries[0].Status.Code.Should().Be((int)Code.Ok);

        var row = await Client.ReadRowAsync(TN, "mbpe-emptyval");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().BeEmpty();
    }
}

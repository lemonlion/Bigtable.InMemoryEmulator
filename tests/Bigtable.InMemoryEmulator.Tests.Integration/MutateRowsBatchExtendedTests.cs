using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for MutateRows batch operations with various entry counts and mutation types.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowsrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutateRowsBatchExtendedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "mrbe-tests";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public MutateRowsBatchExtendedTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Batch_single_entry()
    {
        var entries = new[] { Mutations.CreateEntry("mrbe-single", Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000))) };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "mrbe-single");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Batch_10_entries()
    {
        var entries = Enumerable.Range(0, 10)
            .Select(i => Mutations.CreateEntry($"mrbe-10-{i:D2}", Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000))))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);

        for (int i = 0; i < 10; i++)
        {
            var row = await Client.ReadRowAsync(TN, $"mrbe-10-{i:D2}");
            row.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Batch_50_entries()
    {
        var entries = Enumerable.Range(0, 50)
            .Select(i => Mutations.CreateEntry($"mrbe-50-{i:D3}", Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000))))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);

        var count = 0;
        await foreach (var row in Client.ReadRows(new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.RowKeyRegex("mrbe-50-.*")
        }))
            count++;
        count.Should().Be(50);
    }

    [Fact]
    public async Task Batch_with_multiple_mutations_per_entry()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mrbe-multimut",
                Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "mrbe-multimut");
        row!.Families[0].Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task Batch_with_deletes()
    {
        await Client.MutateRowAsync(TN, "mrbe-del1",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "mrbe-del2",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("mrbe-del1", Mutations.DeleteFromRow()),
            Mutations.CreateEntry("mrbe-del2", Mutations.DeleteFromRow())
        };
        await Client.MutateRowsAsync(TN, entries);

        (await Client.ReadRowAsync(TN, "mrbe-del1")).Should().BeNull();
        (await Client.ReadRowAsync(TN, "mrbe-del2")).Should().BeNull();
    }

    [Fact]
    public async Task Batch_mixed_write_and_delete()
    {
        await Client.MutateRowAsync(TN, "mrbe-mixdel",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("mrbe-mixdel", Mutations.DeleteFromRow()),
            Mutations.CreateEntry("mrbe-mixnew", Mutations.SetCell(CF, "c", "new", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);

        (await Client.ReadRowAsync(TN, "mrbe-mixdel")).Should().BeNull();
        (await Client.ReadRowAsync(TN, "mrbe-mixnew")).Should().NotBeNull();
    }

    [Fact]
    public async Task Batch_overwrite_existing_rows()
    {
        await Client.MutateRowAsync(TN, "mrbe-over",
            Mutations.SetCell(CF, "c", "original", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("mrbe-over", Mutations.SetCell(CF, "c", "updated", new BigtableVersion(2000)))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "mrbe-over");
        row!.Families[0].Columns[0].Cells.OrderByDescending(c => c.TimestampMicros)
            .First().Value.ToStringUtf8().Should().Be("updated");
    }

    [Fact]
    public async Task Batch_cross_family_writes()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mrbe-cross",
                Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
                Mutations.SetCell("cf2", "c", "v2", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var row = await Client.ReadRowAsync(TN, "mrbe-cross");
        row!.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Batch_delete_from_family()
    {
        await Client.MutateRowAsync(TN, "mrbe-delfam",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "v", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("mrbe-delfam", Mutations.DeleteFromFamily(CF))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "mrbe-delfam");
        row!.Families.Should().ContainSingle().Which.Name.Should().Be("cf2");
    }

    [Fact]
    public async Task Batch_delete_from_column()
    {
        await Client.MutateRowAsync(TN, "mrbe-delcol",
            Mutations.SetCell(CF, "keep", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "remove", "v", new BigtableVersion(1000)));

        var entries = new[]
        {
            Mutations.CreateEntry("mrbe-delcol", Mutations.DeleteFromColumn(CF, "remove"))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "mrbe-delcol");
        row!.Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("keep");
    }

    [Fact]
    public async Task Batch_set_and_delete_same_row()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mrbe-setdel",
                Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
                Mutations.DeleteFromColumn(CF, "a"))
        };
        await Client.MutateRowsAsync(TN, entries);
        // Mutations are applied in order: set then delete
        var row = await Client.ReadRowAsync(TN, "mrbe-setdel");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Batch_100_entries()
    {
        var entries = Enumerable.Range(0, 100)
            .Select(i => Mutations.CreateEntry($"mrbe-100-{i:D3}", Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000))))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);

        var count = 0;
        await foreach (var row in Client.ReadRows(new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.RowKeyRegex("mrbe-100-.*")
        }))
            count++;
        count.Should().Be(100);
    }

    [Fact]
    public async Task Batch_with_version_timestamp()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mrbe-ts",
                Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
                Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
                Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "mrbe-ts");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task Batch_idempotent_same_key_same_ts()
    {
        var entries1 = new[]
        {
            Mutations.CreateEntry("mrbe-idem", Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000)))
        };
        var entries2 = new[]
        {
            Mutations.CreateEntry("mrbe-idem", Mutations.SetCell(CF, "c", "second", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries1);
        await Client.MutateRowsAsync(TN, entries2);

        var row = await Client.ReadRowAsync(TN, "mrbe-idem");
        // Same timestamp → overwrite
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task Batch_multiple_rows_same_family_different_columns()
    {
        var entries = Enumerable.Range(0, 5)
            .Select(i => Mutations.CreateEntry($"mrbe-diffcol-{i}",
                Mutations.SetCell(CF, $"col-{i}", $"val-{i}", new BigtableVersion(1000))))
            .ToArray();
        await Client.MutateRowsAsync(TN, entries);

        for (int i = 0; i < 5; i++)
        {
            var row = await Client.ReadRowAsync(TN, $"mrbe-diffcol-{i}");
            row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be($"col-{i}");
        }
    }
}

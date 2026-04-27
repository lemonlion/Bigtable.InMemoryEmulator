using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Comprehensive delete semantics integration tests — DeleteFromRow, DeleteFromFamily,
/// DeleteFromColumn with time ranges, interactions with reads/writes.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class DeleteSemanticsIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "delete-tests";
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public DeleteSemanticsIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    #region DeleteFromRow

    [Fact]
    public async Task DeleteFromRow_removes_all_families()
    {
        var rk = new BigtableByteString("del-row-all");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "v3", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromRow());

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().BeNull();
    }

    [Fact]
    public async Task DeleteFromRow_removes_all_versions()
    {
        var rk = new BigtableByteString("del-row-ver");
        for (int i = 1; i <= 5; i++)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
        }

        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromRow());

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().BeNull();
    }

    [Fact]
    public async Task DeleteFromRow_on_nonexistent_row_is_noop()
    {
        var rk = new BigtableByteString("del-row-noop");
        // Should not throw
        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromRow());
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().BeNull();
    }

    #endregion

    #region DeleteFromFamily

    [Fact]
    public async Task DeleteFromFamily_removes_all_columns_in_family()
    {
        var rk = new BigtableByteString("del-fam-cols");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "d", "v4", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromFamily(CF));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.Should().ContainSingle().Which.Name.Should().Be(CF2);
    }

    [Fact]
    public async Task DeleteFromFamily_removes_all_versions()
    {
        var rk = new BigtableByteString("del-fam-ver");
        for (int i = 1; i <= 3; i++)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
        }
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF2, "c", "keeper", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromFamily(CF));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.Should().NotContain(f => f.Name == CF);
    }

    [Fact]
    public async Task DeleteFromFamily_all_families_makes_row_invisible()
    {
        var rk = new BigtableByteString("del-fam-all");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "v2", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromFamily(CF),
            Mutations.DeleteFromFamily(CF2));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().BeNull();
    }

    #endregion

    #region DeleteFromColumn

    [Fact]
    public async Task DeleteFromColumn_removes_all_versions_of_one_column()
    {
        var rk = new BigtableByteString("del-col-all");
        for (int i = 1; i <= 3; i++)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, "target", $"v{i}", new BigtableVersion(i * 1000)),
                Mutations.SetCell(CF, "other", $"o{i}", new BigtableVersion(i * 1000)));
        }

        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromColumn(CF, "target"));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        var cols = row!.Families.First(f => f.Name == CF).Columns;
        cols.Should().ContainSingle().Which.Qualifier.ToStringUtf8().Should().Be("other");
    }

    [Fact]
    public async Task DeleteFromColumn_with_time_range_inclusive_start()
    {
        // Ref: DeleteFromColumn time_range: [start_timestamp, end_timestamp)
        var rk = new BigtableByteString("del-col-ts");
        for (int i = 1; i <= 5; i++)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
        }

        // Delete versions [2000, 4000) — ts 2000 and 3000
        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(4000))));

        var row = await Client.ReadRowAsync(TN, rk);
        var timestamps = row!.Families[0].Columns[0].Cells
            .Select(c => c.TimestampMicros / 1000).ToList(); // back to ms
        timestamps.Should().Contain(1000);
        timestamps.Should().Contain(4000);
        timestamps.Should().Contain(5000);
        timestamps.Should().NotContain(2000);
        timestamps.Should().NotContain(3000);
    }

    [Fact]
    public async Task DeleteFromColumn_unbounded_start()
    {
        var rk = new BigtableByteString("del-col-unb-start");
        for (int i = 1; i <= 5; i++)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
        }

        // Delete all versions before ts 3000: [0, 3000) where 0 = unbounded start
        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(default, new BigtableVersion(3000))));

        var row = await Client.ReadRowAsync(TN, rk);
        var timestamps = row!.Families[0].Columns[0].Cells
            .Select(c => c.TimestampMicros / 1000).ToList();
        timestamps.Should().Contain(3000);
        timestamps.Should().Contain(4000);
        timestamps.Should().Contain(5000);
    }

    [Fact]
    public async Task DeleteFromColumn_unbounded_end()
    {
        var rk = new BigtableByteString("del-col-unb-end");
        for (int i = 1; i <= 5; i++)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
        }

        // Delete all versions from ts 3000 onwards: [3000, 0) where 0 end = unbounded
        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(3000), default)));

        var row = await Client.ReadRowAsync(TN, rk);
        var timestamps = row!.Families[0].Columns[0].Cells
            .Select(c => c.TimestampMicros / 1000).ToList();
        timestamps.Should().Contain(1000);
        timestamps.Should().Contain(2000);
        timestamps.Should().NotContain(3000);
    }

    [Fact]
    public async Task DeleteFromColumn_single_version()
    {
        var rk = new BigtableByteString("del-col-single");
        for (int i = 1; i <= 3; i++)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
        }

        // Delete exactly version at ts 2000: [2000, 2001)
        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(2001))));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task DeleteFromColumn_nonexistent_column_is_noop()
    {
        var rk = new BigtableByteString("del-col-noop");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "existing", "v1", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "nonexistent"));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("existing");
    }

    #endregion

    #region Delete + Write sequences

    [Fact]
    public async Task Delete_row_then_rewrite()
    {
        var rk = new BigtableByteString("del-rw-1");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromRow());
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v2");
    }

    [Fact]
    public async Task Delete_column_then_rewrite_same_timestamp()
    {
        var rk = new BigtableByteString("del-col-rw");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "original", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "c"));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "rewritten", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("rewritten");
    }

    [Fact]
    public async Task Multiple_deletes_are_idempotent()
    {
        var rk = new BigtableByteString("del-idempotent");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromRow());
        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromRow());
        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromRow());

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().BeNull();
    }

    [Fact]
    public async Task Set_and_delete_in_same_mutation()
    {
        // Set a cell and delete another column in the same mutation call
        var rk = new BigtableByteString("del-same-mut");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "keep", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "remove", "v2", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "keep", "v2", new BigtableVersion(2000)),
            Mutations.DeleteFromColumn(CF, "remove"));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("keep");
    }

    [Fact]
    public async Task Delete_family_and_set_cell_in_that_family_same_mutation()
    {
        // Delete family then set cell in same family (in same mutation)
        var rk = new BigtableByteString("del-fam-set");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "old", "v1", new BigtableVersion(1000)));

        // Delete family then add new cell - mutations applied in order
        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromFamily(CF),
            Mutations.SetCell(CF, "new", "v2", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        var cols = row!.Families[0].Columns;
        cols.Should().ContainSingle().Which.Qualifier.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Delete_row_and_set_cell_same_mutation()
    {
        var rk = new BigtableByteString("del-row-set");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "old", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "old", "v2", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromRow(),
            Mutations.SetCell(CF, "new", "v3", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.Should().ContainSingle().Which.Name.Should().Be(CF);
        row.Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("new");
    }

    #endregion

    #region Delete with reads

    [Fact]
    public async Task Deleted_row_not_in_ReadRows_results()
    {
        await Client.MutateRowAsync(TN, "del-rr-1",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "del-rr-2",
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "del-rr-3",
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "del-rr-2", Mutations.DeleteFromRow());

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN,
            RowSet.FromRowKeys("del-rr-1", "del-rr-2", "del-rr-3")))
        {
            rows.Add(row);
        }
        rows.Should().HaveCount(2);
        rows.Select(r => r.Key.ToStringUtf8()).Should().Equal("del-rr-1", "del-rr-3");
    }

    [Fact]
    public async Task Deleted_column_not_visible_in_read()
    {
        var rk = new BigtableByteString("del-col-read");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn(CF, "a"));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("b");
    }

    #endregion
}

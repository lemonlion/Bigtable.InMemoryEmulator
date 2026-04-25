using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for Upsert (write-or-update) patterns via MutateRow.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowrequest
///   "Cells already present in a row are left unchanged unless explicitly changed by mutation."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class UpsertPatternTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "upsert";

    public UpsertPatternTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task First_write_creates_row()
    {
        await Client.MutateRowAsync(TN, "up-r1",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "up-r1");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Second_write_same_timestamp_overwrites()
    {
        await Client.MutateRowAsync(TN, "up-r2",
            Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "up-r2",
            Mutations.SetCell(CF, "c", "second", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "up-r2");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task Second_write_new_timestamp_adds_version()
    {
        await Client.MutateRowAsync(TN, "up-r3",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "up-r3",
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "up-r3");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task Write_new_column_preserves_existing()
    {
        await Client.MutateRowAsync(TN, "up-r4",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "up-r4",
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "up-r4");
        row!.Families[0].Columns.Should().HaveCount(2);
    }

    [Fact]
    public async Task Overwrite_then_read_latest()
    {
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "up-r5",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
        var row = await Client.ReadRowAsync(TN, "up-r5",
            RowFilters.CellsPerColumnLimit(1));
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v5");
    }

    [Fact]
    public async Task Overwrite_empty_value()
    {
        await Client.MutateRowAsync(TN, "up-r6",
            Mutations.SetCell(CF, "c", "data", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "up-r6",
            Mutations.SetCell(CF, "c", "", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "up-r6");
        row!.Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task Overwrite_with_binary_value()
    {
        await Client.MutateRowAsync(TN, "up-r7",
            Mutations.SetCell(CF, "c", "text", new BigtableVersion(1000)));
        var binVal = ByteString.CopyFrom(new byte[] { 0x00, 0xFF, 0x42 });
        await Client.MutateRowAsync(TN, "up-r7",
            Mutations.SetCell(CF, "c", binVal, new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "up-r7");
        row!.Families[0].Columns[0].Cells[0].Value.ToByteArray().Should().BeEquivalentTo(new byte[] { 0x00, 0xFF, 0x42 });
    }

    [Fact]
    public async Task Multiple_overwrites_same_timestamp_last_wins()
    {
        for (int i = 0; i < 10; i++)
            await Client.MutateRowAsync(TN, "up-r8",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "up-r8");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v9");
    }

    [Fact]
    public async Task Concurrent_columns_independent()
    {
        await Client.MutateRowAsync(TN, "up-r9",
            Mutations.SetCell(CF, "a", "a1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "b1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "up-r9",
            Mutations.SetCell(CF, "a", "a2", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "up-r9");
        var colA = row!.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "a");
        var colB = row.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "b");
        colA.Cells[0].Value.ToStringUtf8().Should().Be("a2");
        colB.Cells[0].Value.ToStringUtf8().Should().Be("b1");
    }
}

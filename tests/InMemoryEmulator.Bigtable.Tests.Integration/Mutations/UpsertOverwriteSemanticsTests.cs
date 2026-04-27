using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class UpsertOverwriteSemanticsTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "upsert-sem";
    private const string CF = "cf";

    public UpsertOverwriteSemanticsTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Same_timestamp_overwrites_value()
    {
        await Client.MutateRowAsync(TN, "r1", Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "r1", Mutations.SetCell(CF, "c", "v2", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "r1");
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("v2");
    }

    [Fact]
    public async Task Different_timestamps_create_versions()
    {
        await Client.MutateRowAsync(TN, "r2", Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "r2", Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "r2");
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(2);
    }

    [Fact]
    public async Task Latest_version_comes_first()
    {
        await Client.MutateRowAsync(TN, "r3",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "r3");
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.First().Value.ToStringUtf8().Should().Be("new");
        cells.Last().Value.ToStringUtf8().Should().Be("old");
    }

    [Fact]
    public async Task Multiple_columns_independent()
    {
        await Client.MutateRowAsync(TN, "r4",
            Mutations.SetCell(CF, "a", "1"),
            Mutations.SetCell(CF, "b", "2"));
        await Client.MutateRowAsync(TN, "r4", Mutations.SetCell(CF, "a", "updated"));
        var row = await Client.ReadRowAsync(TN, "r4");
        var cols = row!.Families.SelectMany(f => f.Columns).ToList();
        cols.Should().HaveCount(2);
    }

    [Fact]
    public async Task Overwrite_with_empty_value()
    {
        await Client.MutateRowAsync(TN, "r5", Mutations.SetCell(CF, "c", "notempty", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "r5", Mutations.SetCell(CF, "c", "", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "r5");
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("");
    }

    [Fact]
    public async Task Overwrite_preserves_other_versions()
    {
        await Client.MutateRowAsync(TN, "r6",
            Mutations.SetCell(CF, "c", "keep", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "replace", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, "r6", Mutations.SetCell(CF, "c", "replaced", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "r6");
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(2);
        cells[0].Value.ToStringUtf8().Should().Be("replaced");
        cells[1].Value.ToStringUtf8().Should().Be("keep");
    }

    [Fact]
    public async Task Batch_same_row_same_timestamp()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("r7", Mutations.SetCell(CF, "c", "batch1", new BigtableVersion(1000))),
        };
        await Client.MutateRowsAsync(TN, entries);
        var entries2 = new[]
        {
            Mutations.CreateEntry("r7", Mutations.SetCell(CF, "c", "batch2", new BigtableVersion(1000))),
        };
        await Client.MutateRowsAsync(TN, entries2);
        var row = await Client.ReadRowAsync(TN, "r7");
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("batch2");
    }

    [Fact]
    public async Task Write_many_versions_read_all()
    {
        for (int i = 1; i <= 10; i++)
            await Client.MutateRowAsync(TN, "r8", Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
        var row = await Client.ReadRowAsync(TN, "r8");
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(10);
        cells.First().Value.ToStringUtf8().Should().Be("v10");
    }

    [Fact]
    public async Task Overwrite_all_versions_one_by_one()
    {
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "r9", Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "r9", Mutations.SetCell(CF, "c", $"u{i}", new BigtableVersion(i * 1000)));
        var row = await Client.ReadRowAsync(TN, "r9");
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(5);
        cells.First().Value.ToStringUtf8().Should().Be("u5");
    }

    [Fact]
    public async Task ReadModifyWrite_append_creates_new_version()
    {
        await Client.MutateRowAsync(TN, "r10", Mutations.SetCell(CF, "c", "hello", new BigtableVersion(1000)));
        await Client.ReadModifyWriteRowAsync(TN, "r10", ReadModifyWriteRules.Append(CF, "c", " world"));
        var row = await Client.ReadRowAsync(TN, "r10");
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        // RMW creates a new cell version with appended value
        cells.First().Value.ToStringUtf8().Should().Be("hello world");
    }

    [Fact]
    public async Task ReadModifyWrite_increment_creates_new_version()
    {
        var bytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes, 100);
        await Client.MutateRowAsync(TN, "r11", Mutations.SetCell(CF, "c", ByteString.CopyFrom(bytes), new BigtableVersion(1000)));
        await Client.ReadModifyWriteRowAsync(TN, "r11", ReadModifyWriteRules.Increment(CF, "c", 50));
        var row = await Client.ReadRowAsync(TN, "r11");
        var val = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).First().Value;
        System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(val.Span).Should().Be(150);
    }

    [Fact]
    public async Task Set_cell_with_binary_value()
    {
        var data = new byte[] { 0x00, 0xFF, 0x01, 0xFE };
        await Client.MutateRowAsync(TN, "r12", Mutations.SetCell(CF, "c", ByteString.CopyFrom(data), new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "r12");
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToByteArray().Should().BeEquivalentTo(data);
    }

    [Fact]
    public async Task Set_cell_with_large_value()
    {
        var largeVal = new string('x', 5000);
        await Client.MutateRowAsync(TN, "r13", Mutations.SetCell(CF, "c", largeVal, new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "r13");
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().HaveLength(5000);
    }
}

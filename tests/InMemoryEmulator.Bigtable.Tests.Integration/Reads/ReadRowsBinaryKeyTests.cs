using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsBinaryKeyTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "rr-binkey";
    private const string CF = "cf";

    public ReadRowsBinaryKeyTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        // Write rows with binary keys
        await Client.MutateRowAsync(TN, new BigtableByteString(new byte[] { 0x00 }), Mutations.SetCell(CF, "c", "zero"));
        await Client.MutateRowAsync(TN, new BigtableByteString(new byte[] { 0x01 }), Mutations.SetCell(CF, "c", "one"));
        await Client.MutateRowAsync(TN, new BigtableByteString(new byte[] { 0xFF }), Mutations.SetCell(CF, "c", "max"));
        await Client.MutateRowAsync(TN, new BigtableByteString(new byte[] { 0x00, 0x01 }), Mutations.SetCell(CF, "c", "compound"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Read_binary_key_row()
    {
        var row = await Client.ReadRowAsync(TN, new BigtableByteString(new byte[] { 0x00 }));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("zero");
    }

    [Fact]
    public async Task Read_0xFF_key()
    {
        var row = await Client.ReadRowAsync(TN, new BigtableByteString(new byte[] { 0xFF }));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("max");
    }

    [Fact]
    public async Task Binary_keys_sorted()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN)) rows.Add(r);
        rows.Should().HaveCount(4);
        // Binary sort: 0x00 < 0x00,0x01 < 0x01 < 0xFF
        var keys = rows.Select(r => r.Key.ToByteArray()).ToList();
        keys[0].Should().BeEquivalentTo(new byte[] { 0x00 });
        keys[1].Should().BeEquivalentTo(new byte[] { 0x00, 0x01 });
        keys[2].Should().BeEquivalentTo(new byte[] { 0x01 });
        keys[3].Should().BeEquivalentTo(new byte[] { 0xFF });
    }

    [Fact]
    public async Task Range_with_binary_keys()
    {
        var rowSet = new RowSet
        {
            RowRanges = { new RowRange
            {
                StartKeyClosed = ByteString.CopyFrom(new byte[] { 0x00 }),
                EndKeyOpen = ByteString.CopyFrom(new byte[] { 0x01 })
            }}
        };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Should().HaveCount(2); // 0x00 and 0x00,0x01
    }

    [Fact]
    public async Task Specific_binary_keys()
    {
        var rowSet = new RowSet
        {
            RowKeys = {
                ByteString.CopyFrom(new byte[] { 0x01 }),
                ByteString.CopyFrom(new byte[] { 0xFF })
            }
        };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: rowSet)) rows.Add(r);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Missing_binary_key()
    {
        var row = await Client.ReadRowAsync(TN, new BigtableByteString(new byte[] { 0x02 }));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Delete_binary_key_row()
    {
        await Client.MutateRowAsync(TN, new BigtableByteString(new byte[] { 0x01 }), Mutations.DeleteFromRow());
        var row = await Client.ReadRowAsync(TN, new BigtableByteString(new byte[] { 0x01 }));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Compound_binary_key()
    {
        var row = await Client.ReadRowAsync(TN, new BigtableByteString(new byte[] { 0x00, 0x01 }));
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("compound");
    }

    [Fact]
    public async Task Filter_on_binary_key_rows()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.CellsPerRowLimit(1)))
            rows.Add(r);
        rows.Should().HaveCount(4);
    }

    [Fact]
    public async Task Binary_key_with_value_filter()
    {
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.ValueExact("zero")))
            rows.Add(r);
        rows.Should().ContainSingle();
    }
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowEmptyValueTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "rr-emptyv";
    private const string CF = "cf";

    public ReadRowEmptyValueTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Write_and_read_empty_value()
    {
        await Client.MutateRowAsync(TN, "r1", Mutations.SetCell(CF, "c", ""));
        var row = await Client.ReadRowAsync(TN, "r1");
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Empty_value_in_scan()
    {
        await Client.MutateRowAsync(TN, "r2", Mutations.SetCell(CF, "c", ""));
        await Client.MutateRowAsync(TN, "r3", Mutations.SetCell(CF, "c", "notempty"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN)) rows.Add(r);
        rows.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Empty_value_exact_filter()
    {
        await Client.MutateRowAsync(TN, "r4", Mutations.SetCell(CF, "c", ""));
        var row = await Client.ReadRowAsync(TN, "r4", RowFilters.ValueExact(""));
        row.Should().NotBeNull();
    }
}

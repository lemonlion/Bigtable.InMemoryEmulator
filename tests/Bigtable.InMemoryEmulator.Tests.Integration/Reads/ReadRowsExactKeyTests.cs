using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsExactKeyTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "exact-key";
    private const string CF = "cf";

    public ReadRowsExactKeyTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        for (int i = 0; i < 10; i++)
            await Client.MutateRowAsync(TN, $"row-{i:D2}", Mutations.SetCell(CF, "c", $"v{i}"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task ReadRow_single_key()
    {
        var row = await Client.ReadRowAsync(TN, "row-05");
        row.Should().NotBeNull();
        row!.Key.ToStringUtf8().Should().Be("row-05");
    }

    [Fact]
    public async Task ReadRow_nonexistent_key()
    {
        var row = await Client.ReadRowAsync(TN, "nonexistent");
        row.Should().BeNull();
    }

    [Fact]
    public async Task ReadRows_multiple_exact_keys()
    {
        var rowSet = RowSet.FromRowKeys("row-01", "row-05", "row-09");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rowSet)) rows.Add(r);
        rows.Should().HaveCount(3);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeEquivalentTo(new[] { "row-01", "row-05", "row-09" });
    }

    [Fact]
    public async Task ReadRows_duplicate_keys_returns_once()
    {
        var rowSet = RowSet.FromRowKeys("row-03", "row-03", "row-03");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rowSet)) rows.Add(r);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task ReadRows_mix_existing_and_missing()
    {
        var rowSet = RowSet.FromRowKeys("row-00", "missing", "row-09");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rowSet)) rows.Add(r);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReadRows_empty_key_set()
    {
        // No keys specified — should return all rows
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN)) rows.Add(r);
        rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task ReadRows_keys_returned_sorted()
    {
        var rowSet = RowSet.FromRowKeys("row-09", "row-01", "row-05");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rowSet)) rows.Add(r);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task ReadRows_with_filter_and_keys()
    {
        var rowSet = RowSet.FromRowKeys("row-00", "row-01", "row-02");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rowSet, RowFilters.ValueRegex("v[02]")))
            rows.Add(r);
        rows.Should().HaveCount(2); // v0, v2
    }

    [Fact]
    public async Task ReadRows_with_limit_and_keys()
    {
        var rowSet = RowSet.FromRowKeys("row-00", "row-01", "row-02", "row-03");
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rowSet, rowsLimit: 2))
            rows.Add(r);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReadRow_with_strip_value_filter()
    {
        var row = await Client.ReadRowAsync(TN, "row-05", RowFilters.StripValueTransformer());
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadRow_correct_value()
    {
        var row = await Client.ReadRowAsync(TN, "row-07");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("v7");
    }
}

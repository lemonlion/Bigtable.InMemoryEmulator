using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class StripValueFilterBehaviorTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "sv-beh";
    private const string CF = "cf";

    public StripValueFilterBehaviorTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        await Client.MutateRowAsync(TN, "r1",
            Mutations.SetCell(CF, "name", "Alice"),
            Mutations.SetCell(CF, "age", "30"));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task StripValue_returns_empty_values()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.StripValueTransformer());
        row.Should().NotBeNull();
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(2);
        foreach (var cell in cells)
            cell.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task StripValue_preserves_columns()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.StripValueTransformer());
        var colNames = row!.Families.SelectMany(f => f.Columns).Select(c => c.Qualifier.ToStringUtf8()).ToList();
        colNames.Should().Contain("name").And.Contain("age");
    }

    [Fact]
    public async Task StripValue_preserves_timestamps()
    {
        await Client.MutateRowAsync(TN, "r2", Mutations.SetCell(CF, "c", "v", new BigtableVersion(5000)));
        var row = await Client.ReadRowAsync(TN, "r2", RowFilters.StripValueTransformer());
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().TimestampMicros.Should().Be(5000000);
    }

    [Fact]
    public async Task StripValue_in_chain()
    {
        var chain = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("name"),
            RowFilters.StripValueTransformer());
        var row = await Client.ReadRowAsync(TN, "r1", chain);
        row.Should().NotBeNull();
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.Should().BeEmpty();
    }

    [Fact]
    public async Task StripValue_across_rows()
    {
        await Client.MutateRowAsync(TN, "r3", Mutations.SetCell(CF, "c", "data"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, filter: RowFilters.StripValueTransformer()))
            rows.Add(r);
        foreach (var row in rows)
            foreach (var cell in row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells))
                cell.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task StripValue_on_missing_row()
    {
        var row = await Client.ReadRowAsync(TN, "missing", RowFilters.StripValueTransformer());
        row.Should().BeNull();
    }

    [Fact]
    public async Task StripValue_preserves_family()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.StripValueTransformer());
        row!.Families.Should().ContainSingle().Which.Name.Should().Be(CF);
    }

    [Fact]
    public async Task StripValue_with_multiple_versions()
    {
        await Client.MutateRowAsync(TN, "r4",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "r4", RowFilters.StripValueTransformer());
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(2);
        cells.All(c => c.Value.IsEmpty).Should().BeTrue();
    }

    [Fact]
    public async Task StripValue_with_binary_value()
    {
        await Client.MutateRowAsync(TN, "r5", Mutations.SetCell(CF, "c", ByteString.CopyFrom(new byte[] { 0xFF, 0x00 }), new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "r5", RowFilters.StripValueTransformer());
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.Should().BeEmpty();
    }
}

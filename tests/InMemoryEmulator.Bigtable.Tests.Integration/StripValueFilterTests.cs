using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for RowFilters.StripValueTransformer().
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
///   "Replaces each cell's value with the empty string."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class StripValueFilterTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "strip-val";

    public StripValueFilterTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task Strip_value_returns_empty_bytes()
    {
        await Client.MutateRowAsync(TN, "sv-r1",
            Mutations.SetCell(CF, "c", "hello", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "sv-r1",
            RowFilters.StripValueTransformer());
        row!.Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task Strip_value_preserves_row_key()
    {
        await Client.MutateRowAsync(TN, "sv-r2",
            Mutations.SetCell(CF, "c", "data", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "sv-r2",
            RowFilters.StripValueTransformer());
        row!.Key.ToStringUtf8().Should().Be("sv-r2");
    }

    [Fact]
    public async Task Strip_value_preserves_family()
    {
        await Client.MutateRowAsync(TN, "sv-r3",
            Mutations.SetCell(CF, "c", "data", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "sv-r3",
            RowFilters.StripValueTransformer());
        row!.Families[0].Name.Should().Be(CF);
    }

    [Fact]
    public async Task Strip_value_preserves_qualifier()
    {
        await Client.MutateRowAsync(TN, "sv-r4",
            Mutations.SetCell(CF, "qual", "data", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "sv-r4",
            RowFilters.StripValueTransformer());
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("qual");
    }

    [Fact]
    public async Task Strip_value_preserves_timestamp()
    {
        await Client.MutateRowAsync(TN, "sv-r5",
            Mutations.SetCell(CF, "c", "data", new BigtableVersion(5000)));
        var row = await Client.ReadRowAsync(TN, "sv-r5",
            RowFilters.StripValueTransformer());
        row!.Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(5_000_000);
    }

    [Fact]
    public async Task Strip_value_applies_to_all_cells()
    {
        await Client.MutateRowAsync(TN, "sv-r6",
            Mutations.SetCell(CF, "a", "val-a", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "val-b", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "sv-r6",
            RowFilters.StripValueTransformer());
        foreach (var col in row!.Families[0].Columns)
            foreach (var cell in col.Cells)
                cell.Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task Strip_value_applies_to_multiple_versions()
    {
        await Client.MutateRowAsync(TN, "sv-r7",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "sv-r7",
            RowFilters.StripValueTransformer());
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
        foreach (var cell in row.Families[0].Columns[0].Cells)
            cell.Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task Strip_value_in_chain_with_other_filter()
    {
        await Client.MutateRowAsync(TN, "sv-r8",
            Mutations.SetCell(CF, "c", "keep-me", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "drop-me", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "sv-r8",
            RowFilters.Chain(RowFilters.CellsPerColumnLimit(1), RowFilters.StripValueTransformer()));
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task Strip_value_on_binary_data()
    {
        var binVal = ByteString.CopyFrom(new byte[] { 0xFF, 0x00, 0x42 });
        await Client.MutateRowAsync(TN, "sv-r9",
            Mutations.SetCell(CF, "c", binVal, new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "sv-r9",
            RowFilters.StripValueTransformer());
        row!.Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task Strip_value_on_already_empty_value()
    {
        await Client.MutateRowAsync(TN, "sv-r10",
            Mutations.SetCell(CF, "c", "", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "sv-r10",
            RowFilters.StripValueTransformer());
        row!.Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }
}

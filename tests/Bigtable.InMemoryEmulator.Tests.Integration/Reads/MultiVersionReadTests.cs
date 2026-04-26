using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MultiVersionReadTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "mv-read";
    private const string CF = "cf";

    public MultiVersionReadTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        for (int i = 1; i <= 10; i++)
            await Client.MutateRowAsync(TN, "r1", Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Read_all_versions()
    {
        var row = await Client.ReadRowAsync(TN, "r1");
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(10);
    }

    [Fact]
    public async Task Versions_newest_first()
    {
        var row = await Client.ReadRowAsync(TN, "r1");
        var vals = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Select(c => c.Value.ToStringUtf8()).ToList();
        vals.First().Should().Be("v10");
        vals.Last().Should().Be("v1");
    }

    [Fact]
    public async Task CellsPerColumnLimit_2()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.CellsPerColumnLimit(2));
        var vals = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Select(c => c.Value.ToStringUtf8()).ToList();
        vals.Should().BeEquivalentTo(new[] { "v10", "v9" });
    }

    [Fact]
    public async Task TimestampRange_middle()
    {
        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                StartTimestampMicros = 3000000,
                EndTimestampMicros = 7000000
            }
        };
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(4); // v3,v4,v5,v6
    }

    [Fact]
    public async Task CellsPerColumnLimit_returns_latest()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.CellsPerColumnLimit(1));
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("v10");
    }

    [Fact]
    public async Task Timestamps_are_in_micros()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.CellsPerColumnLimit(1));
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().TimestampMicros.Should().Be(10000000);
    }

    [Fact]
    public async Task CellsPerRowLimit_on_multi_version()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.CellsPerRowLimit(3));
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(3);
    }

    [Fact]
    public async Task CellsPerRowOffset_on_multi_version()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.CellsPerRowOffset(8));
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(2); // v2,v1
    }

    [Fact]
    public async Task Chain_timestamp_then_limit()
    {
        var chain = RowFilters.Chain(
            new RowFilter { TimestampRangeFilter = new TimestampRange { StartTimestampMicros = 5000000, EndTimestampMicros = 11000000 } },
            RowFilters.CellsPerColumnLimit(3));
        var row = await Client.ReadRowAsync(TN, "r1", chain);
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(3);
    }

    [Fact]
    public async Task Value_regex_across_versions()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.ValueRegex("v[12]"));
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(2);
    }

    [Fact]
    public async Task Multi_column_multi_version()
    {
        for (int i = 1; i <= 3; i++)
            await Client.MutateRowAsync(TN, "r2",
                Mutations.SetCell(CF, "a", $"a{i}", new BigtableVersion(i * 1000)),
                Mutations.SetCell(CF, "b", $"b{i}", new BigtableVersion(i * 1000)));
        var row = await Client.ReadRowAsync(TN, "r2");
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(2);
        foreach (var col in row.Families.SelectMany(f => f.Columns))
            col.Cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task Delete_version_range_then_read()
    {
        await Client.MutateRowAsync(TN, "r1",
            Mutations.DeleteFromColumn(CF, "c",
                new BigtableVersionRange(new BigtableVersion(3000), new BigtableVersion(6000))));
        var row = await Client.ReadRowAsync(TN, "r1");
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(7); // removed v3,v4,v5
    }

    [Fact]
    public async Task StripValue_preserves_version_count()
    {
        var row = await Client.ReadRowAsync(TN, "r1", RowFilters.StripValueTransformer());
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(10);
    }
}

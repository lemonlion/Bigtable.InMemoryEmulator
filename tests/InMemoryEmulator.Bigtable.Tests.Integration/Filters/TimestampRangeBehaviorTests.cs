using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class TimestampRangeBehaviorTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "ts-rng-beh";
    private const string CF = "cf";

    public TimestampRangeBehaviorTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "r1", Mutations.SetCell(CF, "col", $"v{i}", new BigtableVersion(i * 1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private RowFilter TsRange(long startMicros, long endMicros) =>
        new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                StartTimestampMicros = startMicros,
                EndTimestampMicros = endMicros
            }
        };

    [Fact]
    public async Task Range_includes_start_excludes_end()
    {
        var row = await Client.ReadRowAsync(TN, "r1", TsRange(2000000, 4000000));
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().HaveCount(2); // 2000 and 3000 (in micros: 2000000, 3000000)
    }

    [Fact]
    public async Task Range_covering_all()
    {
        var row = await Client.ReadRowAsync(TN, "r1", TsRange(0, 6000000));
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(5);
    }

    [Fact]
    public async Task Range_no_match()
    {
        var row = await Client.ReadRowAsync(TN, "r1", TsRange(10000000, 20000000));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Range_single_timestamp()
    {
        var row = await Client.ReadRowAsync(TN, "r1", TsRange(3000000, 4000000));
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Should().ContainSingle().Which.Value.ToStringUtf8().Should().Be("v3");
    }

    [Fact]
    public async Task Range_open_start()
    {
        // Only end specified = from beginning
        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange { EndTimestampMicros = 3000000 }
        };
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(2); // v1, v2
    }

    [Fact]
    public async Task Range_open_end()
    {
        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange { StartTimestampMicros = 4000000 }
        };
        var row = await Client.ReadRowAsync(TN, "r1", filter);
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(2); // v4, v5
    }

    [Fact]
    public async Task Range_with_column_filter()
    {
        await Client.MutateRowAsync(TN, "r2",
            Mutations.SetCell(CF, "a", "a1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "a2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "b", "b1", new BigtableVersion(1000)));
        var chain = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("a"),
            TsRange(1000000, 2000000));
        var row = await Client.ReadRowAsync(TN, "r2", chain);
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Should().ContainSingle().Which.Value.ToStringUtf8().Should().Be("a1");
    }

    [Fact]
    public async Task Range_across_rows()
    {
        await Client.MutateRowAsync(TN, "r3", Mutations.SetCell(CF, "c", "early", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "r4", Mutations.SetCell(CF, "c", "late", new BigtableVersion(5000)));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, rows: RowSet.FromRowKeys("r3", "r4"), filter: TsRange(4000000, 6000000)))
            rows.Add(r);
        rows.Should().ContainSingle().Which.Key.ToStringUtf8().Should().Be("r4");
    }

    [Fact]
    public async Task Range_on_missing_row()
    {
        var row = await Client.ReadRowAsync(TN, "missing", TsRange(0, 10000000));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Range_preserves_ordering()
    {
        var row = await Client.ReadRowAsync(TN, "r1", TsRange(0, 6000000));
        var timestamps = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Select(c => c.TimestampMicros).ToList();
        timestamps.Should().BeInDescendingOrder();
    }
}

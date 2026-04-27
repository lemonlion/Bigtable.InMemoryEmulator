using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class VersioningAndTimestampTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "vat-tests";
    private const string CF = "cf";

    public VersioningAndTimestampTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Explicit_version_preserved()
    {
        var rk = "vat-explicit";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "val", new BigtableVersion(5000)));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().TimestampMicros.Should().Be(5_000_000);
    }

    [Fact]
    public async Task Multiple_versions_same_column()
    {
        var rk = "vat-multi";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "col", "v3", new BigtableVersion(3000)));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(3);
    }

    [Fact]
    public async Task Latest_version_returned_first()
    {
        var rk = "vat-order";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "oldest", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col", "newest", new BigtableVersion(3000)),
            Mutations.SetCell(CF, "col", "middle", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, rk);
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells[0].Value.ToStringUtf8().Should().Be("newest");
        cells[1].Value.ToStringUtf8().Should().Be("middle");
        cells[2].Value.ToStringUtf8().Should().Be("oldest");
    }

    [Fact]
    public async Task CellsPerColumnLimit_returns_latest()
    {
        var rk = "vat-limit";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "old", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col", "new", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, rk, RowFilters.CellsPerColumnLimit(1));
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Same_version_overwrites()
    {
        var rk = "vat-overwrite";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "first", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "second", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, rk);
        var cells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        cells.Should().ContainSingle();
        cells[0].Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task TimestampRange_filter_inclusive_exclusive()
    {
        var rk = "vat-tsrange";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "col", "v3", new BigtableVersion(3000)));

        // [1_000_000, 3_000_000) = v1 and v2
        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                StartTimestampMicros = 1_000_000,
                EndTimestampMicros = 3_000_000
            }
        };
        var row = await Client.ReadRowAsync(TN, rk, filter);
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(2);
    }

    [Fact]
    public async Task TimestampRange_no_match()
    {
        var rk = "vat-tsnomatch";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "val", new BigtableVersion(5000)));
        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                StartTimestampMicros = 1_000_000,
                EndTimestampMicros = 2_000_000
            }
        };
        var row = await Client.ReadRowAsync(TN, rk, filter);
        row.Should().BeNull();
    }

    [Fact]
    public async Task Server_assigned_timestamp_is_nonzero()
    {
        var rk = "vat-server-ts";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "val"));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().TimestampMicros.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Server_assigned_timestamp_is_millisecond_aligned()
    {
        var rk = "vat-ms-align";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "val"));
        var row = await Client.ReadRowAsync(TN, rk);
        var ts = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Single().TimestampMicros;
        (ts % 1000).Should().Be(0);
    }

    [Fact]
    public async Task Ten_versions_all_preserved()
    {
        var rk = "vat-ten";
        for (int i = 1; i <= 10; i++)
            await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", $"v{i}", new BigtableVersion(i * 1000)));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(10);
    }

    [Fact]
    public async Task CellsPerColumnLimit_2_returns_two_latest()
    {
        var rk = "vat-limit2";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "col", "v3", new BigtableVersion(3000)));
        var row = await Client.ReadRowAsync(TN, rk, RowFilters.CellsPerColumnLimit(2));
        var values = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Select(c => c.Value.ToStringUtf8()).ToList();
        values.Should().HaveCount(2);
        values.Should().Contain("v3");
        values.Should().Contain("v2");
    }

    [Fact]
    public async Task Version_1_is_minimum()
    {
        var rk = "vat-min";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "min", new BigtableVersion(1)));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().TimestampMicros.Should().Be(1000); // Version(1) = 1ms = 1000µs
    }

    [Fact]
    public async Task Large_version_number()
    {
        var rk = "vat-large";
        var version = new BigtableVersion(1_000_000_000); // 1 billion ms
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "col", "big", version));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().TimestampMicros.Should().Be(1_000_000_000_000);
    }

    [Fact]
    public async Task Multiple_columns_each_with_versions()
    {
        var rk = "vat-multicol";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "a1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "a2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "b", "b1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "b2", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).Should().HaveCount(4);
    }
}

using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for ValueRange filtering — closed, open, half-open, and edge cases.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#valuerange
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ValueRangeBoundaryTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public ValueRangeBoundaryTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync("valrange", new[] { CF });
        // Seed rows with values: "a" through "z"
        for (int i = 0; i < 26; i++)
        {
            char c = (char)('a' + i);
            await Client.MutateRowAsync(TN, $"row-{c:D2}",
                Mutations.SetCell(CF, "val", c.ToString(), new BigtableVersion(1000)));
        }
    }
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("valrange");

    private async Task<List<string>> ReadValues(RowFilter filter)
    {
        var values = new List<string>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter))
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    foreach (var cell in col.Cells)
                        values.Add(cell.Value.ToStringUtf8());
        return values;
    }

    #region Closed range

    [Fact]
    public async Task Closed_range_a_to_e()
    {
        var filter = RowFilters.ValueRange(ValueRange.Closed("a", "e"));
        var vals = await ReadValues(filter);
        vals.Should().HaveCount(5);
        vals.Should().Contain("a").And.Contain("e");
    }

    [Fact]
    public async Task Closed_range_single_value()
    {
        var filter = RowFilters.ValueRange(ValueRange.Closed("m", "m"));
        var vals = await ReadValues(filter);
        vals.Should().ContainSingle().Which.Should().Be("m");
    }

    [Fact]
    public async Task Closed_range_x_to_z()
    {
        var filter = RowFilters.ValueRange(ValueRange.Closed("x", "z"));
        var vals = await ReadValues(filter);
        vals.Should().HaveCount(3);
    }

    #endregion

    #region Open range

    [Fact]
    public async Task Open_range_excludes_endpoints()
    {
        var filter = RowFilters.ValueRange(ValueRange.Open("a", "e"));
        var vals = await ReadValues(filter);
        vals.Should().HaveCount(3); // b, c, d
        vals.Should().NotContain("a").And.NotContain("e");
    }

    [Fact]
    public async Task Open_range_same_endpoints_empty()
    {
        var filter = RowFilters.ValueRange(ValueRange.Open("m", "m"));
        var vals = await ReadValues(filter);
        vals.Should().BeEmpty();
    }

    #endregion

    #region ClosedOpen range

    [Fact]
    public async Task ClosedOpen_includes_start_excludes_end()
    {
        var filter = RowFilters.ValueRange(ValueRange.ClosedOpen("g", "k"));
        var vals = await ReadValues(filter);
        vals.Should().HaveCount(4); // g, h, i, j
        vals.Should().Contain("g").And.NotContain("k");
    }

    [Fact]
    public async Task ClosedOpen_single_gap()
    {
        var filter = RowFilters.ValueRange(ValueRange.ClosedOpen("a", "b"));
        var vals = await ReadValues(filter);
        vals.Should().ContainSingle().Which.Should().Be("a");
    }

    #endregion

    #region OpenClosed range

    [Fact]
    public async Task OpenClosed_excludes_start_includes_end()
    {
        var filter = RowFilters.ValueRange(ValueRange.OpenClosed("g", "k"));
        var vals = await ReadValues(filter);
        vals.Should().HaveCount(4); // h, i, j, k
        vals.Should().NotContain("g").And.Contain("k");
    }

    [Fact]
    public async Task OpenClosed_single_gap()
    {
        var filter = RowFilters.ValueRange(ValueRange.OpenClosed("a", "b"));
        var vals = await ReadValues(filter);
        vals.Should().ContainSingle().Which.Should().Be("b");
    }

    #endregion

    #region No match

    [Fact]
    public async Task Range_outside_all_values_returns_empty()
    {
        var filter = RowFilters.ValueRange(ValueRange.Closed("0", "9"));
        var vals = await ReadValues(filter);
        vals.Should().BeEmpty();
    }

    [Fact]
    public async Task Range_after_all_values_returns_empty()
    {
        var filter = RowFilters.ValueRange(ValueRange.Closed("{", "~"));
        var vals = await ReadValues(filter);
        vals.Should().BeEmpty();
    }

    #endregion

    #region Full range

    [Fact]
    public async Task Closed_a_to_z_returns_all_26()
    {
        var filter = RowFilters.ValueRange(ValueRange.Closed("a", "z"));
        var vals = await ReadValues(filter);
        vals.Should().HaveCount(26);
    }

    #endregion

    #region Combined with other filters

    [Fact]
    public async Task ValueRange_with_RowKeyRegex()
    {
        var filter = RowFilters.Chain(
            RowFilters.RowKeyRegex("row-a.*"),
            RowFilters.ValueRange(ValueRange.Closed("a", "z")));
        var vals = await ReadValues(filter);
        // row-a has value "a"
        vals.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task ValueRange_with_ColumnQualifier()
    {
        // Add a second column to one row
        await Client.MutateRowAsync(TN, "row-a",
            Mutations.SetCell(CF, "extra", "zzz", new BigtableVersion(1000)));
        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("val"),
            RowFilters.ValueRange(ValueRange.Closed("a", "c")));
        var vals = await ReadValues(filter);
        vals.Should().HaveCount(3); // a, b, c from column "val" only
    }

    [Fact]
    public async Task ValueRange_with_limit()
    {
        var filter = RowFilters.ValueRange(ValueRange.Closed("a", "z"));
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter, rowsLimit: 5))
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    foreach (var cell in col.Cells)
                        vals.Add(cell.Value.ToStringUtf8());
        vals.Should().HaveCount(5);
    }

    #endregion

    #region Numeric byte values

    [Fact]
    public async Task ValueRange_with_numeric_byte_values()
    {
        // Write cells with big-endian int64 values
        for (int i = 0; i < 10; i++)
        {
            var bytes = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes, i * 100);
            await Client.MutateRowAsync(TN, $"num-{i:D2}",
                Mutations.SetCell(CF, "num", ByteString.CopyFrom(bytes), new BigtableVersion(1000)));
        }

        // Read with a range that includes values 0-400 (bytes up to 400-BE)
        var startBytes = new byte[8];
        var endBytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(startBytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(endBytes, 500);

        var filter = RowFilters.Chain(
            RowFilters.ColumnQualifierExact("num"),
            RowFilters.ValueRange(ValueRange.Closed(
                ByteString.CopyFrom(startBytes),
                ByteString.CopyFrom(endBytes))));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: null, filter))
            rows.Add(row);
        rows.Should().HaveCountGreaterThanOrEqualTo(5); // 0, 100, 200, 300, 400
    }

    #endregion
}

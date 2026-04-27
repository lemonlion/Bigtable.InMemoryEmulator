using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for value encoding edge cases: empty values, binary, large values,
/// null handling, and value filter interactions.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#cell
///   "value: Uninterpreted bytes. Any encoding restrictions are set by a specific Bigtable feature."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ValueEncodingEdgeCaseTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "val-enc";

    public ValueEncodingEdgeCaseTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    #region Empty values

    [Fact]
    public async Task Empty_string_value()
    {
        await Client.MutateRowAsync(TN, "ve-empty",
            Mutations.SetCell(CF, "c", "", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "ve-empty");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().BeEmpty();
    }

    [Fact]
    public async Task Empty_bytes_value()
    {
        await Client.MutateRowAsync(TN, "ve-emptybytes",
            Mutations.SetCell(CF, "c", ByteString.Empty, new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "ve-emptybytes");
        row!.Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task Value_filter_matches_empty()
    {
        await Client.MutateRowAsync(TN, "ve-filter-empty",
            Mutations.SetCell(CF, "c", "", new BigtableVersion(1000)));
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("ve-filter-empty"),
            filter: RowFilters.ValueRegex("")))
        {
            row.Families[0].Columns[0].Cells.Should().HaveCount(1);
        }
    }

    #endregion

    #region Binary values

    [Fact]
    public async Task All_byte_values()
    {
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        await Client.MutateRowAsync(TN, "ve-allbytes",
            Mutations.SetCell(CF, "c", ByteString.CopyFrom(bytes), new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "ve-allbytes");
        row!.Families[0].Columns[0].Cells[0].Value.ToByteArray().Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public async Task Null_byte_in_value()
    {
        var bytes = new byte[] { 0x41, 0x00, 0x42, 0x00, 0x43 }; // A\0B\0C
        await Client.MutateRowAsync(TN, "ve-null",
            Mutations.SetCell(CF, "c", ByteString.CopyFrom(bytes), new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "ve-null");
        row!.Families[0].Columns[0].Cells[0].Value.ToByteArray().Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public async Task Single_byte_value()
    {
        await Client.MutateRowAsync(TN, "ve-1byte",
            Mutations.SetCell(CF, "c", ByteString.CopyFrom(new byte[] { 0xFF }), new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "ve-1byte");
        row!.Families[0].Columns[0].Cells[0].Value.ToByteArray().Should().BeEquivalentTo(new byte[] { 0xFF });
    }

    [Fact]
    public async Task Int64_big_endian_value_roundtrip()
    {
        var val = 123456789L;
        var bytes = BitConverter.GetBytes(val);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        await Client.MutateRowAsync(TN, "ve-int64",
            Mutations.SetCell(CF, "c", ByteString.CopyFrom(bytes), new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "ve-int64");
        var readBytes = row!.Families[0].Columns[0].Cells[0].Value.ToByteArray();
        if (BitConverter.IsLittleEndian) Array.Reverse(readBytes);
        BitConverter.ToInt64(readBytes, 0).Should().Be(val);
    }

    #endregion

    #region Unicode values

    [Fact]
    public async Task Ascii_value()
    {
        await Client.MutateRowAsync(TN, "ve-ascii",
            Mutations.SetCell(CF, "c", "Hello, World!", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "ve-ascii");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("Hello, World!");
    }

    [Fact]
    public async Task Emoji_value()
    {
        var val = "🎉🙂🎊";
        await Client.MutateRowAsync(TN, "ve-emoji",
            Mutations.SetCell(CF, "c", val, new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "ve-emoji");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be(val);
    }

    [Fact]
    public async Task CJK_value()
    {
        var val = "中文日本語한국어";
        await Client.MutateRowAsync(TN, "ve-cjk",
            Mutations.SetCell(CF, "c", val, new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "ve-cjk");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be(val);
    }

    [Fact]
    public async Task Mixed_scripts_value()
    {
        var val = "Hello мир 世界 🌍";
        await Client.MutateRowAsync(TN, "ve-mixed",
            Mutations.SetCell(CF, "c", val, new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "ve-mixed");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be(val);
    }

    #endregion

    #region Large values

    [Fact]
    public async Task Value_10kb()
    {
        var val = new string('X', 10 * 1024);
        await Client.MutateRowAsync(TN, "ve-10k",
            Mutations.SetCell(CF, "c", val, new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "ve-10k");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().HaveLength(10 * 1024);
    }

    [Fact]
    public async Task Value_30kb()
    {
        var val = new string('Y', 30 * 1024);
        await Client.MutateRowAsync(TN, "ve-30k",
            Mutations.SetCell(CF, "c", val, new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "ve-30k");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().HaveLength(30 * 1024);
    }

    [Fact]
    public async Task Value_large_binary_pattern()
    {
        // Test a large binary value with diverse byte patterns
        var bytes = new byte[50 * 1024];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 256);
        await Client.MutateRowAsync(TN, "ve-bigbin",
            Mutations.SetCell(CF, "c", ByteString.CopyFrom(bytes), new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "ve-bigbin");
        row!.Families[0].Columns[0].Cells[0].Value.Length.Should().Be(50 * 1024);
    }

    #endregion

    #region Value regex filter edge cases

    [Fact]
    public async Task ValueRegex_dot_star_matches_all()
    {
        await Client.MutateRowAsync(TN, "ve-rx1",
            Mutations.SetCell(CF, "c", "anything", new BigtableVersion(1000)));
        var found = false;
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("ve-rx1"),
            filter: RowFilters.ValueRegex(".*")))
            found = true;
        found.Should().BeTrue();
    }

    [Fact]
    public async Task ValueRegex_exact_match()
    {
        await Client.MutateRowAsync(TN, "ve-rx2",
            Mutations.SetCell(CF, "c", "exact", new BigtableVersion(1000)));
        var found = false;
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("ve-rx2"),
            filter: RowFilters.ValueRegex("exact")))
            found = true;
        found.Should().BeTrue();
    }

    [Fact]
    public async Task ValueRegex_no_match()
    {
        await Client.MutateRowAsync(TN, "ve-rx3",
            Mutations.SetCell(CF, "c", "hello", new BigtableVersion(1000)));
        var found = false;
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("ve-rx3"),
            filter: RowFilters.ValueRegex("goodbye")))
            found = true;
        found.Should().BeFalse();
    }

    [Fact]
    public async Task ValueRegex_partial_does_not_match()
    {
        // RE2 is full match (anchored), "ell" should NOT match "hello"
        await Client.MutateRowAsync(TN, "ve-rx4",
            Mutations.SetCell(CF, "c", "hello", new BigtableVersion(1000)));
        var found = false;
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("ve-rx4"),
            filter: RowFilters.ValueRegex("ell")))
            found = true;
        found.Should().BeFalse();
    }

    [Fact]
    public async Task ValueRegex_with_wildcards()
    {
        await Client.MutateRowAsync(TN, "ve-rx5",
            Mutations.SetCell(CF, "c", "hello world", new BigtableVersion(1000)));
        var found = false;
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("ve-rx5"),
            filter: RowFilters.ValueRegex("hello.*")))
            found = true;
        found.Should().BeTrue();
    }

    [Fact]
    public async Task ValueRange_closed_open()
    {
        await Client.MutateRowAsync(TN, "ve-vr1",
            Mutations.SetCell(CF, "c", "b", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "d", "d", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "a", new BigtableVersion(1000)));
        var cells = new List<string>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("ve-vr1"),
            filter: RowFilters.ValueRange(ValueRange.ClosedOpen("a", "d"))))
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    foreach (var cell in col.Cells)
                        cells.Add(cell.Value.ToStringUtf8());
        cells.Should().Contain("a").And.Contain("b");
        cells.Should().NotContain("d");
    }

    #endregion

    #region Multiple values same column

    [Fact]
    public async Task Different_values_different_timestamps()
    {
        await Client.MutateRowAsync(TN, "ve-multi",
            Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "second", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "third", new BigtableVersion(3000)));
        var row = await Client.ReadRowAsync(TN, "ve-multi");
        var cells = row!.Families[0].Columns[0].Cells;
        cells[0].Value.ToStringUtf8().Should().Be("third");
        cells[1].Value.ToStringUtf8().Should().Be("second");
        cells[2].Value.ToStringUtf8().Should().Be("first");
    }

    [Fact]
    public async Task Overwrite_value_at_same_timestamp()
    {
        await Client.MutateRowAsync(TN, "ve-overwrite",
            Mutations.SetCell(CF, "c", "original", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ve-overwrite",
            Mutations.SetCell(CF, "c", "replaced", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "ve-overwrite");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("replaced");
    }

    #endregion
}

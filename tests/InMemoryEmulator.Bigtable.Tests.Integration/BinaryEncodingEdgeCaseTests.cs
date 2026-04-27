using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for binary key and value encoding edge cases — null bytes, high bytes,
/// Unicode, and mixed binary patterns.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#cell
/// "Values are stored as raw bytes without encoding."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class BinaryEncodingEdgeCaseTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "bin-enc";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public BinaryEncodingEdgeCaseTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Null_byte_in_value()
    {
        var bytes = new byte[] { 0x41, 0x00, 0x42 }; // A\0B
        await Client.MutateRowAsync(TN, "bin-null",
            Mutations.SetCell(CF, "c", ByteString.CopyFrom(bytes), new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "bin-null");
        row!.Families[0].Columns[0].Cells[0].Value.ToByteArray().Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public async Task All_byte_values()
    {
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        await Client.MutateRowAsync(TN, "bin-allbytes",
            Mutations.SetCell(CF, "c", ByteString.CopyFrom(bytes), new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "bin-allbytes");
        row!.Families[0].Columns[0].Cells[0].Value.ToByteArray().Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public async Task Empty_byte_array()
    {
        await Client.MutateRowAsync(TN, "bin-empty",
            Mutations.SetCell(CF, "c", ByteString.Empty, new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "bin-empty");
        row!.Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task Utf8_multibyte_value()
    {
        var text = "Hello 世界 🌍";
        await Client.MutateRowAsync(TN, "bin-utf8",
            Mutations.SetCell(CF, "c", text, new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "bin-utf8");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be(text);
    }

    [Fact]
    public async Task Binary_row_key()
    {
        var keyBytes = new byte[] { 0x01, 0x02, 0xFF };
        await Client.MutateRowAsync(TN, ByteString.CopyFrom(keyBytes),
            Mutations.SetCell(CF, "c", "val", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFrom(keyBytes) } }
        };
        var keys = new List<ByteString>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key);

        keys.Should().HaveCount(1);
        keys[0].ToByteArray().Should().BeEquivalentTo(keyBytes);
    }

    [Fact]
    public async Task Binary_column_qualifier()
    {
        var colBytes = ByteString.CopyFrom(new byte[] { 0xCA, 0xFE });
        await Client.MutateRowAsync(TN, "bin-colq",
            Mutations.SetCell(CF, colBytes, ByteString.CopyFromUtf8("val"), new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "bin-colq");
        row!.Families[0].Columns[0].Qualifier.ToByteArray().Should().BeEquivalentTo(new byte[] { 0xCA, 0xFE });
    }

    [Fact]
    public async Task Int64_big_endian_value()
    {
        long val = 1234567890L;
        var bytes = BitConverter.GetBytes(val);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);

        await Client.MutateRowAsync(TN, "bin-int64",
            Mutations.SetCell(CF, "c", ByteString.CopyFrom(bytes), new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "bin-int64");
        var readBytes = row!.Families[0].Columns[0].Cells[0].Value.ToByteArray();
        if (BitConverter.IsLittleEndian) Array.Reverse(readBytes);
        BitConverter.ToInt64(readBytes, 0).Should().Be(val);
    }

    [Fact]
    public async Task Value_with_repeated_null_bytes()
    {
        var bytes = new byte[100]; // all zeros
        await Client.MutateRowAsync(TN, "bin-zeros",
            Mutations.SetCell(CF, "c", ByteString.CopyFrom(bytes), new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "bin-zeros");
        row!.Families[0].Columns[0].Cells[0].Value.Length.Should().Be(100);
    }

    [Fact]
    public async Task Value_with_0xFF_bytes()
    {
        var bytes = Enumerable.Repeat((byte)0xFF, 50).ToArray();
        await Client.MutateRowAsync(TN, "bin-ff",
            Mutations.SetCell(CF, "c", ByteString.CopyFrom(bytes), new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "bin-ff");
        row!.Families[0].Columns[0].Cells[0].Value.ToByteArray().Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public async Task Multiple_binary_columns_same_row()
    {
        var bytes1 = new byte[] { 0x01, 0x02 };
        var bytes2 = new byte[] { 0x03, 0x04 };
        var bytes3 = new byte[] { 0x05, 0x06 };

        await Client.MutateRowAsync(TN, "bin-multi",
            Mutations.SetCell(CF, "c1", ByteString.CopyFrom(bytes1), new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c2", ByteString.CopyFrom(bytes2), new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c3", ByteString.CopyFrom(bytes3), new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "bin-multi");
        row!.Families[0].Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task Binary_value_versions()
    {
        for (int i = 0; i < 5; i++)
        {
            var bytes = new byte[] { (byte)(i * 10), (byte)(i * 10 + 1) };
            await Client.MutateRowAsync(TN, "bin-ver",
                Mutations.SetCell(CF, "c", ByteString.CopyFrom(bytes), new BigtableVersion((i + 1) * 1000)));
        }

        var row = await Client.ReadRowAsync(TN, "bin-ver");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(5);
    }

    [Fact]
    public async Task ReadModifyWrite_append_binary()
    {
        var initial = ByteString.CopyFrom(new byte[] { 0x01 });
        var append = ByteString.CopyFrom(new byte[] { 0x02 });

        await Client.MutateRowAsync(TN, "bin-rmw-app",
            Mutations.SetCell(CF, "c", initial, new BigtableVersion(1000)));

        await Client.ReadModifyWriteRowAsync(TN, "bin-rmw-app",
            ReadModifyWriteRules.Append(CF, "c", append));

        var row = await Client.ReadRowAsync(TN, "bin-rmw-app");
        var val = row!.Families[0].Columns
            .First(c => c.Qualifier.ToStringUtf8() == "c")
            .Cells.OrderByDescending(c => c.TimestampMicros).First()
            .Value.ToByteArray();
        val.Should().Contain(0x01);
        val.Should().Contain(0x02);
    }

    [Fact]
    public async Task Value_filter_on_string_data()
    {
        // Use string values for exact value matching to avoid binary regex issues
        await Client.MutateRowAsync(TN, "bin-vf-match",
            Mutations.SetCell(CF, "c", "match-target", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "bin-vf-other",
            Mutations.SetCell(CF, "c", "other-value", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.ValueExact("match-target")
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());

        keys.Should().ContainSingle().Which.Should().Be("bin-vf-match");
    }

    [Fact]
    public async Task Special_chars_in_string_value()
    {
        var value = "tab\there\nnewline\rcarriage\"quote\\backslash";
        await Client.MutateRowAsync(TN, "bin-special",
            Mutations.SetCell(CF, "c", value, new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "bin-special");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be(value);
    }

    [Fact]
    public async Task Very_long_column_qualifier()
    {
        var longQualifier = new string('x', 1000);
        await Client.MutateRowAsync(TN, "bin-longcol",
            Mutations.SetCell(CF, longQualifier, "val", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "bin-longcol");
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be(longQualifier);
    }
}

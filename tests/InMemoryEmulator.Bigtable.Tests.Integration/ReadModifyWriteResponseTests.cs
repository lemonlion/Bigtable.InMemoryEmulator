using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for ReadModifyWrite response verification and consistency patterns.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readmodifywriterowresponse
///   "Response message for Bigtable.ReadModifyWriteRow."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadModifyWriteResponseTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "rmw-resp";
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public ReadModifyWriteResponseTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    private async Task<List<Row>> ReadAll(RowSet? rows = null, RowFilter? filter = null)
    {
        var list = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, rows: rows, filter: filter))
            list.Add(row);
        return list;
    }

    #region Response contains correct data

    [Fact]
    public async Task Increment_response_contains_new_value()
    {
        await Client.MutateRowAsync(TN, "rmwr-inc",
            Mutations.SetCell(CF, "counter",
                ByteString.CopyFrom(BitConverter.GetBytes(10L).Reverse().ToArray()),
                new BigtableVersion(1000)));
        var response = await Client.ReadModifyWriteRowAsync(TN, "rmwr-inc",
            ReadModifyWriteRules.Increment(CF, "counter", 5));
        var cell = response.Row.Families.First(f => f.Name == CF)
            .Columns.First(c => c.Qualifier.ToStringUtf8() == "counter").Cells[0];
        var val = BitConverter.ToInt64(cell.Value.ToByteArray().Reverse().ToArray());
        val.Should().Be(15);
    }

    [Fact]
    public async Task Append_response_contains_new_value()
    {
        await Client.MutateRowAsync(TN, "rmwr-app",
            Mutations.SetCell(CF, "data", "hello", new BigtableVersion(1000)));
        var response = await Client.ReadModifyWriteRowAsync(TN, "rmwr-app",
            ReadModifyWriteRules.Append(CF, "data", " world"));
        var cell = response.Row.Families.First(f => f.Name == CF)
            .Columns.First(c => c.Qualifier.ToStringUtf8() == "data").Cells[0];
        cell.Value.ToStringUtf8().Should().Be("hello world");
    }

    [Fact]
    public async Task Multi_rule_response_contains_all_results()
    {
        await Client.MutateRowAsync(TN, "rmwr-multi",
            Mutations.SetCell(CF, "name", "Alice", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "counter",
                ByteString.CopyFrom(BitConverter.GetBytes(0L).Reverse().ToArray()),
                new BigtableVersion(1000)));
        var response = await Client.ReadModifyWriteRowAsync(TN, "rmwr-multi",
            ReadModifyWriteRules.Append(CF, "name", " Bob"),
            ReadModifyWriteRules.Increment(CF, "counter", 1));
        var nameCol = response.Row.Families.First(f => f.Name == CF)
            .Columns.First(c => c.Qualifier.ToStringUtf8() == "name");
        nameCol.Cells[0].Value.ToStringUtf8().Should().Be("Alice Bob");
        var counterCol = response.Row.Families.First(f => f.Name == CF)
            .Columns.First(c => c.Qualifier.ToStringUtf8() == "counter");
        BitConverter.ToInt64(counterCol.Cells[0].Value.ToByteArray().Reverse().ToArray()).Should().Be(1);
    }

    [Fact]
    public async Task Response_row_key_matches()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "rmwr-key",
            ReadModifyWriteRules.Append(CF, "c", "v"));
        response.Row.Key.ToStringUtf8().Should().Be("rmwr-key");
    }

    #endregion

    #region Cross-family RMW

    [Fact]
    public async Task RMW_across_families_in_one_call()
    {
        await Client.MutateRowAsync(TN, "rmwr-xf",
            Mutations.SetCell(CF, "a", "hello", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "b", "world", new BigtableVersion(1000)));
        var response = await Client.ReadModifyWriteRowAsync(TN, "rmwr-xf",
            ReadModifyWriteRules.Append(CF, "a", "!"),
            ReadModifyWriteRules.Append(CF2, "b", "!"));
        response.Row.Families.Should().HaveCount(2);
        response.Row.Families.First(f => f.Name == CF).Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("hello!");
        response.Row.Families.First(f => f.Name == CF2).Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("world!");
    }

    #endregion

    #region Sequential increments

    [Fact]
    public async Task Sequential_increments_accumulate()
    {
        await Client.MutateRowAsync(TN, "rmwr-seqinc",
            Mutations.SetCell(CF, "c",
                ByteString.CopyFrom(BitConverter.GetBytes(0L).Reverse().ToArray()),
                new BigtableVersion(1000)));
        for (int i = 1; i <= 5; i++)
        {
            var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwr-seqinc",
                ReadModifyWriteRules.Increment(CF, "c", i));
            var val = BitConverter.ToInt64(resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray());
            val.Should().Be(i * (i + 1) / 2); // sum of 1..i
        }
    }

    [Fact]
    public async Task Sequential_appends_accumulate()
    {
        var key = "rmwr-seqapp";
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF, "c", "", new BigtableVersion(1000)));
        for (int i = 0; i < 5; i++)
        {
            var resp = await Client.ReadModifyWriteRowAsync(TN, key,
                ReadModifyWriteRules.Append(CF, "c", $"{i}"));
            resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().HaveLength(i + 1);
        }
        var rows = await ReadAll(RowSet.FromRowKeys(key), RowFilters.CellsPerColumnLimit(1));
        rows[0].Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("01234");
    }

    #endregion

    #region RMW on new rows

    [Fact]
    public async Task Increment_on_new_row()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwr-newi",
            ReadModifyWriteRules.Increment(CF, "c", 42));
        var val = BitConverter.ToInt64(resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray());
        val.Should().Be(42);
    }

    [Fact]
    public async Task Append_on_new_row()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwr-newa",
            ReadModifyWriteRules.Append(CF, "c", "first"));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("first");
    }

    [Fact]
    public async Task Mixed_RMW_on_new_row()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwr-newm",
            ReadModifyWriteRules.Append(CF, "text", "hello"),
            ReadModifyWriteRules.Increment(CF, "count", 1));
        var textCol = resp.Row.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "text");
        textCol.Cells[0].Value.ToStringUtf8().Should().Be("hello");
        var countCol = resp.Row.Families[0].Columns.First(c => c.Qualifier.ToStringUtf8() == "count");
        BitConverter.ToInt64(countCol.Cells[0].Value.ToByteArray().Reverse().ToArray()).Should().Be(1);
    }

    #endregion

    #region Negative increments

    [Fact]
    public async Task Negative_increment()
    {
        await Client.MutateRowAsync(TN, "rmwr-neg",
            Mutations.SetCell(CF, "c",
                ByteString.CopyFrom(BitConverter.GetBytes(100L).Reverse().ToArray()),
                new BigtableVersion(1000)));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwr-neg",
            ReadModifyWriteRules.Increment(CF, "c", -30));
        var val = BitConverter.ToInt64(resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray());
        val.Should().Be(70);
    }

    [Fact]
    public async Task Increment_to_zero()
    {
        await Client.MutateRowAsync(TN, "rmwr-zer",
            Mutations.SetCell(CF, "c",
                ByteString.CopyFrom(BitConverter.GetBytes(10L).Reverse().ToArray()),
                new BigtableVersion(1000)));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwr-zer",
            ReadModifyWriteRules.Increment(CF, "c", -10));
        var val = BitConverter.ToInt64(resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray());
        val.Should().Be(0);
    }

    [Fact]
    public async Task Increment_to_negative()
    {
        await Client.MutateRowAsync(TN, "rmwr-belowz",
            Mutations.SetCell(CF, "c",
                ByteString.CopyFrom(BitConverter.GetBytes(5L).Reverse().ToArray()),
                new BigtableVersion(1000)));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwr-belowz",
            ReadModifyWriteRules.Increment(CF, "c", -10));
        var val = BitConverter.ToInt64(resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray());
        val.Should().Be(-5);
    }

    #endregion

    #region RMW response consistency with subsequent read

    [Fact]
    public async Task RMW_response_matches_subsequent_read()
    {
        var key = "rmwr-cons";
        await Client.MutateRowAsync(TN, key,
            Mutations.SetCell(CF, "c",
                ByteString.CopyFrom(BitConverter.GetBytes(100L).Reverse().ToArray()),
                new BigtableVersion(1000)));
        var resp = await Client.ReadModifyWriteRowAsync(TN, key,
            ReadModifyWriteRules.Increment(CF, "c", 50));
        var respVal = BitConverter.ToInt64(resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray());
        var rows = await ReadAll(RowSet.FromRowKeys(key), RowFilters.CellsPerColumnLimit(1));
        var readVal = BitConverter.ToInt64(rows[0].Families[0].Columns[0].Cells[0].Value.ToByteArray().Reverse().ToArray());
        readVal.Should().Be(respVal);
    }

    #endregion

    #region Binary append

    [Fact]
    public async Task Append_binary_data()
    {
        var initial = new byte[] { 0x01, 0x02 };
        var appended = new byte[] { 0x03, 0x04 };
        await Client.MutateRowAsync(TN, "rmwr-binapp",
            Mutations.SetCell(CF, "c", ByteString.CopyFrom(initial), new BigtableVersion(1000)));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmwr-binapp",
            ReadModifyWriteRules.Append(CF, "c", ByteString.CopyFrom(appended)));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray()
            .Should().BeEquivalentTo(new byte[] { 0x01, 0x02, 0x03, 0x04 });
    }

    #endregion
}

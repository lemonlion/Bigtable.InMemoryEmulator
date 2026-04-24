using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for ReadModifyWrite append operations and mixed append+increment.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readmodifywriterule
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadModifyWriteAppendTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public ReadModifyWriteAppendTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync("rmw-append", new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("rmw-append");

    private async Task<string> ReadValue(string rowKey, string col)
    {
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys(rowKey)))
            foreach (var fam in row.Families)
                foreach (var c in fam.Columns)
                    if (c.Qualifier.ToStringUtf8() == col)
                        return c.Cells[0].Value.ToStringUtf8();
        return "";
    }

    #region Single append

    [Fact]
    public async Task Append_to_new_cell()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "ap-new",
            ReadModifyWriteRules.Append(CF, "log", "first"));
        var cell = resp.Row.Families[0].Columns[0].Cells[0];
        cell.Value.ToStringUtf8().Should().Be("first");
    }

    [Fact]
    public async Task Append_to_existing_cell()
    {
        await Client.MutateRowAsync(TN, "ap-exist",
            Mutations.SetCell(CF, "log", "hello", new BigtableVersion(1000)));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "ap-exist",
            ReadModifyWriteRules.Append(CF, "log", " world"));
        var cell = resp.Row.Families[0].Columns[0].Cells[0];
        cell.Value.ToStringUtf8().Should().Be("hello world");
    }

    [Fact]
    public async Task Append_empty_string()
    {
        await Client.MutateRowAsync(TN, "ap-empty",
            Mutations.SetCell(CF, "log", "data", new BigtableVersion(1000)));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "ap-empty",
            ReadModifyWriteRules.Append(CF, "log", ""));
        var cell = resp.Row.Families[0].Columns[0].Cells[0];
        cell.Value.ToStringUtf8().Should().Be("data");
    }

    [Fact]
    public async Task Append_multiple_times()
    {
        await Client.ReadModifyWriteRowAsync(TN, "ap-multi",
            ReadModifyWriteRules.Append(CF, "log", "a"));
        await Client.ReadModifyWriteRowAsync(TN, "ap-multi",
            ReadModifyWriteRules.Append(CF, "log", "b"));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "ap-multi",
            ReadModifyWriteRules.Append(CF, "log", "c"));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("abc");
    }

    [Fact]
    public async Task Append_binary_data()
    {
        var initialBytes = new byte[] { 0x01, 0x02, 0x03 };
        var appendBytes = new byte[] { 0x04, 0x05 };
        await Client.MutateRowAsync(TN, "ap-binary",
            Mutations.SetCell(CF, "bin", ByteString.CopyFrom(initialBytes), new BigtableVersion(1000)));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "ap-binary",
            ReadModifyWriteRules.Append(CF, "bin", ByteString.CopyFrom(appendBytes)));
        var result = resp.Row.Families[0].Columns[0].Cells[0].Value.ToByteArray();
        result.Should().BeEquivalentTo(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 });
    }

    [Fact]
    public async Task Append_large_string()
    {
        var large = new string('x', 10000);
        var resp = await Client.ReadModifyWriteRowAsync(TN, "ap-large",
            ReadModifyWriteRules.Append(CF, "big", large));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Length.Should().Be(10000);
    }

    #endregion

    #region Multiple columns in single RMW

    [Fact]
    public async Task Append_to_multiple_columns()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "ap-mcol",
            ReadModifyWriteRules.Append(CF, "col1", "a"),
            ReadModifyWriteRules.Append(CF, "col2", "b"),
            ReadModifyWriteRules.Append(CF, "col3", "c"));
        resp.Row.Families[0].Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task Append_and_increment_in_same_request()
    {
        await Client.MutateRowAsync(TN, "ap-mix",
            Mutations.SetCell(CF, "log", "start", new BigtableVersion(1000)));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "ap-mix",
            ReadModifyWriteRules.Append(CF, "log", "-end"),
            ReadModifyWriteRules.Increment(CF, "count", 1));
        // Check both operations applied
        var cols = resp.Row.Families[0].Columns;
        var logCol = cols.First(c => c.Qualifier.ToStringUtf8() == "log");
        logCol.Cells[0].Value.ToStringUtf8().Should().Be("start-end");
        var countCol = cols.First(c => c.Qualifier.ToStringUtf8() == "count");
        var countVal = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(countCol.Cells[0].Value.Span);
        countVal.Should().Be(1);
    }

    #endregion

    #region Append preserves only latest version

    [Fact]
    public async Task Append_operates_on_latest_version()
    {
        // Write two versions
        await Client.MutateRowAsync(TN, "ap-ver",
            Mutations.SetCell(CF, "data", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ap-ver",
            Mutations.SetCell(CF, "data", "new", new BigtableVersion(2000)));
        // Append should operate on latest version ("new")
        var resp = await Client.ReadModifyWriteRowAsync(TN, "ap-ver",
            ReadModifyWriteRules.Append(CF, "data", "-appended"));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new-appended");
    }

    #endregion

    #region Append after delete

    [Fact]
    public async Task Append_after_delete_creates_new_value()
    {
        await Client.MutateRowAsync(TN, "ap-del",
            Mutations.SetCell(CF, "data", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ap-del", Mutations.DeleteFromRow());
        var resp = await Client.ReadModifyWriteRowAsync(TN, "ap-del",
            ReadModifyWriteRules.Append(CF, "data", "fresh"));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("fresh");
    }

    #endregion

    #region Read result verification

    [Fact]
    public async Task Append_result_matches_subsequent_read()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "ap-verify",
            ReadModifyWriteRules.Append(CF, "data", "hello"));
        var respValue = resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8();

        var readValue = await ReadValue("ap-verify", "data");
        readValue.Should().Be(respValue);
    }

    [Fact]
    public async Task Increment_result_matches_subsequent_read()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "inc-verify",
            ReadModifyWriteRules.Increment(CF, "counter", 42));
        var respVal = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(
            resp.Row.Families[0].Columns[0].Cells[0].Value.Span);

        // Read back and verify
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("inc-verify")))
            rows.Add(row);
        var readVal = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(
            rows[0].Families[0].Columns[0].Cells[0].Value.Span);
        readVal.Should().Be(respVal);
    }

    #endregion
}

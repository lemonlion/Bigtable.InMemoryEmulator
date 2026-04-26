using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadRowsOpenRangePrefixTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "rr-orp";
    private const string CF = "cf";

    public ReadRowsOpenRangePrefixTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF });
        foreach (var key in new[] { "a", "aa", "ab", "b", "ba", "bb", "c" })
            await Client.MutateRowAsync(TN, key, Mutations.SetCell(CF, "c", key));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Open_start_to_key()
    {
        var range = new RowRange { EndKeyClosed = ByteString.CopyFromUtf8("b") };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, RowSet.FromRowRanges(range))) rows.Add(r);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeEquivalentTo(new[] { "a", "aa", "ab", "b" });
    }

    [Fact]
    public async Task Key_to_open_end()
    {
        var range = new RowRange { StartKeyClosed = ByteString.CopyFromUtf8("b") };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, RowSet.FromRowRanges(range))) rows.Add(r);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeEquivalentTo(new[] { "b", "ba", "bb", "c" });
    }

    [Fact]
    public async Task Open_start_open_end()
    {
        // No range specified — all rows
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN)) rows.Add(r);
        rows.Should().HaveCount(7);
    }

    [Fact]
    public async Task Start_open_excludes_start()
    {
        var range = new RowRange
        {
            StartKeyOpen = ByteString.CopyFromUtf8("a"),
            EndKeyClosed = ByteString.CopyFromUtf8("b")
        };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, RowSet.FromRowRanges(range))) rows.Add(r);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeEquivalentTo(new[] { "aa", "ab", "b" });
    }

    [Fact]
    public async Task End_open_excludes_end()
    {
        var range = new RowRange
        {
            StartKeyClosed = ByteString.CopyFromUtf8("a"),
            EndKeyOpen = ByteString.CopyFromUtf8("b")
        };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, RowSet.FromRowRanges(range))) rows.Add(r);
        rows.Select(r => r.Key.ToStringUtf8()).Should().BeEquivalentTo(new[] { "a", "aa", "ab" });
    }

    [Fact]
    public async Task Unbounded_start_open_end()
    {
        var range = new RowRange { EndKeyOpen = ByteString.CopyFromUtf8("aa") };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, RowSet.FromRowRanges(range))) rows.Add(r);
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("a");
    }

    [Fact]
    public async Task Unbounded_start_closed_end()
    {
        var range = new RowRange { EndKeyClosed = ByteString.CopyFromUtf8("a") };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, RowSet.FromRowRanges(range))) rows.Add(r);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Start_closed_unbounded_end()
    {
        var range = new RowRange { StartKeyClosed = ByteString.CopyFromUtf8("c") };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, RowSet.FromRowRanges(range))) rows.Add(r);
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("c");
    }

    [Fact]
    public async Task Start_after_all_keys()
    {
        var range = new RowRange { StartKeyClosed = ByteString.CopyFromUtf8("z") };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, RowSet.FromRowRanges(range))) rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task End_before_all_keys()
    {
        var range = new RowRange { EndKeyOpen = ByteString.CopyFromUtf8("a") };
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN, RowSet.FromRowRanges(range))) rows.Add(r);
        rows.Should().BeEmpty();
    }
}

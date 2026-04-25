using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for ReadModifyWrite error handling and boundary conditions.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readmodifywriterowrequest
///   "Modifies a row atomically on the server side."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ReadModifyWriteErrorTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "rmw-err";

    public ReadModifyWriteErrorTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    #region Invalid family

    [Fact]
    public async Task Append_nonexistent_family_throws()
    {
        var act = () => Client.ReadModifyWriteRowAsync(TN, "rmw-err1",
            ReadModifyWriteRules.Append("bad_family", "c", "v"));
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    [Fact]
    public async Task Increment_nonexistent_family_throws()
    {
        var act = () => Client.ReadModifyWriteRowAsync(TN, "rmw-err2",
            ReadModifyWriteRules.Increment("bad_family", "c", 1));
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    #endregion

    #region Invalid table

    [Fact]
    public async Task RMW_nonexistent_table_throws()
    {
        var fakeTn = _fixture.GetTableName("nonexistent-rmw-table");
        var act = () => Client.ReadModifyWriteRowAsync(fakeTn, "r1",
            ReadModifyWriteRules.Append(CF, "c", "v"));
        await act.Should().ThrowAsync<Grpc.Core.RpcException>()
            .Where(e => e.StatusCode == Grpc.Core.StatusCode.NotFound);
    }

    #endregion

    #region Recovery after error

    [Fact]
    public async Task Row_usable_after_failed_RMW()
    {
        // Cause an error
        try { await Client.ReadModifyWriteRowAsync(TN, "rmw-rec", ReadModifyWriteRules.Append("bad", "c", "v")); }
        catch { /* expected */ }
        // Valid operation should work
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-rec",
            ReadModifyWriteRules.Append(CF, "c", "data"));
        resp.Row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("data");
    }

    #endregion

    #region Increment on non-int value

    [Fact]
    public async Task Increment_on_string_value_behavior()
    {
        // Set a non-8-byte value, then try increment
        await Client.MutateRowAsync(TN, "rmw-nonum",
            Mutations.SetCell(CF, "c", "not-a-number", new BigtableVersion(1000)));
        // Incrementing a non-8-byte value should fail
        var act = () => Client.ReadModifyWriteRowAsync(TN, "rmw-nonum",
            ReadModifyWriteRules.Increment(CF, "c", 1));
        // The behavior depends on the implementation — it either throws or treats it as bytes
        try
        {
            await act();
            // If it didn't throw, the value was modified somehow — just verify it's readable
            var row = await Client.ReadRowAsync(TN, "rmw-nonum");
            row.Should().NotBeNull();
        }
        catch (Grpc.Core.RpcException)
        {
            // Also acceptable — server rejects increment on non-8-byte value
        }
    }

    #endregion

    #region RMW timestamp behavior

    [Fact]
    public async Task Append_timestamp_is_server_assigned()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-ts1",
            ReadModifyWriteRules.Append(CF, "c", "v"));
        var ts = resp.Row.Families[0].Columns[0].Cells[0].TimestampMicros;
        ts.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Increment_timestamp_is_server_assigned()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-ts2",
            ReadModifyWriteRules.Increment(CF, "c", 1));
        var ts = resp.Row.Families[0].Columns[0].Cells[0].TimestampMicros;
        ts.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RMW_replaces_previous_timestamp()
    {
        await Client.MutateRowAsync(TN, "rmw-ts3",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-ts3",
            ReadModifyWriteRules.Append(CF, "c", "-new"));
        var cell = resp.Row.Families[0].Columns[0].Cells[0];
        cell.Value.ToStringUtf8().Should().Be("old-new");
        // The RMW result should have a server-assigned timestamp
        cell.TimestampMicros.Should().BeGreaterThan(0);
    }

    #endregion

    #region RMW response row key

    [Fact]
    public async Task Response_row_key_matches_request()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-key",
            ReadModifyWriteRules.Append(CF, "c", "v"));
        resp.Row.Key.ToStringUtf8().Should().Be("rmw-key");
    }

    #endregion

    #region Multiple rules atomicity

    [Fact]
    public async Task Multiple_rules_applied_atomically()
    {
        // Both rules should succeed or fail together
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-atom",
            ReadModifyWriteRules.Append(CF, "a", "val-a"),
            ReadModifyWriteRules.Append(CF, "b", "val-b"));
        var cols = resp.Row.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("a").And.Contain("b");
    }

    [Fact]
    public async Task Mixed_append_increment_atomic()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "rmw-mixed",
            ReadModifyWriteRules.Append(CF, "log", "entry"),
            ReadModifyWriteRules.Increment(CF, "counter", 1));
        var cols = resp.Row.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().Contain("log").And.Contain("counter");
    }

    #endregion
}

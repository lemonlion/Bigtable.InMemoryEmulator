using Bigtable.InMemoryEmulator;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for FaultInjector and RpcLog/QueryLog diagnostics.
/// Uses InMemoryBigtable.Create() — public API only, but InMemory-specific.
///
/// Ref: Phase 5 plan — "Fault injection (Layer 2/3 only)"
/// </summary>
[Trait(TestTraits.Target, TestTraits.InMemoryOnly)]
public sealed class FaultInjectionTests : IDisposable
{
    private readonly InMemoryBigtableResult _result;
    private readonly BigtableClient _client;
    private readonly TableName _tableName;

    public FaultInjectionTests()
    {
        _result = InMemoryBigtable.Create("fitable", ["cf"]);
        _client = _result.Client;
        _tableName = _result.GetTableName("fitable");
    }

    public void Dispose() => _result.Dispose();

    #region FaultInjector

    [Fact]
    public async Task FaultInjector_injects_Unavailable_for_MutateRow()
    {
        _result.FaultInjector.SetFault(ctx =>
            ctx.Method.Contains("MutateRow") && !ctx.Method.Contains("MutateRows")
                ? new Status(StatusCode.Unavailable, "Simulated unavailable")
                : null);

        var act = () => _client.MutateRowAsync(_tableName, new BigtableByteString("row1"),
            Mutations.SetCell("cf", "col", "val", new BigtableVersion(1000)));

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.Unavailable);
    }

    [Fact]
    public async Task FaultInjector_allows_other_rpcs_when_targeting_specific_method()
    {
        // Only block CheckAndMutateRow, allow MutateRow
        _result.FaultInjector.SetFault(ctx =>
            ctx.Method.Contains("CheckAndMutateRow")
                ? new Status(StatusCode.Aborted, "Simulated abort")
                : null);

        // MutateRow should succeed
        await _client.MutateRowAsync(_tableName, new BigtableByteString("row1"),
            Mutations.SetCell("cf", "col", "val", new BigtableVersion(1000)));

        var row = await _client.ReadRowAsync(_tableName, new BigtableByteString("row1"));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task FaultInjector_clear_stops_fault_injection()
    {
        _result.FaultInjector.SetFault(_ => new Status(StatusCode.Internal, "Error"));

        var act1 = () => _client.MutateRowAsync(_tableName, new BigtableByteString("row1"),
            Mutations.SetCell("cf", "col", "val", new BigtableVersion(1000)));
        await act1.Should().ThrowAsync<RpcException>();

        // Clear the fault
        _result.FaultInjector.Clear();

        // Should succeed now
        await _client.MutateRowAsync(_tableName, new BigtableByteString("row1"),
            Mutations.SetCell("cf", "col", "val", new BigtableVersion(1000)));
        var row = await _client.ReadRowAsync(_tableName, new BigtableByteString("row1"));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task FaultInjector_can_inject_DeadlineExceeded()
    {
        _result.FaultInjector.SetFault(_ =>
            new Status(StatusCode.DeadlineExceeded, "Timeout simulation"));

        var act = () => _client.MutateRowAsync(_tableName, new BigtableByteString("row1"),
            Mutations.SetCell("cf", "col", "val", new BigtableVersion(1000)));

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.DeadlineExceeded);
    }

    [Fact]
    public async Task FaultInjector_context_has_method_name()
    {
        FaultContext? capturedContext = null;
        _result.FaultInjector.SetFault(ctx =>
        {
            capturedContext = ctx;
            return null; // Don't actually fail
        });

        await _client.MutateRowAsync(_tableName, new BigtableByteString("row1"),
            Mutations.SetCell("cf", "col", "val", new BigtableVersion(1000)));

        capturedContext.Should().NotBeNull();
        capturedContext!.Method.Should().Contain("MutateRow");
    }

    #endregion

    #region RpcLog

    [Fact]
    public async Task RpcLog_records_MutateRow_calls()
    {
        _result.RpcLog.Clear();

        await _client.MutateRowAsync(_tableName, new BigtableByteString("row1"),
            Mutations.SetCell("cf", "col", "val", new BigtableVersion(1000)));

        var entries = _result.RpcLog.Entries;
        entries.Should().Contain(e => e.Method.Contains("MutateRow"));
    }

    [Fact]
    public async Task RpcLog_records_ReadRows_calls()
    {
        await _client.MutateRowAsync(_tableName, new BigtableByteString("row1"),
            Mutations.SetCell("cf", "col", "val", new BigtableVersion(1000)));

        _result.RpcLog.Clear();

        // ReadRowAsync uses ReadRows RPC under the hood
        await _client.ReadRowAsync(_tableName, new BigtableByteString("row1"));

        var entries = _result.RpcLog.Entries;
        entries.Should().Contain(e => e.Method.Contains("ReadRows"));
    }

    [Fact]
    public async Task RpcLog_records_failed_calls()
    {
        _result.RpcLog.Clear();
        _result.FaultInjector.SetFault(_ => new Status(StatusCode.Unavailable, "Error"));

        try
        {
            await _client.MutateRowAsync(_tableName, new BigtableByteString("row1"),
                Mutations.SetCell("cf", "col", "val", new BigtableVersion(1000)));
        }
        catch (RpcException) { }

        var entries = _result.RpcLog.Entries;
        entries.Should().Contain(e => !e.Succeeded && e.StatusCode == StatusCode.Unavailable);
    }

    [Fact]
    public async Task RpcLog_clear_removes_entries()
    {
        await _client.MutateRowAsync(_tableName, new BigtableByteString("row1"),
            Mutations.SetCell("cf", "col", "val", new BigtableVersion(1000)));

        _result.RpcLog.Entries.Should().NotBeEmpty();
        _result.RpcLog.Clear();
        _result.RpcLog.Entries.Should().BeEmpty();
    }

    #endregion

    #region QueryLog

    [Fact]
    public async Task QueryLog_records_ExecuteQuery_calls()
    {
        _result.QueryLog.Clear();

        // Seed data
        await _client.MutateRowAsync(_tableName, new BigtableByteString("row1"),
            Mutations.SetCell("cf", "col", "val", new BigtableVersion(1000)));

        // Execute a query via the ServiceApiClient
        var serviceApiClient = new BigtableServiceApiClientBuilder
        {
            CallInvoker = _result.Channel.CreateCallInvoker()
        }.Build();

        var request = new ExecuteQueryRequest
        {
            InstanceName = $"projects/{_result.ProjectId}/instances/{_result.InstanceId}",
            Query = "SELECT _key FROM fitable",
            ProtoFormat = new ProtoFormat(),
        };

        var stream = serviceApiClient.ExecuteQuery(request);
        var e = stream.GetResponseStream().GetAsyncEnumerator(default);
        while (await e.MoveNextAsync()) { }

        _result.QueryLog.Entries.Should().Contain(q => q.Sql.Contains("fitable"));
    }

    #endregion
}

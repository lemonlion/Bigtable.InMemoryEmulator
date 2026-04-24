using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.V2;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bigtable.InMemoryEmulator;

/// <summary>
/// Hosts an in-process gRPC TestServer implementing the Bigtable Data API.
/// Creates a real BigtableClient backed by in-memory storage via the gRPC pipeline.
///
/// This is the "FakeCosmosHandler" equivalent — the real SDK's BigtableClientImpl
/// handles row assembly (CellChunk → Row), retry logic, and streaming internally.
/// Zero production code changes required.
///
/// Ref: Phase 4-SDK plan — "Layer 3: In-process gRPC TestServer — PRIMARY"
/// </summary>
internal sealed class InMemoryBigtableServer : IDisposable
{
    private readonly WebApplication _app;
    private readonly GrpcChannel _channel;

    public InMemoryBigtableStore Store { get; }
    public BigtableClient Client { get; }
    public GrpcChannel Channel => _channel;
    public FaultInjector FaultInjector { get; }
    public RpcLog RpcLog { get; }
    public QueryLog QueryLog { get; }

    private InMemoryBigtableServer(
        WebApplication app,
        GrpcChannel channel,
        BigtableClient client,
        InMemoryBigtableStore store,
        FaultInjector faultInjector,
        RpcLog rpcLog,
        QueryLog queryLog)
    {
        _app = app;
        _channel = channel;
        Client = client;
        Store = store;
        FaultInjector = faultInjector;
        RpcLog = rpcLog;
        QueryLog = queryLog;
    }

    /// <summary>
    /// Creates and starts an in-process Bigtable gRPC server.
    /// Returns a fully usable BigtableClient connected to the in-memory store.
    /// </summary>
    public static InMemoryBigtableServer Create(InMemoryBigtableStore? store = null,
        string projectId = "test-project", string instanceId = "test-instance",
        FaultInjector? faultInjector = null)
    {
        store ??= new InMemoryBigtableStore();
        faultInjector ??= new FaultInjector();
        var rpcLog = new RpcLog();
        var queryLog = new QueryLog();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(faultInjector);
        builder.Services.AddSingleton(rpcLog);
        builder.Services.AddSingleton(queryLog);
        builder.Services.AddSingleton(new BigtableTableAdminGrpcService(store, projectId, instanceId));

        var app = builder.Build();
        app.MapGrpcService<BigtableGrpcService>();
        app.MapGrpcService<BigtableTableAdminGrpcService>();
        app.Start();

        var testServer = app.GetTestServer();
        var handler = new ResponseVersionHandler(testServer.CreateHandler());

        var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler,
        });

        var client = new BigtableClientBuilder
        {
            CallInvoker = channel.CreateCallInvoker(),
        }.Build();

        return new InMemoryBigtableServer(app, channel, client, store, faultInjector, rpcLog, queryLog);
    }

    public void Dispose()
    {
        _channel.Dispose();
        _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

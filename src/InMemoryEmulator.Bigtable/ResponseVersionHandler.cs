namespace InMemoryEmulator.Bigtable;

/// <summary>
/// Fixes an HTTP version mismatch between ASP.NET Core TestServer and gRPC.
/// TestServer returns HTTP/1.1 responses, but gRPC requires HTTP/2.
/// This handler patches the response version to 2.0.
///
/// Ref: https://docs.microsoft.com/en-us/aspnet/core/grpc/test-services
/// </summary>
internal sealed class ResponseVersionHandler : DelegatingHandler
{
    public ResponseVersionHandler(HttpMessageHandler innerHandler) : base(innerHandler) { }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        response.Version = request.Version;
        return response;
    }
}

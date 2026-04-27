using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for PingAndWarm RPC.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#pingandwarmrequest
///   "Warm up associated metadata for the given instance and app profile."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class PingAndWarmTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;

    public PingAndWarmTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync("ping-init", new[] { "cf" });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task PingAndWarm_succeeds()
    {
        var request = new PingAndWarmRequest
        {
            Name = _fixture.InstanceName,
        };
        var response = await _fixture.ServiceApiClient.PingAndWarmAsync(request);
        response.Should().NotBeNull();
    }

    [Fact]
    public async Task PingAndWarm_with_app_profile()
    {
        var request = new PingAndWarmRequest
        {
            Name = _fixture.InstanceName,
            AppProfileId = "default",
        };
        var response = await _fixture.ServiceApiClient.PingAndWarmAsync(request);
        response.Should().NotBeNull();
    }

    [Fact]
    public async Task PingAndWarm_multiple_times()
    {
        for (int i = 0; i < 5; i++)
        {
            var request = new PingAndWarmRequest
            {
                Name = _fixture.InstanceName,
            };
            var response = await _fixture.ServiceApiClient.PingAndWarmAsync(request);
            response.Should().NotBeNull();
        }
    }
}

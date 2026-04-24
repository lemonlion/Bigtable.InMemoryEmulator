namespace Bigtable.InMemoryEmulator.Tests.Infrastructure;

/// <summary>
/// xUnit collection fixture that reads environment variables to determine the test target.
/// Provides a shared test session across all tests in a collection.
///
/// Environment variables:
///   BIGTABLE_TEST_TARGET: "InMemory" (default), "EmulatorGo", "Gcp"
///   BIGTABLE_EMULATOR_HOST: "localhost:8086" (for Go emulator)
///   BIGTABLE_PROJECT: GCP project ID (for real Bigtable)
///   BIGTABLE_INSTANCE: GCP instance ID (for real Bigtable)
/// </summary>
public sealed class EmulatorSession
{
    public TestTarget Target { get; }
    public string? EmulatorHost { get; }
    public string? ProjectId { get; }
    public string? InstanceId { get; }

    public EmulatorSession()
    {
        var targetStr = Environment.GetEnvironmentVariable("BIGTABLE_TEST_TARGET") ?? "InMemory";
        Target = targetStr.ToLowerInvariant() switch
        {
            "inmemory" => TestTarget.InMemory,
            "emulatorgo" or "emulator-go" => TestTarget.EmulatorGo,
            "gcp" => TestTarget.Gcp,
            _ => TestTarget.InMemory,
        };

        EmulatorHost = Environment.GetEnvironmentVariable("BIGTABLE_EMULATOR_HOST");
        ProjectId = Environment.GetEnvironmentVariable("BIGTABLE_PROJECT") ?? "test-project";
        InstanceId = Environment.GetEnvironmentVariable("BIGTABLE_INSTANCE") ?? "test-instance";
    }

    /// <summary>
    /// Creates the appropriate test fixture for the current test target.
    /// </summary>
    public ITestTableFixture CreateFixture()
    {
        return Target switch
        {
            TestTarget.InMemory => new InMemoryTestFixture(),
            TestTarget.EmulatorGo => new EmulatorGoTestFixture(
                EmulatorHost ?? "localhost:8086", ProjectId!, InstanceId!),
            TestTarget.Gcp => new GcpTestFixture(ProjectId!, InstanceId!),
            _ => throw new NotSupportedException($"Test target '{Target}' is not yet supported."),
        };
    }
}

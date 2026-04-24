namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// xUnit collection definition that shares EmulatorSession across all integration tests.
/// </summary>
[CollectionDefinition(Name)]
public class IntegrationCollection : ICollectionFixture<EmulatorSession>
{
    public const string Name = "Integration";
}

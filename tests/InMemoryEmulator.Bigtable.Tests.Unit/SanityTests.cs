namespace InMemoryEmulator.Bigtable.Tests;

public class SanityTests
{
	[Fact]
	public void InMemoryBigtable_Class_Exists()
	{
		var type = typeof(InMemoryBigtable);
		Assert.NotNull(type);
	}
}

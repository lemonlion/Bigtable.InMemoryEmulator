namespace InMemoryEmulator.Bigtable.Tests.Infrastructure;

/// <summary>
/// Constants for test trait classification.
/// </summary>
public static class TestTraits
{
	/// <summary>
	/// Trait name for test target classification.
	/// </summary>
	public const string Target = "Target";

	/// <summary>
	/// Runs on all targets (in-memory, Go emulator, real GCP).
	/// </summary>
	public const string All = "All";

	/// <summary>
	/// Runs only on the in-memory emulator (fault injection, internal APIs).
	/// </summary>
	public const string InMemoryOnly = "InMemoryOnly";

	/// <summary>
	/// Runs on in-memory and real GCP, but NOT on the Go emulator.
	/// Used for features the Go emulator doesn't support (GoogleSQL, ReadChangeStream, Sink filter).
	/// </summary>
	public const string GcpOnly = "GcpOnly";

	/// <summary>
	/// Documents known behavioral divergences between targets.
	/// </summary>
	public const string KnownDivergence = "KnownDivergence";
}

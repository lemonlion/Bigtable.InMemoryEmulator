using System.Reflection;

namespace InMemoryEmulator.Bigtable;

/// <summary>
/// Detects when the Google.Cloud.Bigtable.V2 SDK version changes from what was tested.
/// This helps catch potential breaking changes early — the SDK may rename internal types,
/// change protobuf field semantics, or alter gRPC behavior between versions.
///
/// Usage:
///   var warning = SdkVersionDriftDetector.CheckAndWarn();
///   if (warning != null) Console.WriteLine(warning);
///
/// Ref: Concept mapping — "SdkVersionDriftDetector: Same pattern — detect SDK version changes
///   that might break assumptions."
/// </summary>
public static class SdkVersionDriftDetector
{
    /// <summary>
    /// The SDK version this emulator was tested against.
    /// Update this when upgrading Google.Cloud.Bigtable.V2 NuGet.
    /// </summary>
    public const string TestedSdkVersion = "3.15.0";

    /// <summary>
    /// The actual loaded version of Google.Cloud.Bigtable.V2 at runtime.
    /// </summary>
    public static string ActualSdkVersion { get; } = DetectActualVersion();

    /// <summary>
    /// True if the loaded SDK version differs from the tested version.
    /// </summary>
    public static bool IsDrifted => !string.Equals(TestedSdkVersion, ActualSdkVersion, StringComparison.Ordinal);

    /// <summary>
    /// Returns a warning message if the SDK version has drifted, or null if versions match.
    /// </summary>
    public static string? CheckAndWarn()
    {
        return IsDrifted ? FormatDriftWarning(TestedSdkVersion, ActualSdkVersion) : null;
    }

    /// <summary>
    /// Formats a human-readable drift warning message.
    /// </summary>
    public static string FormatDriftWarning(string testedVersion, string actualVersion)
    {
        return $"[SdkVersionDriftDetector] Google.Cloud.Bigtable.V2 SDK version drift detected: " +
               $"tested against {testedVersion}, but loaded version is {actualVersion}. " +
               $"The in-memory emulator may behave unexpectedly if the SDK has breaking changes.";
    }

    private static string DetectActualVersion()
    {
        var assembly = typeof(Google.Cloud.Bigtable.V2.BigtableClient).Assembly;
        var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // InformationalVersion may contain commit hash suffix (e.g., "3.15.0+abc123")
        if (infoVersion != null)
        {
            var plusIndex = infoVersion.IndexOf('+');
            return plusIndex >= 0 ? infoVersion[..plusIndex] : infoVersion;
        }

        // Fallback to assembly version
        return assembly.GetName().Version?.ToString(3) ?? "unknown";
    }
}

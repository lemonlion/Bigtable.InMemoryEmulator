using Bigtable.InMemoryEmulator;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for SdkVersionDriftDetector — detects when the Google.Cloud.Bigtable.V2 SDK version
/// changes from what was tested, to catch potential breaking changes early.
/// </summary>
public class SdkVersionDriftDetectorTests
{
    [Fact]
    public void TestedVersion_returns_hardcoded_known_version()
    {
        SdkVersionDriftDetector.TestedSdkVersion.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ActualVersion_returns_loaded_assembly_version()
    {
        SdkVersionDriftDetector.ActualSdkVersion.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void IsDrifted_returns_false_when_versions_match()
    {
        // Current state: the tested version should match the actual loaded version
        SdkVersionDriftDetector.IsDrifted.Should().BeFalse(
            $"tested={SdkVersionDriftDetector.TestedSdkVersion}, actual={SdkVersionDriftDetector.ActualSdkVersion}");
    }

    [Fact]
    public void CheckAndWarn_returns_null_when_no_drift()
    {
        SdkVersionDriftDetector.CheckAndWarn().Should().BeNull();
    }

    [Fact]
    public void CheckAndWarn_returns_message_for_simulated_drift()
    {
        // We can only verify the format of the warning — actual drift would require a different SDK version
        var warning = SdkVersionDriftDetector.FormatDriftWarning("3.0.0", "4.0.0");
        warning.Should().Contain("3.0.0");
        warning.Should().Contain("4.0.0");
        warning.Should().Contain("Google.Cloud.Bigtable.V2");
    }
}

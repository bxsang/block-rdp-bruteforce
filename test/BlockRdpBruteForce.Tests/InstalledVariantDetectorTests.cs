using System.Runtime.Versioning;
using BlockRdpBruteForce.Update;

namespace BlockRdpBruteForce.Tests;

[SupportedOSPlatform("windows")]
public sealed class InstalledVariantDetectorTests
{
    [Theory]
    [InlineData(0L, MsiVariant.FrameworkDependent)]
    [InlineData(1_000_000L, MsiVariant.FrameworkDependent)]
    [InlineData(5L * 1024 * 1024, MsiVariant.FrameworkDependent)]
    [InlineData(InstalledVariantDetector.SelfContainedThresholdBytes - 1, MsiVariant.FrameworkDependent)]
    [InlineData(InstalledVariantDetector.SelfContainedThresholdBytes, MsiVariant.SelfContained)]
    [InlineData(70L * 1024 * 1024, MsiVariant.SelfContained)]
    [InlineData(150L * 1024 * 1024, MsiVariant.SelfContained)]
    public void FromExeSize_classifies_at_threshold(long bytes, MsiVariant expected)
    {
        Assert.Equal(expected, InstalledVariantDetector.FromExeSize(bytes));
    }
}

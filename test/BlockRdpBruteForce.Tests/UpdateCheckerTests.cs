using System.Runtime.Versioning;
using BlockRdpBruteForce.Update;

namespace BlockRdpBruteForce.Tests;

[SupportedOSPlatform("windows")]
public sealed class UpdateCheckerTests
{
    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("1.2", true)]
    [InlineData("1.2.3.4", true)]
    [InlineData("v1.2.3", true)]
    [InlineData("V1.2.3", true)]
    [InlineData(" 1.2.3 ", true)]
    [InlineData("not-a-version", false)]
    [InlineData("", false)]
    public void TryParseVersion_handles_v_prefix(string input, bool expected)
    {
        var ok = UpdateChecker.TryParseVersion(input, out _);
        Assert.Equal(expected, ok);
    }

    [Fact]
    public void TryParseVersion_compares_correctly()
    {
        Assert.True(UpdateChecker.TryParseVersion("1.3.0", out var newer));
        Assert.True(UpdateChecker.TryParseVersion("1.2.0", out var older));
        Assert.True(newer > older);
    }
}

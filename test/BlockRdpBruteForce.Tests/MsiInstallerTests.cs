using System.Runtime.Versioning;
using BlockRdpBruteForce.Updater;

namespace BlockRdpBruteForce.Tests;

[SupportedOSPlatform("windows")]
public sealed class MsiInstallerTests
{
    [Fact]
    public void Maps_zero_to_success()
    {
        var r = MsiInstaller.Map(0);
        Assert.True(r.Ok);
        Assert.False(r.RebootRequired);
        Assert.Equal(0, r.ExitCode);
    }

    [Fact]
    public void Maps_3010_to_success_with_reboot()
    {
        var r = MsiInstaller.Map(3010);
        Assert.True(r.Ok);
        Assert.True(r.RebootRequired);
        Assert.Equal(3010, r.ExitCode);
    }

    [Fact]
    public void Maps_1602_to_cancelled()
    {
        var r = MsiInstaller.Map(1602);
        Assert.False(r.Ok);
        Assert.True(r.WasCancelled);
        Assert.Equal(1602, r.ExitCode);
    }

    [Fact]
    public void Maps_1603_to_failure()
    {
        var r = MsiInstaller.Map(1603);
        Assert.False(r.Ok);
        Assert.False(r.WasCancelled);
        Assert.Equal(1603, r.ExitCode);
        Assert.NotNull(r.Error);
        Assert.Contains("1603", r.Error);
    }

    [Theory]
    [InlineData(1618)]
    [InlineData(1619)]
    [InlineData(1625)]
    public void Maps_known_failure_codes(int exitCode)
    {
        var r = MsiInstaller.Map(exitCode);
        Assert.False(r.Ok);
        Assert.False(r.WasCancelled);
        Assert.Equal(exitCode, r.ExitCode);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void Maps_unknown_code_to_generic_failure()
    {
        var r = MsiInstaller.Map(9999);
        Assert.False(r.Ok);
        Assert.False(r.WasCancelled);
        Assert.Equal(9999, r.ExitCode);
        Assert.NotNull(r.Error);
        Assert.Contains("9999", r.Error);
    }
}

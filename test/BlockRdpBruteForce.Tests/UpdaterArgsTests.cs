using System.Runtime.Versioning;
using BlockRdpBruteForce.Updater;

namespace BlockRdpBruteForce.Tests;

[SupportedOSPlatform("windows")]
public sealed class UpdaterArgsTests
{
    private static string ValidMsiPath(string version) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "BlockRdpBruteForce", "updates",
            $"BlockRdpBruteForce-{version}-self-contained.msi");

    private static string ValidLogPath(string version) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "BlockRdpBruteForce", "updates",
            $"msiexec-{version}.log");

    [Fact]
    public void Parses_valid_args()
    {
        var argv = new[]
        {
            "--version", "1.4.0",
            "--asset-name", "BlockRdpBruteForce-1.4.0-self-contained.msi",
            "--asset-url", "https://github.com/o/r/releases/download/v1.4.0/foo.msi",
            "--asset-size", "78234112",
            "--msi-path", ValidMsiPath("1.4.0"),
            "--log-path", ValidLogPath("1.4.0"),
        };

        var result = UpdaterArgs.Parse(argv);

        Assert.True(result.Ok, result.Error);
        Assert.NotNull(result.Args);
        Assert.Equal("1.4.0", result.Args!.Version);
        Assert.Equal(78234112, result.Args.AssetSize);
        Assert.Equal("https://github.com/o/r/releases/download/v1.4.0/foo.msi", result.Args.AssetUrl);
    }

    [Fact]
    public void Rejects_msi_path_outside_updates_dir()
    {
        var argv = new[]
        {
            "--version", "1.4.0",
            "--asset-name", "x.msi",
            "--asset-url", "https://example.com/x.msi",
            "--asset-size", "200000",
            "--msi-path", @"C:\Windows\Temp\evil.msi",
            "--log-path", ValidLogPath("1.4.0"),
        };

        var result = UpdaterArgs.Parse(argv);

        Assert.False(result.Ok);
        Assert.Contains("BlockRdpBruteForce\\updates", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_relative_msi_path_that_escapes_updates_dir()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var traversal = Path.Combine(programData, "BlockRdpBruteForce", "updates", "..", "..", "evil.msi");

        var argv = new[]
        {
            "--version", "1.4.0",
            "--asset-name", "x.msi",
            "--asset-url", "https://example.com/x.msi",
            "--asset-size", "200000",
            "--msi-path", traversal,
            "--log-path", ValidLogPath("1.4.0"),
        };

        var result = UpdaterArgs.Parse(argv);

        Assert.False(result.Ok);
    }

    [Fact]
    public void Rejects_non_https_asset_url()
    {
        var argv = new[]
        {
            "--version", "1.4.0",
            "--asset-name", "x.msi",
            "--asset-url", "file:///etc/passwd",
            "--asset-size", "200000",
            "--msi-path", ValidMsiPath("1.4.0"),
            "--log-path", ValidLogPath("1.4.0"),
        };

        var result = UpdaterArgs.Parse(argv);

        Assert.False(result.Ok);
        Assert.Contains("http", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_missing_required_args()
    {
        var argv = new[] { "--version", "1.4.0" };

        var result = UpdaterArgs.Parse(argv);

        Assert.False(result.Ok);
        Assert.Contains("required", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_invalid_asset_size()
    {
        var argv = new[]
        {
            "--version", "1.4.0",
            "--asset-name", "x.msi",
            "--asset-url", "https://example.com/x.msi",
            "--asset-size", "0",
            "--msi-path", ValidMsiPath("1.4.0"),
            "--log-path", ValidLogPath("1.4.0"),
        };

        var result = UpdaterArgs.Parse(argv);

        Assert.False(result.Ok);
        Assert.Contains("asset-size", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_unknown_argument()
    {
        var argv = new[]
        {
            "--version", "1.4.0",
            "--asset-name", "x.msi",
            "--asset-url", "https://example.com/x.msi",
            "--asset-size", "200000",
            "--msi-path", ValidMsiPath("1.4.0"),
            "--log-path", ValidLogPath("1.4.0"),
            "--unexpected", "value",
        };

        var result = UpdaterArgs.Parse(argv);

        Assert.False(result.Ok);
        Assert.Contains("unknown", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsPathUnderUpdatesDir_accepts_valid_path()
    {
        Assert.True(UpdaterArgs.IsPathUnderUpdatesDir(ValidMsiPath("1.4.0")));
    }

    [Fact]
    public void IsPathUnderUpdatesDir_rejects_outside_path()
    {
        Assert.False(UpdaterArgs.IsPathUnderUpdatesDir(@"C:\Windows\System32\evil.msi"));
    }
}

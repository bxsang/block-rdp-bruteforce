using System.Runtime.Versioning;

namespace BlockRdpBruteForce.Updater;

[SupportedOSPlatform("windows")]
internal sealed class UpdaterArgs
{
    public required string Version { get; init; }
    public required string AssetName { get; init; }
    public required string AssetUrl { get; init; }
    public required long AssetSize { get; init; }
    public required string MsiPath { get; init; }
    public required string LogPath { get; init; }
    public string? TrayPath { get; init; }

    public static ParseResult Parse(string[] argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        string? version = null;
        string? assetName = null;
        string? assetUrl = null;
        long? assetSize = null;
        string? msiPath = null;
        string? logPath = null;
        string? trayPath = null;

        for (var i = 0; i < argv.Length; i++)
        {
            var key = argv[i];
            if (i + 1 >= argv.Length) return ParseResult.Failed($"missing value for {key}");
            var value = argv[++i];
            switch (key)
            {
                case "--version": version = value; break;
                case "--asset-name": assetName = value; break;
                case "--asset-url": assetUrl = value; break;
                case "--asset-size":
                    if (!long.TryParse(value, out var size) || size <= 0)
                        return ParseResult.Failed($"invalid --asset-size: {value}");
                    assetSize = size;
                    break;
                case "--msi-path": msiPath = value; break;
                case "--log-path": logPath = value; break;
                case "--tray-path": trayPath = value; break;
                default: return ParseResult.Failed($"unknown argument: {key}");
            }
        }

        if (string.IsNullOrWhiteSpace(version)) return ParseResult.Failed("--version is required");
        if (string.IsNullOrWhiteSpace(assetName)) return ParseResult.Failed("--asset-name is required");
        if (string.IsNullOrWhiteSpace(assetUrl)) return ParseResult.Failed("--asset-url is required");
        if (assetSize is null) return ParseResult.Failed("--asset-size is required");
        if (string.IsNullOrWhiteSpace(msiPath)) return ParseResult.Failed("--msi-path is required");
        if (string.IsNullOrWhiteSpace(logPath)) return ParseResult.Failed("--log-path is required");

        if (!Uri.TryCreate(assetUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return ParseResult.Failed("--asset-url must be an http(s) URL");
        }

        if (!IsPathUnderUpdatesDir(msiPath!))
        {
            return ParseResult.Failed(
                $"--msi-path must resolve under %ProgramData%\\BlockRdpBruteForce\\updates\\: {msiPath}");
        }

        var args = new UpdaterArgs
        {
            Version = version!,
            AssetName = assetName!,
            AssetUrl = assetUrl!,
            AssetSize = assetSize.Value,
            MsiPath = Path.GetFullPath(msiPath!),
            LogPath = Path.GetFullPath(logPath!),
            TrayPath = string.IsNullOrWhiteSpace(trayPath) ? null : Path.GetFullPath(trayPath!),
        };
        return ParseResult.Success(args);
    }

    internal static bool IsPathUnderUpdatesDir(string candidate)
    {
        try
        {
            var full = Path.GetFullPath(candidate);
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrEmpty(programData)) return false;
            var allowed = Path.GetFullPath(Path.Combine(programData, "BlockRdpBruteForce", "updates"));
            allowed = allowed.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public sealed class ParseResult
    {
        public bool Ok { get; init; }
        public string? Error { get; init; }
        public UpdaterArgs? Args { get; init; }

        public static ParseResult Success(UpdaterArgs args) => new() { Ok = true, Args = args };
        public static ParseResult Failed(string error) => new() { Ok = false, Error = error };
    }
}

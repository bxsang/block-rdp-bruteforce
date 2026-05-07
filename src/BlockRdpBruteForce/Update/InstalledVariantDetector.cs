using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;

namespace BlockRdpBruteForce.Update;

[SupportedOSPlatform("windows")]
public sealed class InstalledVariantDetector
{
    // Self-contained single-file publish embeds the .NET runtime: the resulting
    // exe is ~70+ MB on win-x64. Framework-dependent single-file is a few MB.
    // 20 MB cleanly separates them with margin in either direction.
    public const long SelfContainedThresholdBytes = 20L * 1024 * 1024;

    private readonly Lazy<MsiVariant> _detected;

    public InstalledVariantDetector(ILogger<InstalledVariantDetector> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _detected = new Lazy<MsiVariant>(() => Detect(log));
    }

    public MsiVariant Variant => _detected.Value;

    public Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    public string CurrentVersionString
    {
        get
        {
            var v = CurrentVersion;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static MsiVariant FromExeSize(long bytes) =>
        bytes >= SelfContainedThresholdBytes
            ? MsiVariant.SelfContained
            : MsiVariant.FrameworkDependent;

    private static MsiVariant Detect(ILogger log)
    {
        try
        {
            var path = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                log.LogWarning("Could not resolve service exe path; defaulting to SelfContained");
                return MsiVariant.SelfContained;
            }

            var size = new FileInfo(path).Length;
            var variant = FromExeSize(size);
            log.LogInformation(
                "Detected installed MSI variant: {Variant} (exe size {Size:N0} bytes)",
                variant, size);
            return variant;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Variant detection failed; defaulting to SelfContained");
            return MsiVariant.SelfContained;
        }
    }
}

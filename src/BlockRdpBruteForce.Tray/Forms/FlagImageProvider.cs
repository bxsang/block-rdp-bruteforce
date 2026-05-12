using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Versioning;

namespace BlockRdpBruteForce.Tray.Forms;

[SupportedOSPlatform("windows")]
internal static class FlagImageProvider
{
    private static readonly Assembly Assembly = typeof(FlagImageProvider).Assembly;
    private static readonly ConcurrentDictionary<string, Image?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Image? Get(string? countryCode)
    {
        if (string.IsNullOrEmpty(countryCode) || countryCode.Length != 2) return null;
        return Cache.GetOrAdd(countryCode, Load);
    }

    private static Image? Load(string countryCode)
    {
        var name = $"BlockRdpBruteForce.Tray.Resources.Flags.{countryCode.ToLowerInvariant()}.png";
        using var stream = Assembly.GetManifestResourceStream(name);
        if (stream is null) return null;
        // Image.FromStream requires the stream to remain open for the image's
        // lifetime, so copy into a MemoryStream we hand off to the Image.
        var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;
        return Image.FromStream(ms);
    }
}

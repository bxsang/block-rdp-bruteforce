using System.Collections.Generic;
using System.Net;
using System.Runtime.Versioning;
using MaxMind.Db;

namespace BlockRdpBruteForce.Geo;

[SupportedOSPlatform("windows")]
public sealed class GeoLookup : IDisposable
{
    private readonly ILogger<GeoLookup> _log;
    private Reader? _reader;

    public GeoLookup(ILogger<GeoLookup> log)
    {
        _log = log;
    }

    public bool IsLoaded => Volatile.Read(ref _reader) is not null;

    public bool TryOpen(string mmdbPath)
    {
        if (string.IsNullOrWhiteSpace(mmdbPath) || !File.Exists(mmdbPath))
            return false;

        try
        {
            var newReader = new Reader(mmdbPath, FileAccessMode.MemoryMapped);
            var prev = Interlocked.Exchange(ref _reader, newReader);
            prev?.Dispose();
            _log.LogInformation("Geo DB opened: {Path}", mmdbPath);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to open geo DB at {Path}", mmdbPath);
            return false;
        }
    }

    public GeoInfo? Lookup(IPAddress ip)
    {
        var reader = Volatile.Read(ref _reader);
        if (reader is null) return null;

        try
        {
            var record = reader.Find<Dictionary<string, object>>(ip);
            if (record is null) return null;

            return new GeoInfo
            {
                CountryCode = TryGetString(record, "country_code"),
                Asn = TryGetString(record, "asn"),
                AsName = TryGetString(record, "as_name"),
            };
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Geo lookup failed for {Ip}", ip);
            return null;
        }
    }

    private static string? TryGetString(IReadOnlyDictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value) || value is null) return null;
        var s = value.ToString();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    public void Close()
    {
        var prev = Interlocked.Exchange(ref _reader, null);
        prev?.Dispose();
    }

    public void Dispose() => Close();
}

public sealed class GeoInfo
{
    public string? CountryCode { get; set; }
    public string? Asn { get; set; }
    public string? AsName { get; set; }
}

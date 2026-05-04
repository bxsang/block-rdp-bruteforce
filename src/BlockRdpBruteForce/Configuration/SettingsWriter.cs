using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlockRdpBruteForce.Detection;
using BlockRdpBruteForce.Ipc;
using Microsoft.Extensions.Options;

namespace BlockRdpBruteForce.Configuration;

[SupportedOSPlatform("windows")]
public sealed class SettingsWriter
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly string[] AllowedScopes = { "AllPorts", "RdpOnly" };

    private readonly object _writeLock = new();
    private readonly string _overridePath;
    private readonly ILogger<SettingsWriter> _log;

    private ConfigPayload _current;

    public SettingsWriter(IOptions<AppOptions> initial, ILogger<SettingsWriter> log)
        : this(SnapshotFrom(initial), DefaultOverridePath(), log)
    {
    }

    internal SettingsWriter(ConfigPayload initial, string overridePath, ILogger<SettingsWriter> log)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentException.ThrowIfNullOrEmpty(overridePath);
        ArgumentNullException.ThrowIfNull(log);

        _current = Clone(initial);
        _overridePath = overridePath;
        _log = log;
    }

    public string OverridePath => _overridePath;

    public ConfigPayload GetEffective()
    {
        lock (_writeLock)
        {
            return Clone(_current);
        }
    }

    public ConfigSetResult Apply(ConfigPayload payload, string caller)
    {
        ArgumentNullException.ThrowIfNull(payload);

        lock (_writeLock)
        {
            var candidate = Merge(_current, payload);
            Validate(candidate);

            var changedKeys = new List<string>();
            var hot = new List<string>();
            var restartRequired = false;

            if (payload.FailureThreshold.HasValue && payload.FailureThreshold != _current.FailureThreshold)
            { changedKeys.Add(nameof(ConfigPayload.FailureThreshold)); restartRequired = true; }

            if (payload.SlidingWindowMinutes.HasValue && payload.SlidingWindowMinutes != _current.SlidingWindowMinutes)
            { changedKeys.Add(nameof(ConfigPayload.SlidingWindowMinutes)); restartRequired = true; }

            if (payload.BlockDurationMinutes.HasValue && payload.BlockDurationMinutes != _current.BlockDurationMinutes)
            { changedKeys.Add(nameof(ConfigPayload.BlockDurationMinutes)); restartRequired = true; }

            if (payload.FirewallScope is not null &&
                !string.Equals(NormalizeScope(payload.FirewallScope), _current.FirewallScope, StringComparison.Ordinal))
            { changedKeys.Add(nameof(ConfigPayload.FirewallScope)); restartRequired = true; }

            if (payload.EvaluateNlaFallback.HasValue && payload.EvaluateNlaFallback != _current.EvaluateNlaFallback)
            { changedKeys.Add(nameof(ConfigPayload.EvaluateNlaFallback)); restartRequired = true; }

            if (payload.Whitelist is not null &&
                !WhitelistEquals(NormalizeWhitelist(payload.Whitelist), _current.Whitelist ?? new()))
            { changedKeys.Add(nameof(ConfigPayload.Whitelist)); hot.Add("whitelist"); }

            if (changedKeys.Count == 0)
            {
                return new ConfigSetResult
                {
                    Effective = Clone(_current),
                    RestartRequired = false,
                };
            }

            WriteOverride(candidate);
            _current = candidate;

            _log.LogWarning(
                "Settings updated by {Caller}: {Keys}; restartRequired={Restart}, hot={Hot}",
                caller,
                string.Join(",", changedKeys),
                restartRequired,
                hot.Count == 0 ? "(none)" : string.Join(",", hot));

            return new ConfigSetResult
            {
                Effective = Clone(_current),
                RestartRequired = restartRequired,
                AppliedHot = hot,
            };
        }
    }

    public static void Validate(ConfigPayload merged)
    {
        ArgumentNullException.ThrowIfNull(merged);

        if (merged.FailureThreshold is not int ft || ft < 1)
            throw new ConfigValidationException("FailureThreshold must be >= 1");
        if (merged.SlidingWindowMinutes is not int sw || sw < 1 || sw > 1440)
            throw new ConfigValidationException("SlidingWindowMinutes must be in [1, 1440]");
        if (merged.BlockDurationMinutes is not int bd || bd < 0)
            throw new ConfigValidationException("BlockDurationMinutes must be >= 0 (0 = permanent)");
        if (merged.EvaluateNlaFallback is null)
            throw new ConfigValidationException("EvaluateNlaFallback must be a boolean");

        var scope = merged.FirewallScope ?? string.Empty;
        if (!AllowedScopes.Contains(scope, StringComparer.Ordinal))
            throw new ConfigValidationException(
                $"FirewallScope must be one of: {string.Join(", ", AllowedScopes)}");

        var whitelist = merged.Whitelist ?? new List<string>();
        foreach (var entry in whitelist)
        {
            if (!WhitelistEvaluator.TryParse(entry, out _, out _, out _))
                throw new ConfigValidationException(
                    $"Invalid whitelist entry: '{entry}' (must be an IP address or CIDR)");
        }

        // Self-lockout invariant — mirrors install/Install.ps1
        if (whitelist.Count == 0 && ft < 3)
            throw new ConfigValidationException(
                "Refusing: empty whitelist with FailureThreshold<3 is a self-lockout footgun. " +
                "Add a whitelist entry first, or raise FailureThreshold to >=3.");
    }

    private static ConfigPayload Merge(ConfigPayload current, ConfigPayload payload) => new()
    {
        FailureThreshold     = payload.FailureThreshold     ?? current.FailureThreshold,
        SlidingWindowMinutes = payload.SlidingWindowMinutes ?? current.SlidingWindowMinutes,
        BlockDurationMinutes = payload.BlockDurationMinutes ?? current.BlockDurationMinutes,
        Whitelist            = payload.Whitelist is null
                                ? current.Whitelist?.ToList() ?? new()
                                : NormalizeWhitelist(payload.Whitelist),
        FirewallScope        = payload.FirewallScope is null
                                ? current.FirewallScope
                                : NormalizeScope(payload.FirewallScope),
        EvaluateNlaFallback  = payload.EvaluateNlaFallback  ?? current.EvaluateNlaFallback,
    };

    private static List<string> NormalizeWhitelist(IEnumerable<string> entries)
    {
        var list = new List<string>();
        foreach (var raw in entries)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            list.Add(raw.Trim());
        }
        return list;
    }

    private static string NormalizeScope(string s)
    {
        var trimmed = s.Trim();
        foreach (var allowed in AllowedScopes)
            if (string.Equals(allowed, trimmed, StringComparison.OrdinalIgnoreCase)) return allowed;
        return trimmed;
    }

    private void WriteOverride(ConfigPayload candidate)
    {
        var dir = Path.GetDirectoryName(_overridePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        JsonObject root;
        if (File.Exists(_overridePath))
        {
            try
            {
                using var fs = File.OpenRead(_overridePath);
                root = JsonNode.Parse(fs) as JsonObject ?? new JsonObject();
            }
            catch (JsonException ex)
            {
                _log.LogWarning(ex,
                    "Existing override file at {Path} is not valid JSON; replacing.",
                    _overridePath);
                root = new JsonObject();
            }
        }
        else
        {
            root = new JsonObject();
        }

        // Replace the BlockRdp section but keep any other top-level keys an admin
        // may have added (e.g., a Logging override).
        var section = new JsonObject
        {
            [nameof(AppOptions.FailureThreshold)]     = candidate.FailureThreshold!.Value,
            [nameof(AppOptions.SlidingWindowMinutes)] = candidate.SlidingWindowMinutes!.Value,
            [nameof(AppOptions.BlockDurationMinutes)] = candidate.BlockDurationMinutes!.Value,
            [nameof(AppOptions.FirewallScope)]        = candidate.FirewallScope,
            [nameof(AppOptions.EvaluateNlaFallback)]  = candidate.EvaluateNlaFallback!.Value,
        };
        var arr = new JsonArray();
        foreach (var w in candidate.Whitelist ?? new()) arr.Add(w);
        section[nameof(AppOptions.Whitelist)] = arr;

        // Preserve any unmanaged keys an admin may have written into BlockRdp
        // (e.g. StateFilePath override). They are not part of the managed set,
        // but we shouldn't strip them.
        if (root[AppOptions.SectionName] is JsonObject existingSection)
        {
            foreach (var kvp in existingSection)
            {
                if (section.ContainsKey(kvp.Key)) continue;
                section[kvp.Key] = kvp.Value?.DeepClone();
            }
        }

        root[AppOptions.SectionName] = section;

        var json = root.ToJsonString(WriteOptions);
        var tmp = _overridePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _overridePath, overwrite: true);
    }

    private static bool WhitelistEquals(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static ConfigPayload Clone(ConfigPayload src) => new()
    {
        FailureThreshold = src.FailureThreshold,
        SlidingWindowMinutes = src.SlidingWindowMinutes,
        BlockDurationMinutes = src.BlockDurationMinutes,
        Whitelist = src.Whitelist?.ToList(),
        FirewallScope = src.FirewallScope,
        EvaluateNlaFallback = src.EvaluateNlaFallback,
    };

    private static ConfigPayload SnapshotFrom(IOptions<AppOptions> initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        var src = initial.Value;
        return new ConfigPayload
        {
            FailureThreshold = src.FailureThreshold,
            SlidingWindowMinutes = src.SlidingWindowMinutes,
            BlockDurationMinutes = src.BlockDurationMinutes,
            Whitelist = src.Whitelist?.ToList() ?? new List<string>(),
            FirewallScope = src.FirewallScope,
            EvaluateNlaFallback = src.EvaluateNlaFallback,
        };
    }

    private static string DefaultOverridePath()
    {
        var programData = Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData";
        return Path.Combine(programData, "BlockRdpBruteForce", "appsettings.json");
    }
}

public sealed class ConfigValidationException : Exception
{
    public ConfigValidationException(string message) : base(message) { }
}

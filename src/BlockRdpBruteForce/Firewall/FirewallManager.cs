using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BlockRdpBruteForce.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlockRdpBruteForce.Firewall;

[SupportedOSPlatform("windows")]
public sealed class FirewallManager : IFirewallManager
{
    private const int NET_FW_RULE_DIR_IN = 1;
    private const int NET_FW_ACTION_BLOCK = 0;
    private const int NET_FW_PROFILE2_ALL = 0x7FFFFFFF;
    private const int NET_FW_IP_PROTOCOL_TCP = 6;
    private const uint HRESULT_RULE_NOT_FOUND = 0x80070002;

    private readonly AppOptions _options;
    private readonly ILogger<FirewallManager> _log;

    public FirewallManager(IOptions<AppOptions> options, ILogger<FirewallManager> log)
    {
        _options = options.Value;
        _log = log;
    }

    public void AddIp(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        var current = GetBlockedIps();
        if (current.Contains(ip)) return;
        var next = current.ToList();
        next.Add(ip);
        SetIps(next);
    }

    public void RemoveIp(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        var current = GetBlockedIps();
        if (!current.Contains(ip)) return;
        SetIps(current.Where(x => !x.Equals(ip)));
    }

    public IReadOnlyCollection<IPAddress> GetBlockedIps()
    {
        dynamic policy = CreatePolicy();
        try
        {
            dynamic rules = policy.Rules;
            var found = new List<IPAddress>();
            foreach (dynamic rule in rules)
            {
                string name = rule.Name;
                if (!IsManagedRule(name)) continue;
                string remote = rule.RemoteAddresses ?? string.Empty;
                foreach (var token in remote.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var addrPart = token.Split('/')[0];
                    if (IPAddress.TryParse(addrPart, out var ip)) found.Add(ip);
                }
            }
            return found.Distinct().ToList();
        }
        finally
        {
            ReleaseCom(policy);
        }
    }

    public void SetIps(IEnumerable<IPAddress> ips)
    {
        ArgumentNullException.ThrowIfNull(ips);

        var chunks = FirewallRuleChunker.Chunk(ips, _options.FirewallRuleName, _options.MaxRemoteAddressesPerRule);
        var desiredNames = new HashSet<string>(chunks.Select(c => c.RuleName), StringComparer.OrdinalIgnoreCase);

        dynamic policy = CreatePolicy();
        try
        {
            dynamic rules = policy.Rules;

            foreach (var existingName in ListManagedRuleNames(rules))
            {
                if (desiredNames.Contains(existingName)) continue;
                try { rules.Remove(existingName); }
                catch (Exception ex) when (IsRuleNotFound(ex)) { }
            }

            foreach (var chunk in chunks)
                UpsertRule(rules, chunk);
        }
        finally
        {
            ReleaseCom(policy);
        }
    }

    private static object CreatePolicy()
    {
        var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)
            ?? throw new InvalidOperationException("HNetCfg.FwPolicy2 ProgID not found");
        return Activator.CreateInstance(policyType)
            ?? throw new InvalidOperationException("Failed to instantiate HNetCfg.FwPolicy2");
    }

    private static void ReleaseCom(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
            Marshal.FinalReleaseComObject(com);
    }

    private void UpsertRule(dynamic rules, FirewallRuleChunk chunk)
    {
        var csv = FirewallRuleChunker.FormatRemoteAddresses(chunk.Addresses);
        dynamic? rule = null;
        try
        {
            rule = rules.Item(chunk.RuleName);
        }
        catch (Exception ex) when (IsRuleNotFound(ex))
        {
            rule = null;
        }

        if (rule is null)
        {
            var ruleType = Type.GetTypeFromProgID("HNetCfg.FwRule", throwOnError: true)
                ?? throw new InvalidOperationException("HNetCfg.FwRule ProgID not found");
            dynamic newRule = Activator.CreateInstance(ruleType)
                ?? throw new InvalidOperationException("Failed to instantiate HNetCfg.FwRule");
            newRule.Name = chunk.RuleName;
            newRule.Description = "Auto-managed by BlockRdpBruteForce";
            newRule.Direction = NET_FW_RULE_DIR_IN;
            newRule.Action = NET_FW_ACTION_BLOCK;
            newRule.Profiles = NET_FW_PROFILE2_ALL;
            ApplyScope(newRule);
            newRule.RemoteAddresses = csv;
            newRule.Enabled = true;
            rules.Add(newRule);
            _log.LogInformation("Created firewall rule {Rule} with {Count} address(es)", chunk.RuleName, chunk.Addresses.Count);
        }
        else
        {
            rule.RemoteAddresses = csv;
            rule.Enabled = true;
            _log.LogDebug("Updated firewall rule {Rule} with {Count} address(es)", chunk.RuleName, chunk.Addresses.Count);
        }
    }

    private void ApplyScope(dynamic rule)
    {
        if (string.Equals(_options.FirewallScope, "RdpOnly", StringComparison.OrdinalIgnoreCase))
        {
            rule.Protocol = NET_FW_IP_PROTOCOL_TCP;
            rule.LocalPorts = "3389";
        }
    }

    private List<string> ListManagedRuleNames(dynamic rules)
    {
        var names = new List<string>();
        foreach (dynamic rule in rules)
        {
            string name = rule.Name;
            if (IsManagedRule(name)) names.Add(name);
        }
        return names;
    }

    private bool IsManagedRule(string name)
    {
        var prefix = _options.FirewallRuleName;
        return name.StartsWith($"{prefix}-v4", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith($"{prefix}-v6", StringComparison.OrdinalIgnoreCase);
    }

    // The Windows Firewall COM API signals "rule not found" with HRESULT
    // 0x80070002. When called via `dynamic`, the C# runtime binder routes that
    // through Marshal.GetExceptionForHR, which maps 0x80070002 to
    // FileNotFoundException — not COMException. Match either to keep the
    // "absent rule is a no-op" semantics intact.
    private static bool IsRuleNotFound(Exception ex) =>
        (ex is COMException || ex is FileNotFoundException)
        && (uint)ex.HResult == HRESULT_RULE_NOT_FOUND;
}

using System.IO.Pipes;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using BlockRdpBruteForce.Configuration;
using BlockRdpBruteForce.Ipc;
using Microsoft.Extensions.Configuration;

namespace BlockRdpBruteForce.Cli;

[SupportedOSPlatform("windows")]
public static class CliDispatcher
{
    private static readonly string[] KnownVerbs =
        { "status", "list", "unblock", "pause", "resume", "config", "whitelist" };

    public static bool IsCliInvocation(string[] args) =>
        args.Length > 0 && KnownVerbs.Contains(args[0], StringComparer.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        var pipeName = ResolvePipeName();
        var verb = args[0].ToLowerInvariant();

        try
        {
            return verb switch
            {
                "status" => RunStatus(pipeName),
                "list" => RunList(pipeName),
                "unblock" => RunUnblock(pipeName, args),
                "pause" => RunPause(pipeName, args),
                "resume" => RunSimple(pipeName, new PipeRequest { Op = PipeOps.Resume }, "Resumed."),
                "config" => RunConfig(pipeName, args),
                "whitelist" => RunWhitelist(pipeName, args),
                _ => PrintUsage(),
            };
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine($"Could not reach service on pipe '{pipeName}'. Is BlockRdpBruteForce running?");
            return 2;
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Access denied connecting to the service pipe.");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"CLI failed: {ex.Message}");
            return 1;
        }
    }

    private static string ResolvePipeName()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddJsonFile(
                Path.Combine(
                    Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData",
                    "BlockRdpBruteForce", "appsettings.json"),
                optional: true)
            .Build();

        var configured = config[$"{AppOptions.SectionName}:PipeName"];
        return string.IsNullOrWhiteSpace(configured) ? "BlockRdpBruteForce" : configured;
    }

    private static int RunStatus(string pipeName)
    {
        var response = SendRequest(pipeName, new PipeRequest { Op = PipeOps.Status });
        if (!response.Ok || response.Status is null)
            return ReportError(response, "status failed");

        var s = response.Status;
        Console.WriteLine($"Service:         {s.ServiceName}");
        Console.WriteLine($"Started:         {s.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"Now:             {s.NowUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"Threshold:       {s.FailureThreshold} failures in {s.SlidingWindowMinutes} min");
        Console.WriteLine($"Block duration:  {(s.BlockDurationMinutes <= 0 ? "permanent" : s.BlockDurationMinutes + " min")}");
        Console.WriteLine($"Firewall rule:   {s.FirewallRuleName}");
        Console.WriteLine($"Whitelist:       {s.WhitelistEntryCount} entries");
        Console.WriteLine($"NLA fallback:    {(s.EvaluateNlaFallback ? "enabled" : "disabled")}");
        Console.WriteLine($"Blocked IPs:     {s.BlockedIpCount}");
        Console.WriteLine(s.PausedUntilUtc is { } until
            ? $"Paused until:    {until.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : "Paused:          no");
        return 0;
    }

    private static int RunList(string pipeName)
    {
        var response = SendRequest(pipeName, new PipeRequest { Op = PipeOps.List });
        if (!response.Ok || response.Items is null)
            return ReportError(response, "list failed");

        if (response.Items.Count == 0)
        {
            Console.WriteLine("No blocked IPs.");
            return 0;
        }

        var nowUtc = DateTime.UtcNow;
        Console.WriteLine($"{"IP",-39}  {"Count",5}  {"First seen",-19}  {"Last seen",-19}  {"TTL",-12}");
        Console.WriteLine(new string('-', 39 + 2 + 5 + 2 + 19 + 2 + 19 + 2 + 12));
        foreach (var item in response.Items.OrderBy(i => i.Ip, StringComparer.Ordinal))
        {
            string ttl = item.BlockedUntilUtc is null
                ? "permanent"
                : item.BlockedUntilUtc <= nowUtc
                    ? "expired"
                    : FormatDuration(item.BlockedUntilUtc.Value - nowUtc);
            Console.WriteLine(
                $"{item.Ip,-39}  {item.Count,5}  {item.FirstSeenUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {item.LastSeenUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {ttl,-12}");
        }
        return 0;
    }

    private static int RunUnblock(string pipeName, string[] args)
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine("Usage: BlockRdpBruteForce unblock <ip>");
            return 64;
        }
        if (!IPAddress.TryParse(args[1].Trim(), out var ip))
        {
            Console.Error.WriteLine($"Invalid IP: {args[1]}");
            return 64;
        }

        var response = SendRequest(pipeName, new PipeRequest { Op = PipeOps.Unblock, Ip = ip.ToString() });
        if (!response.Ok || response.Unblock is null)
            return ReportError(response, "unblock failed");

        Console.WriteLine(response.Unblock.WasBlocked
            ? $"Unblocked {response.Unblock.Ip}."
            : $"{response.Unblock.Ip} was not in the block list.");
        return 0;
    }

    private static int RunPause(string pipeName, string[] args)
    {
        var minutes = 60;
        if (args.Length >= 2 && int.TryParse(args[1], out var parsed) && parsed > 0)
            minutes = parsed;

        var response = SendRequest(pipeName,
            new PipeRequest { Op = PipeOps.Pause, PauseMinutes = minutes });
        if (!response.Ok || response.Pause is null)
            return ReportError(response, "pause failed");

        Console.WriteLine(response.Pause.PausedUntilUtc is { } until
            ? $"Paused until {until.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : "Resumed.");
        return 0;
    }

    private static int RunSimple(string pipeName, PipeRequest request, string okMessage)
    {
        var response = SendRequest(pipeName, request);
        if (!response.Ok) return ReportError(response, request.Op + " failed");
        Console.WriteLine(okMessage);
        return 0;
    }

    private static int RunConfig(string pipeName, string[] args)
    {
        var sub = args.Length >= 2 ? args[1].ToLowerInvariant() : "get";

        if (sub == "get")
        {
            var response = SendRequest(pipeName, new PipeRequest { Op = PipeOps.ConfigGet });
            if (!response.Ok || response.ConfigEffective is null)
                return ReportError(response, "config get failed");
            PrintConfig(response.ConfigEffective);
            return 0;
        }

        if (sub == "set")
        {
            if (args.Length < 4)
            {
                Console.Error.WriteLine("Usage: BlockRdpBruteForce config set <key> <value>");
                Console.Error.WriteLine("  Keys: failure-threshold, sliding-window-minutes, block-duration-minutes,");
                Console.Error.WriteLine("        firewall-scope, evaluate-nla-fallback, history-retention-days");
                return 64;
            }
            var key = args[2].ToLowerInvariant();
            var value = args[3];
            var payload = new ConfigPayload();
            switch (key)
            {
                case "failure-threshold":
                    if (!int.TryParse(value, out var ft))
                        return Fail($"failure-threshold must be an integer (got '{value}')");
                    payload.FailureThreshold = ft;
                    break;
                case "sliding-window-minutes":
                    if (!int.TryParse(value, out var sw))
                        return Fail($"sliding-window-minutes must be an integer (got '{value}')");
                    payload.SlidingWindowMinutes = sw;
                    break;
                case "block-duration-minutes":
                    if (!int.TryParse(value, out var bd))
                        return Fail($"block-duration-minutes must be an integer (got '{value}')");
                    payload.BlockDurationMinutes = bd;
                    break;
                case "firewall-scope":
                    payload.FirewallScope = value;
                    break;
                case "evaluate-nla-fallback":
                    if (!bool.TryParse(value, out var nla))
                        return Fail($"evaluate-nla-fallback must be true/false (got '{value}')");
                    payload.EvaluateNlaFallback = nla;
                    break;
                case "history-retention-days":
                    if (!int.TryParse(value, out var hr))
                        return Fail($"history-retention-days must be an integer (got '{value}')");
                    payload.HistoryRetentionDays = hr;
                    break;
                default:
                    return Fail($"unknown config key: {key}");
            }

            var response = SendRequest(pipeName, new PipeRequest { Op = PipeOps.ConfigSet, Config = payload });
            if (!response.Ok || response.ConfigSet is null)
                return ReportError(response, "config set failed");
            PrintSetResult(response.ConfigSet);
            return 0;
        }

        Console.Error.WriteLine("Usage: BlockRdpBruteForce config [get|set <key> <value>]");
        return 64;
    }

    private static int RunWhitelist(string pipeName, string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: BlockRdpBruteForce whitelist add|remove <cidr>");
            return 64;
        }
        var sub = args[1].ToLowerInvariant();
        var cidr = args[2];
        var op = sub switch
        {
            "add" => PipeOps.WhitelistAdd,
            "remove" => PipeOps.WhitelistRemove,
            _ => null,
        };
        if (op is null)
        {
            Console.Error.WriteLine("Usage: BlockRdpBruteForce whitelist add|remove <cidr>");
            return 64;
        }

        var response = SendRequest(pipeName, new PipeRequest { Op = op, Cidr = cidr });
        if (!response.Ok || response.ConfigSet is null)
            return ReportError(response, $"whitelist {sub} failed");
        PrintSetResult(response.ConfigSet);
        return 0;
    }

    private static void PrintConfig(ConfigPayload c)
    {
        Console.WriteLine($"FailureThreshold:     {c.FailureThreshold}");
        Console.WriteLine($"SlidingWindowMinutes: {c.SlidingWindowMinutes}");
        Console.WriteLine($"BlockDurationMinutes: {c.BlockDurationMinutes}{(c.BlockDurationMinutes <= 0 ? " (permanent)" : string.Empty)}");
        Console.WriteLine($"FirewallScope:        {c.FirewallScope}");
        Console.WriteLine($"EvaluateNlaFallback:  {c.EvaluateNlaFallback}");
        Console.WriteLine($"HistoryRetentionDays: {c.HistoryRetentionDays}{(c.HistoryRetentionDays <= 0 ? " (keep forever)" : string.Empty)}");
        Console.WriteLine($"Whitelist:            {(c.Whitelist is { Count: > 0 } w ? string.Join(", ", w) : "(empty)")}");
    }

    private static void PrintSetResult(ConfigSetResult result)
    {
        if (result.AppliedHot.Count > 0 && !result.RestartRequired)
            Console.WriteLine("Settings updated. Active immediately (whitelist hot-applied).");
        else if (result.AppliedHot.Count > 0 && result.RestartRequired)
            Console.WriteLine("Settings updated. Whitelist active immediately; other changes require service restart.");
        else
            Console.WriteLine("Settings updated. Restart the service to take effect (e.g., `Restart-Service BlockRdpBruteForce`).");
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 64;
    }

    private static PipeResponse SendRequest(string pipeName, PipeRequest request)
    {
        using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.None,
            TokenImpersonationLevel.Identification);
        client.Connect(5000);

        client.Write(PipeProtocol.Encode(request));
        client.Flush();

        var responseBytes = ReadLine(client);
        if (responseBytes is null)
            return PipeResponse.Failure("no response");

        return PipeProtocol.Decode<PipeResponse>(responseBytes)
            ?? PipeResponse.Failure("could not parse response");
    }

    private static byte[]? ReadLine(NamedPipeClientStream client)
    {
        var buf = new byte[1024];
        using var ms = new MemoryStream(1024);
        while (true)
        {
            var read = client.Read(buf, 0, buf.Length);
            if (read <= 0) return ms.Length == 0 ? null : ms.ToArray();
            for (var i = 0; i < read; i++)
            {
                if (buf[i] == (byte)'\n')
                {
                    ms.Write(buf, 0, i);
                    return ms.ToArray();
                }
            }
            ms.Write(buf, 0, read);
            if (ms.Length > PipeProtocol.MaxRequestBytes * 4)
                return ms.ToArray();
        }
    }

    private static int ReportError(PipeResponse response, string fallback)
    {
        Console.Error.WriteLine(response.Error ?? fallback);
        return 1;
    }

    private static int PrintUsage()
    {
        var text = new StringBuilder();
        text.AppendLine("Usage: BlockRdpBruteForce <verb>");
        text.AppendLine("  status                       Print service status");
        text.AppendLine("  list                         List blocked IPs");
        text.AppendLine("  unblock <ip>                 Remove IP from block list (admin only)");
        text.AppendLine("  pause [minutes]              Pause blocking (default 60 min, admin only)");
        text.AppendLine("  resume                       Resume blocking (admin only)");
        text.AppendLine("  config [get]                 Show effective settings (admin only)");
        text.AppendLine("  config set <key> <value>     Update a setting (admin only)");
        text.AppendLine("  whitelist add <cidr>         Add IP/CIDR to whitelist (hot, admin only)");
        text.AppendLine("  whitelist remove <cidr>      Remove IP/CIDR from whitelist (hot, admin only)");
        Console.Error.WriteLine(text.ToString());
        return 64;
    }

    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m";
        return $"{(int)span.TotalSeconds}s";
    }
}

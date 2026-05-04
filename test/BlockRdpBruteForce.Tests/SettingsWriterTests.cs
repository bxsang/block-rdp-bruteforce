using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlockRdpBruteForce.Configuration;
using BlockRdpBruteForce.Ipc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlockRdpBruteForce.Tests;

[SupportedOSPlatform("windows")]
public sealed class SettingsWriterTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly SettingsWriter _writer;

    public SettingsWriterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"brbf-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "appsettings.json");

        var initial = new ConfigPayload
        {
            FailureThreshold = 5,
            SlidingWindowMinutes = 10,
            BlockDurationMinutes = 1440,
            Whitelist = new List<string> { "127.0.0.1", "10.0.0.0/8" },
            FirewallScope = "AllPorts",
            EvaluateNlaFallback = true,
        };
        _writer = new SettingsWriter(initial, _path, NullLogger<SettingsWriter>.Instance);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    [Fact]
    public void GetEffective_returns_clone()
    {
        var a = _writer.GetEffective();
        a.FailureThreshold = 999;
        var b = _writer.GetEffective();
        Assert.Equal(5, b.FailureThreshold);
    }

    [Fact]
    public void Apply_no_changes_is_noop_and_does_not_write_file()
    {
        var result = _writer.Apply(new ConfigPayload(), "test");
        Assert.False(result.RestartRequired);
        Assert.Empty(result.AppliedHot);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Apply_threshold_change_writes_file_and_requires_restart()
    {
        var result = _writer.Apply(new ConfigPayload { FailureThreshold = 7 }, "test");

        Assert.True(result.RestartRequired);
        Assert.Empty(result.AppliedHot);
        Assert.Equal(7, result.Effective.FailureThreshold);
        Assert.True(File.Exists(_path));

        var json = JsonNode.Parse(File.ReadAllText(_path))!;
        Assert.Equal(7, json["BlockRdp"]!["FailureThreshold"]!.GetValue<int>());
        // Other managed keys should also be present (full BlockRdp section is rewritten).
        Assert.Equal(10, json["BlockRdp"]!["SlidingWindowMinutes"]!.GetValue<int>());
    }

    [Fact]
    public void Apply_whitelist_only_change_does_not_require_restart()
    {
        var result = _writer.Apply(
            new ConfigPayload { Whitelist = new List<string> { "127.0.0.1", "10.0.0.0/8", "192.168.0.0/16" } },
            "test");

        Assert.False(result.RestartRequired);
        Assert.Contains("whitelist", result.AppliedHot);
        Assert.Equal(3, result.Effective.Whitelist!.Count);
    }

    [Fact]
    public void Apply_mixed_change_reports_hot_whitelist_and_restart_required()
    {
        var result = _writer.Apply(
            new ConfigPayload
            {
                FailureThreshold = 8,
                Whitelist = new List<string> { "172.16.0.0/12" },
            },
            "test");

        Assert.True(result.RestartRequired);
        Assert.Contains("whitelist", result.AppliedHot);
    }

    [Fact]
    public void Validate_rejects_threshold_below_one()
    {
        Assert.Throws<ConfigValidationException>(() => _writer.Apply(
            new ConfigPayload { FailureThreshold = 0 }, "test"));
    }

    [Fact]
    public void Validate_rejects_bad_whitelist_entry()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => _writer.Apply(
            new ConfigPayload { Whitelist = new List<string> { "not-an-ip" } }, "test"));
        Assert.Contains("not-an-ip", ex.Message);
    }

    [Fact]
    public void Validate_rejects_unknown_firewall_scope()
    {
        Assert.Throws<ConfigValidationException>(() => _writer.Apply(
            new ConfigPayload { FirewallScope = "Bogus" }, "test"));
    }

    [Fact]
    public void Validate_enforces_self_lockout_invariant()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => _writer.Apply(
            new ConfigPayload { Whitelist = new List<string>(), FailureThreshold = 1 }, "test"));
        Assert.Contains("lockout", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_does_not_write_file_when_validation_fails()
    {
        Assert.Throws<ConfigValidationException>(() => _writer.Apply(
            new ConfigPayload { FailureThreshold = -1 }, "test"));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Apply_preserves_non_managed_top_level_keys_in_existing_file()
    {
        File.WriteAllText(_path, """
        {
          "Logging": { "LogLevel": { "Default": "Debug" } },
          "BlockRdp": { "PipeName": "CustomPipe" }
        }
        """);

        _writer.Apply(new ConfigPayload { FailureThreshold = 9 }, "test");

        var json = JsonNode.Parse(File.ReadAllText(_path))!;
        Assert.Equal("Debug", json["Logging"]!["LogLevel"]!["Default"]!.GetValue<string>());
        // Unmanaged BlockRdp keys preserved
        Assert.Equal("CustomPipe", json["BlockRdp"]!["PipeName"]!.GetValue<string>());
        // Managed key written
        Assert.Equal(9, json["BlockRdp"]!["FailureThreshold"]!.GetValue<int>());
    }

    [Fact]
    public void Apply_replaces_corrupt_existing_file()
    {
        File.WriteAllText(_path, "this is { not json");

        _writer.Apply(new ConfigPayload { FailureThreshold = 4 }, "test");

        var json = JsonNode.Parse(File.ReadAllText(_path))!;
        Assert.Equal(4, json["BlockRdp"]!["FailureThreshold"]!.GetValue<int>());
    }

    [Fact]
    public void Apply_does_not_leave_tmp_file_behind()
    {
        _writer.Apply(new ConfigPayload { FailureThreshold = 6 }, "test");
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public void Apply_normalizes_firewall_scope_case()
    {
        var result = _writer.Apply(new ConfigPayload { FirewallScope = "rdponly" }, "test");
        Assert.Equal("RdpOnly", result.Effective.FirewallScope);
    }

    [Fact]
    public void Apply_writes_whitelist_as_json_array()
    {
        _writer.Apply(
            new ConfigPayload { Whitelist = new List<string> { "1.2.3.4", "5.6.7.0/24" } },
            "test");

        var json = JsonNode.Parse(File.ReadAllText(_path))!;
        var arr = json["BlockRdp"]!["Whitelist"]!.AsArray();
        Assert.Equal(2, arr.Count);
        Assert.Equal("1.2.3.4", arr[0]!.GetValue<string>());
    }

    [Fact]
    public void Apply_resending_same_whitelist_is_noop()
    {
        var result = _writer.Apply(
            new ConfigPayload { Whitelist = new List<string> { "127.0.0.1", "10.0.0.0/8" } },
            "test");

        Assert.False(result.RestartRequired);
        Assert.Empty(result.AppliedHot);
        Assert.False(File.Exists(_path));
    }
}

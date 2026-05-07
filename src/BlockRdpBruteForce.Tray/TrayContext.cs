using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using BlockRdpBruteForce.Configuration;
using BlockRdpBruteForce.Ipc;
using BlockRdpBruteForce.Tray.Forms;
using Microsoft.Extensions.Configuration;

namespace BlockRdpBruteForce.Tray;

[SupportedOSPlatform("windows")]
public sealed class TrayContext : ApplicationContext
{
    private const int PollIntervalMs = 5000;
    private const int PauseMinutesDefault = 60;
    // Poll the update endpoint every ~5 minutes (60 ticks of the 5-second timer).
    private const int UpdatePollEveryNTicks = 60;

    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly PipeClient _client;
    private readonly string _logFolder;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _showItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _resumeItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _viewLogsItem;
    private readonly ToolStripMenuItem _openLogsItem;
    private readonly ToolStripMenuItem _checkUpdatesItem;
    private readonly ToolStripMenuItem _restartAsAdminItem;
    private readonly ToolStripMenuItem _exitItem;
    private ToolStripMenuItem? _installUpdateItem;
    private ToolStripSeparator? _installUpdateSeparator;
    private BlockedIpsForm? _openForm;
    private SettingsForm? _settingsForm;
    private LogViewerForm? _logViewerForm;
    private int _tickCount;
    private string? _ackedVersion;
    private string? _availableVersion;

    public TrayContext()
    {
        var (pipeName, logFolder) = LoadConfig();
        _logFolder = logFolder;
        _client = new PipeClient(pipeName);

        _showItem = new ToolStripMenuItem("Show blocked IPs...", null, OnShow);
        _pauseItem = new ToolStripMenuItem($"Pause for {PauseMinutesDefault} minutes", null, OnPause);
        _resumeItem = new ToolStripMenuItem("Resume", null, OnResume) { Visible = false };
        _settingsItem = new ToolStripMenuItem("Settings...", null, OnSettings);
        _viewLogsItem = new ToolStripMenuItem("View logs...", null, OnViewLogs);
        _openLogsItem = new ToolStripMenuItem("Open log folder", null, OnOpenLogs);
        _checkUpdatesItem = new ToolStripMenuItem("Check for updates...", null, OnCheckUpdates);
        _restartAsAdminItem = new ToolStripMenuItem("Restart as Administrator", null, OnRestartAsAdmin)
        {
            Visible = !IsRunningAsAdmin()
        };
        _exitItem = new ToolStripMenuItem("Exit tray", null, (_, _) => ExitThread());

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_showItem);
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(_resumeItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_settingsItem);
        _menu.Items.Add(_viewLogsItem);
        _menu.Items.Add(_openLogsItem);
        _menu.Items.Add(_checkUpdatesItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_restartAsAdminItem);
        _menu.Items.Add(_exitItem);

        _ackedVersion = LoadAckedVersion();

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "BlockRdpBruteForce",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.DoubleClick += OnShow;
        _icon.BalloonTipClicked += OnBalloonClicked;

        _timer = new System.Windows.Forms.Timer { Interval = PollIntervalMs };
        _timer.Tick += async (_, _) => await OnTickAsync();
        _timer.Start();

        _ = PollStatusAsync();
        _ = PollUpdateAsync(verbose: false);
    }

    private async Task OnTickAsync()
    {
        _tickCount++;
        await PollStatusAsync();
        if (_tickCount % UpdatePollEveryNTicks == 0)
            await PollUpdateAsync(verbose: false);
    }

    private static (string PipeName, string LogFolder) LoadConfig()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddJsonFile(
                Path.Combine(
                    Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData",
                    "BlockRdpBruteForce", "appsettings.json"),
                optional: true)
            .Build();

        var section = config.GetSection(AppOptions.SectionName);
        var pipe = section["PipeName"];
        if (string.IsNullOrWhiteSpace(pipe)) pipe = "BlockRdpBruteForce";

        var rawLog = section["LogPath"] ?? @"%ProgramData%\BlockRdpBruteForce\logs\service-.log";
        var expanded = Environment.ExpandEnvironmentVariables(rawLog);
        var folder = Path.GetDirectoryName(expanded) ?? expanded;

        return (pipe, folder);
    }

    private async Task PollStatusAsync()
    {
        try
        {
            var status = await _client.StatusAsync();
            var pausedSuffix = status.PausedUntilUtc is { } until
                ? $" — paused until {until.ToLocalTime():HH:mm}"
                : string.Empty;
            _icon.Text = Truncate(
                $"BlockRdpBruteForce — {status.BlockedIpCount} blocked{pausedSuffix}");
            _resumeItem.Visible = status.PausedUntilUtc is not null;
            _pauseItem.Enabled = status.PausedUntilUtc is null;
        }
        catch (TimeoutException)
        {
            _icon.Text = "BlockRdpBruteForce — service unreachable";
        }
        catch (Exception ex)
        {
            _icon.Text = Truncate($"BlockRdpBruteForce — error: {ex.Message}");
        }
    }

    private void OnShow(object? sender, EventArgs e)
    {
        if (_openForm is { IsDisposed: false })
        {
            _openForm.BringToFront();
            _openForm.Activate();
            return;
        }

        _openForm = new BlockedIpsForm(_client);
        _openForm.FormClosed += (_, _) => _openForm = null;
        _openForm.Show();
        _openForm.Activate();
    }

    private async void OnPause(object? sender, EventArgs e)
    {
        try
        {
            var payload = await _client.PauseAsync(PauseMinutesDefault);
            await PollStatusAsync();
            if (payload.PausedUntilUtc is { } until)
                ShowBalloon("Paused", $"Blocking paused until {until.ToLocalTime():HH:mm}");
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(
                ex.Message + "\n\nPause requires running the tray as Administrator.",
                "Could not pause",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void OnResume(object? sender, EventArgs e)
    {
        try
        {
            await _client.ResumeAsync();
            await PollStatusAsync();
            ShowBalloon("Resumed", "Blocking resumed.");
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(
                ex.Message + "\n\nResume requires running the tray as Administrator.",
                "Could not resume",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnSettings(object? sender, EventArgs e)
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.BringToFront();
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_client);
        _settingsForm.FormClosed += (_, _) => _settingsForm = null;
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    private void OnViewLogs(object? sender, EventArgs e)
    {
        if (_logViewerForm is { IsDisposed: false })
        {
            _logViewerForm.BringToFront();
            _logViewerForm.Activate();
            return;
        }

        _logViewerForm = new LogViewerForm(_logFolder);
        _logViewerForm.FormClosed += (_, _) => _logViewerForm = null;
        _logViewerForm.Show();
        _logViewerForm.Activate();
    }

    private void OnRestartAsAdmin(object? sender, EventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Application.ExecutablePath)
            {
                UseShellExecute = true,
                Verb = "runas"
            });
            ExitThread();
        }
        catch (OperationCanceledException) { } // user cancelled UAC
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not restart as Administrator",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static bool IsRunningAsAdmin()
    {
        // CheckTokenMembership(null) uses the process token and checks that the
        // Administrators SID is both present AND enabled — correctly returns false
        // for a non-elevated admin process (UAC filtered token).
        var sid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var sidBytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(sidBytes, 0);
        return CheckTokenMembership(IntPtr.Zero, sidBytes, out var isMember) && isMember;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CheckTokenMembership(
        IntPtr tokenHandle, byte[] sidToCheck, out bool isMember);

    private void OnOpenLogs(object? sender, EventArgs e)
    {
        try
        {
            if (!Directory.Exists(_logFolder))
            {
                MessageBox.Show(
                    $"Log folder not found:\n{_logFolder}",
                    "BlockRdpBruteForce",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_logFolder}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowBalloon(string title, string text)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = text;
            _icon.ShowBalloonTip(3000);
        }
        catch { }
    }

    private static string Truncate(string s) => s.Length <= 63 ? s : s[..63];

    private async Task PollUpdateAsync(bool verbose)
    {
        UpdateStatusPayload? status;
        try
        {
            status = await _client.UpdateStatusAsync();
        }
        catch
        {
            if (verbose) ShowBalloon("Update check", "Service unreachable.");
            return;
        }

        ApplyUpdateStatus(status, verbose);
    }

    private void ApplyUpdateStatus(UpdateStatusPayload status, bool verbose)
    {
        if (status.UpdateAvailable && !string.IsNullOrWhiteSpace(status.LatestVersion))
        {
            _availableVersion = status.LatestVersion;
            EnsureInstallMenuItem(status.LatestVersion!);

            if (!string.Equals(_ackedVersion, status.LatestVersion, StringComparison.OrdinalIgnoreCase))
            {
                ShowBalloon(
                    "Update available",
                    $"BlockRdpBruteForce {status.LatestVersion} is available — click to install.");
            }
            else if (verbose)
            {
                ShowBalloon(
                    "Update available",
                    $"BlockRdpBruteForce {status.LatestVersion} is ready to install (right-click tray to begin).");
            }
        }
        else
        {
            _availableVersion = null;
            RemoveInstallMenuItem();

            if (verbose)
            {
                var current = string.IsNullOrEmpty(status.CurrentVersion) ? "current" : status.CurrentVersion;
                ShowBalloon("No updates", $"You're on the latest version ({current}).");
            }
        }
    }

    private void EnsureInstallMenuItem(string version)
    {
        if (_installUpdateItem is { IsDisposed: false })
        {
            _installUpdateItem.Text = $"Install update {version}...";
            return;
        }

        _installUpdateSeparator = new ToolStripSeparator();
        _installUpdateItem = new ToolStripMenuItem(
            $"Install update {version}...", null, OnInstallUpdate)
        {
            Font = new Font(_menu.Font, FontStyle.Bold),
        };
        _menu.Items.Insert(0, _installUpdateItem);
        _menu.Items.Insert(1, _installUpdateSeparator);
    }

    private void RemoveInstallMenuItem()
    {
        if (_installUpdateItem is not null)
        {
            _menu.Items.Remove(_installUpdateItem);
            _installUpdateItem.Dispose();
            _installUpdateItem = null;
        }
        if (_installUpdateSeparator is not null)
        {
            _menu.Items.Remove(_installUpdateSeparator);
            _installUpdateSeparator.Dispose();
            _installUpdateSeparator = null;
        }
    }

    private async void OnCheckUpdates(object? sender, EventArgs e)
    {
        try
        {
            _checkUpdatesItem.Enabled = false;
            var status = await _client.UpdateCheckNowAsync();
            ApplyUpdateStatus(status, verbose: true);
        }
        catch (Exception ex)
        {
            ShowBalloon("Update check failed", ex.Message);
        }
        finally
        {
            _checkUpdatesItem.Enabled = true;
        }
    }

    private void OnBalloonClicked(object? sender, EventArgs e)
    {
        if (!string.IsNullOrEmpty(_availableVersion))
            BeginInstallUpdate(_availableVersion!);
    }

    private void OnInstallUpdate(object? sender, EventArgs e)
    {
        if (!string.IsNullOrEmpty(_availableVersion))
            BeginInstallUpdate(_availableVersion!);
    }

    private async void BeginInstallUpdate(string version)
    {
        var confirm = MessageBox.Show(
            $"Install BlockRdpBruteForce {version} now?\n\n" +
            "The service will briefly restart, you'll see a Windows Installer progress " +
            "dialog, and the tray will reappear automatically when the upgrade is done.",
            "Install update",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);

        if (confirm != DialogResult.Yes)
        {
            SaveAckedVersion(version);
            return;
        }

        try
        {
            var result = await _client.UpdateApplyAsync(version);
            if (!result.Started)
            {
                MessageBox.Show(
                    result.Message ?? "The service refused to start the update.",
                    "Could not install update",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            SaveAckedVersion(version);
            ShowBalloon("Updating", "Installing BlockRdpBruteForce — the tray will close shortly.");
            // Give the user the balloon, then exit so MSI can replace our exe.
            await Task.Delay(2000);
            ExitThread();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not install update",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string AckPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "BlockRdpBruteForce", "update-ack.json");
    }

    private static string? LoadAckedVersion()
    {
        try
        {
            var path = AckPath();
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            var doc = JsonDocument.Parse(fs);
            return doc.RootElement.TryGetProperty("ackedVersion", out var v)
                ? v.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void SaveAckedVersion(string version)
    {
        try
        {
            var path = AckPath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(new { ackedVersion = version, ackedAtUtc = DateTime.UtcNow });
            File.WriteAllText(path, json);
            _ackedVersion = version;
        }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _openForm?.Dispose();
            _settingsForm?.Dispose();
            _logViewerForm?.Dispose();
        }
        base.Dispose(disposing);
    }
}

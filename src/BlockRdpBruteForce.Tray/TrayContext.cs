using System.Diagnostics;
using System.Runtime.Versioning;
using BlockRdpBruteForce.Configuration;
using BlockRdpBruteForce.Tray.Forms;
using Microsoft.Extensions.Configuration;

namespace BlockRdpBruteForce.Tray;

[SupportedOSPlatform("windows")]
public sealed class TrayContext : ApplicationContext
{
    private const int PollIntervalMs = 5000;
    private const int PauseMinutesDefault = 60;

    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly PipeClient _client;
    private readonly string _logFolder;
    private readonly ToolStripMenuItem _showItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _resumeItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _openLogsItem;
    private readonly ToolStripMenuItem _exitItem;
    private BlockedIpsForm? _openForm;
    private SettingsForm? _settingsForm;

    public TrayContext()
    {
        var (pipeName, logFolder) = LoadConfig();
        _logFolder = logFolder;
        _client = new PipeClient(pipeName);

        _showItem = new ToolStripMenuItem("Show blocked IPs...", null, OnShow);
        _pauseItem = new ToolStripMenuItem($"Pause for {PauseMinutesDefault} minutes", null, OnPause);
        _resumeItem = new ToolStripMenuItem("Resume", null, OnResume) { Visible = false };
        _settingsItem = new ToolStripMenuItem("Settings...", null, OnSettings);
        _openLogsItem = new ToolStripMenuItem("Open log folder", null, OnOpenLogs);
        _exitItem = new ToolStripMenuItem("Exit tray", null, (_, _) => ExitThread());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_showItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_resumeItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_settingsItem);
        menu.Items.Add(_openLogsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_exitItem);

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "BlockRdpBruteForce",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += OnShow;

        _timer = new System.Windows.Forms.Timer { Interval = PollIntervalMs };
        _timer.Tick += async (_, _) => await PollStatusAsync();
        _timer.Start();

        _ = PollStatusAsync();
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
        }
        base.Dispose(disposing);
    }
}

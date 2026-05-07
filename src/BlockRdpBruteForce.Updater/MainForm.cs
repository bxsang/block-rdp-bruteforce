using System.Diagnostics;
using System.Runtime.Versioning;

namespace BlockRdpBruteForce.Updater;

[SupportedOSPlatform("windows")]
internal sealed class MainForm : Form
{
    private readonly UpdaterArgs _args;
    private readonly StageWriter _stageWriter;
    private readonly CancellationTokenSource _cts = new();

    private readonly Label _titleLabel;
    private readonly Label _statusLabel;
    private readonly ProgressBar _progress;
    private readonly Label _detailLabel;
    private readonly Button _primaryButton;
    private readonly Button _secondaryButton;

    private enum UiState { Downloading, Installing, ResultSuccess, ResultFailed, ResultCancelled }
    private UiState _state = UiState.Downloading;
    private bool _allowClose;

    public MainForm(UpdaterArgs args, StageWriter stageWriter)
    {
        _args = args;
        _stageWriter = stageWriter;

        Text = "BlockRdpBruteForce Updater";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = true;
        ClientSize = new Size(460, 200);
        ShowInTaskbar = true;

        _titleLabel = new Label
        {
            Text = $"Installing BlockRdpBruteForce {args.Version}",
            Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11f, FontStyle.Bold),
            Location = new Point(20, 16),
            AutoSize = true,
        };

        _statusLabel = new Label
        {
            Text = "Preparing download…",
            Location = new Point(20, 50),
            Size = new Size(420, 20),
            AutoEllipsis = true,
        };

        _progress = new ProgressBar
        {
            Location = new Point(20, 76),
            Size = new Size(420, 22),
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Style = ProgressBarStyle.Continuous,
        };

        _detailLabel = new Label
        {
            Text = string.Empty,
            Location = new Point(20, 104),
            Size = new Size(420, 20),
            AutoEllipsis = true,
            ForeColor = SystemColors.GrayText,
        };

        _primaryButton = new Button
        {
            Text = "Cancel",
            Location = new Point(360, 156),
            Size = new Size(80, 28),
        };
        _primaryButton.Click += OnPrimaryClick;

        _secondaryButton = new Button
        {
            Text = "Open log",
            Location = new Point(260, 156),
            Size = new Size(90, 28),
            Visible = false,
        };
        _secondaryButton.Click += OnOpenLogClick;

        Controls.Add(_titleLabel);
        Controls.Add(_statusLabel);
        Controls.Add(_progress);
        Controls.Add(_detailLabel);
        Controls.Add(_secondaryButton);
        Controls.Add(_primaryButton);

        FormClosing += OnFormClosing;
        Shown += async (_, _) => await RunAsync().ConfigureAwait(true);
    }

    private async Task RunAsync()
    {
        try
        {
            _stageWriter.Write(StageWriter.StageDownloading);
            var downloaded = await DownloadAsync(_cts.Token).ConfigureAwait(true);
            if (!downloaded)
            {
                return;
            }

            EnterInstallingState();
            _stageWriter.Write(StageWriter.StageInstalling);
            var installResult = await MsiInstaller.RunAsync(_args.MsiPath, _args.LogPath, _cts.Token)
                .ConfigureAwait(true);

            if (installResult.Ok)
            {
                _stageWriter.Write(StageWriter.StageDone);
                EnterResultState(UiState.ResultSuccess, installResult.RebootRequired
                    ? $"Update complete (v{_args.Version}). A reboot is required to finish."
                    : $"Update complete (v{_args.Version}).",
                    detail: $"msiexec exit code {installResult.ExitCode}");
            }
            else if (installResult.WasCancelled)
            {
                _stageWriter.Write(StageWriter.StageFailed, installResult.Error);
                EnterResultState(UiState.ResultCancelled, "Installation was cancelled.",
                    detail: $"msiexec exit code {installResult.ExitCode}", showOpenLog: true);
            }
            else
            {
                _stageWriter.Write(StageWriter.StageFailed, installResult.Error);
                EnterResultState(UiState.ResultFailed,
                    installResult.Error ?? $"Installation failed (exit {installResult.ExitCode}).",
                    detail: $"msiexec exit code {installResult.ExitCode}", showOpenLog: true);
            }
        }
        catch (OperationCanceledException)
        {
            _stageWriter.Write(StageWriter.StageFailed, "cancelled by user");
            EnterResultState(UiState.ResultCancelled, "Update cancelled.", detail: string.Empty);
        }
        catch (Exception ex)
        {
            _stageWriter.Write(StageWriter.StageFailed, ex.Message);
            EnterResultState(UiState.ResultFailed, $"Unexpected error: {ex.Message}",
                detail: string.Empty, showOpenLog: File.Exists(_args.LogPath));
        }
    }

    private async Task<bool> DownloadAsync(CancellationToken ct)
    {
        if (TryReuseCachedMsi())
        {
            _statusLabel.Text = "Using previously downloaded installer.";
            _progress.Value = 100;
            return true;
        }

        _statusLabel.Text = $"Downloading {_args.AssetName}…";
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Value = 0;

        using var downloader = new HttpDownloaderHolder();
        var msi = MsiDownloader.CreateDefault();
        var progress = new Progress<DownloadProgress>(OnDownloadProgress);

        var result = await msi.DownloadAsync(_args.AssetUrl, _args.MsiPath, _args.AssetSize, progress, ct)
            .ConfigureAwait(true);

        if (result.Ok) return true;

        EnterResultState(UiState.ResultFailed, $"Download failed: {result.Error}",
            detail: string.Empty, showOpenLog: false);
        return false;
    }

    private bool TryReuseCachedMsi()
    {
        try
        {
            if (!File.Exists(_args.MsiPath)) return false;
            var size = new FileInfo(_args.MsiPath).Length;
            return size == _args.AssetSize && size >= 100_000;
        }
        catch
        {
            return false;
        }
    }

    private void OnDownloadProgress(DownloadProgress p)
    {
        if (IsDisposed || !IsHandleCreated) return;

        var totalMb = p.TotalBytes > 0 ? p.TotalBytes / 1024.0 / 1024.0 : 0;
        var doneMb = p.BytesRead / 1024.0 / 1024.0;
        var pct = p.TotalBytes > 0 ? (int)(p.BytesRead * 100 / p.TotalBytes) : 0;
        if (pct < 0) pct = 0;
        if (pct > 100) pct = 100;

        var speedMbps = p.BytesPerSecond / 1024.0 / 1024.0;
        _progress.Value = pct;
        _statusLabel.Text = $"Downloading {_args.AssetName}…";
        _detailLabel.Text = totalMb > 0
            ? $"{doneMb:F1} MB / {totalMb:F1} MB ({pct}%)  •  {speedMbps:F1} MB/s"
            : $"{doneMb:F1} MB  •  {speedMbps:F1} MB/s";
    }

    private void EnterInstallingState()
    {
        _state = UiState.Installing;
        _statusLabel.Text = $"Installing BlockRdpBruteForce {_args.Version}…";
        _detailLabel.Text = "The service and tray will restart automatically.";
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.MarqueeAnimationSpeed = 30;
        _primaryButton.Enabled = false;
    }

    private void EnterResultState(UiState state, string message, string detail, bool showOpenLog = false)
    {
        _state = state;
        _allowClose = true;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Value = state == UiState.ResultSuccess ? 100 : _progress.Value;
        _statusLabel.Text = message;
        _detailLabel.Text = detail;
        _primaryButton.Text = "Close";
        _primaryButton.Enabled = true;
        _secondaryButton.Visible = showOpenLog;
    }

    private void OnPrimaryClick(object? sender, EventArgs e)
    {
        if (_state == UiState.Downloading)
        {
            _cts.Cancel();
            _primaryButton.Enabled = false;
            _statusLabel.Text = "Cancelling…";
            return;
        }

        // Installing state: button is disabled. Result states: close.
        _allowClose = true;
        Close();
    }

    private void OnOpenLogClick(object? sender, EventArgs e)
    {
        try
        {
            if (!File.Exists(_args.LogPath))
            {
                MessageBox.Show(this, $"Log file not found at {_args.LogPath}",
                    "Open log", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = _args.LogPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open log",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose) return;

        if (_state == UiState.Downloading)
        {
            // Cancel download instead of closing immediately so the .tmp gets cleaned up.
            _cts.Cancel();
            e.Cancel = true;
            return;
        }

        if (_state == UiState.Installing)
        {
            var confirm = MessageBox.Show(this,
                "An installation is in progress and cannot be cancelled cleanly. " +
                "Closing this window will not stop msiexec. Close anyway?",
                "Installing",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes) e.Cancel = true;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }

    // Tiny holder so an `await using` stays scoped if we ever pool the HttpClient.
    private sealed class HttpDownloaderHolder : IDisposable
    {
        public void Dispose() { }
    }
}

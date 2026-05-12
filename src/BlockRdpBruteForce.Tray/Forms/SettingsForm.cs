using System.Diagnostics;
using System.Runtime.Versioning;
using BlockRdpBruteForce.Detection;
using BlockRdpBruteForce.Ipc;
using Microsoft.Win32;

namespace BlockRdpBruteForce.Tray.Forms;

[SupportedOSPlatform("windows")]
public sealed class SettingsForm : Form
{
    private const string AutostartRegPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AutostartValueName = "BlockRdpBruteForceTray";

    private readonly PipeClient _client;

    private readonly NumericUpDown _failureThreshold;
    private readonly NumericUpDown _slidingWindow;
    private readonly NumericUpDown _blockDuration;
    private readonly NumericUpDown _historyRetention;
    private readonly ComboBox _firewallScope;
    private readonly CheckBox _evaluateNla;
    private readonly ListBox _whitelistBox;
    private readonly Button _addWhitelist;
    private readonly Button _removeWhitelist;
    private readonly Button _applyButton;
    private readonly Button _reloadButton;
    private readonly Button _closeButton;
    private readonly Label _statusLabel;

    private readonly CheckBox _geoEnabled;
    private readonly TextBox _geoToken;
    private readonly NumericUpDown _geoInterval;
    private readonly Button _geoRefresh;
    private readonly Label _geoStatus;

    private readonly CheckBox _updateEnabled;
    private readonly NumericUpDown _updateInterval;
    private readonly Button _updateCheckNow;
    private readonly Button _updateInstall;
    private readonly Label _updateStatus;
    private string? _availableUpdateVersion;

    private readonly CheckBox _autostartEnabled;
    private readonly Label _autostartNote;

    private readonly TabControl _tabs;
    private readonly TabPage _generalTab;
    private readonly TabPage _whitelistTab;
    private readonly TabPage _geoTab;
    private readonly TabPage _updatesTab;
    private readonly TabPage _interfaceTab;

    private ConfigPayload? _loaded;
    private bool _suppressClosePrompt;

    public SettingsForm(PipeClient client)
    {
        _client = client;

        Text = "BlockRdpBruteForce — Settings";
        Width = 620;
        Height = 540;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(560, 480);
        FormBorderStyle = FormBorderStyle.Sizable;

        _failureThreshold = NewSpinner(1, 1000);
        _slidingWindow = NewSpinner(1, 1440);
        _blockDuration = NewSpinner(0, 525_600);
        _historyRetention = NewSpinner(0, 3650);

        _firewallScope = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };
        _firewallScope.Items.AddRange(new object[] { "AllPorts", "RdpOnly" });

        _evaluateNla = new CheckBox
        {
            Text = "Subscribe to RdpCoreTS event log (NLA fallback)",
            AutoSize = true,
        };

        _whitelistBox = new ListBox { Dock = DockStyle.Fill };
        _addWhitelist = new Button { Text = "Add…", Dock = DockStyle.Fill, Height = 28 };
        _removeWhitelist = new Button { Text = "Remove", Dock = DockStyle.Fill, Height = 28 };
        _addWhitelist.Click += (_, _) => OnAddWhitelist();
        _removeWhitelist.Click += async (_, _) => await OnRemoveWhitelistAsync();

        _geoEnabled = new CheckBox
        {
            Text = "Enable IP geolocation (Country / ASN / Org columns)",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
        };
        _geoToken = new TextBox
        {
            UseSystemPasswordChar = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };
        _geoInterval = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 30,
            Anchor = AnchorStyles.Left,
            Width = 80,
        };
        _geoRefresh = new Button
        {
            Text = "Refresh now",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
        };
        _geoRefresh.Click += async (_, _) => await OnGeoRefreshAsync();
        _geoStatus = new Label
        {
            Text = "Status: not loaded",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Height = 36,
        };

        _updateEnabled = new CheckBox
        {
            Text = "Automatically check for new releases",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
        };
        _updateInterval = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 168,
            Anchor = AnchorStyles.Left,
            Width = 80,
        };
        _updateCheckNow = new Button
        {
            Text = "Check now",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
        };
        _updateCheckNow.Click += async (_, _) => await OnUpdateCheckNowAsync();
        _updateInstall = new Button
        {
            Text = "Install update",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Visible = false,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
            Margin = new Padding(8, 3, 3, 3),
        };
        _updateInstall.Click += async (_, _) => await OnInstallUpdateAsync();
        _updateStatus = new Label
        {
            Text = "Status: not checked yet",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Height = 36,
        };

        _autostartEnabled = new CheckBox
        {
            Text = "Start BlockRdpBruteForce tray when I sign in",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
        };
        _autostartEnabled.CheckedChanged += OnAutostartToggled;
        _autostartNote = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Height = 36,
            ForeColor = SystemColors.GrayText,
            Padding = new Padding(0, 8, 0, 0),
        };

        _generalTab = BuildGeneralTab();
        _whitelistTab = BuildWhitelistTab();
        _geoTab = BuildGeoTab();
        _updatesTab = BuildUpdatesTab();
        _interfaceTab = BuildInterfaceTab();

        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs.TabPages.Add(_generalTab);
        _tabs.TabPages.Add(_whitelistTab);
        _tabs.TabPages.Add(_geoTab);
        _tabs.TabPages.Add(_updatesTab);
        _tabs.TabPages.Add(_interfaceTab);

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            ColumnCount = 4,
            Padding = new Padding(8, 6, 8, 6),
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _reloadButton = new Button { Text = "Reload", AutoSize = true };
        _reloadButton.Click += async (_, _) => await ReloadAsync();

        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        };

        _applyButton = new Button { Text = "Apply", AutoSize = true };
        _applyButton.Click += async (_, _) => await ApplyAsync();

        _closeButton = new Button { Text = "Close", AutoSize = true };
        _closeButton.Click += (_, _) => Close();

        bottom.Controls.Add(_reloadButton, 0, 0);
        bottom.Controls.Add(_statusLabel, 1, 0);
        bottom.Controls.Add(_applyButton, 2, 0);
        bottom.Controls.Add(_closeButton, 3, 0);

        Controls.Add(_tabs);
        Controls.Add(bottom);

        Shown += async (_, _) => await ReloadAsync();
        FormClosing += OnFormClosingPrompt;
    }

    private TabPage BuildGeneralTab()
    {
        var page = new TabPage("General") { Padding = new Padding(12), UseVisualStyleBackColor = true };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 6,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 6; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        AddRow(layout, 0, "Failure threshold (count):", _failureThreshold);
        AddRow(layout, 1, "Sliding window (minutes):", _slidingWindow);
        AddRow(layout, 2, "Block duration (minutes, 0 = permanent):", _blockDuration);
        AddRow(layout, 3, "History retention (days, 0 = keep forever):", _historyRetention);
        AddRow(layout, 4, "Firewall scope:", _firewallScope);
        AddRow(layout, 5, string.Empty, _evaluateNla);

        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildWhitelistTab()
    {
        var page = new TabPage("Whitelist") { Padding = new Padding(12), UseVisualStyleBackColor = true };
        var note = new Label
        {
            Text = "Whitelisted IPs and CIDR ranges are never blocked. Edits are saved immediately.",
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 32,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = SystemColors.GrayText,
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var sideButtons = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
        };
        sideButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sideButtons.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sideButtons.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sideButtons.Controls.Add(_addWhitelist, 0, 0);
        sideButtons.Controls.Add(_removeWhitelist, 0, 1);

        layout.Controls.Add(_whitelistBox, 0, 0);
        layout.Controls.Add(sideButtons, 1, 0);

        page.Controls.Add(layout);
        page.Controls.Add(note);
        return page;
    }

    private TabPage BuildGeoTab()
    {
        var page = new TabPage("GeoIP") { Padding = new Padding(12), UseVisualStyleBackColor = true };

        var inner = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 3,
            RowCount = 4,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (var i = 0; i < 4; i++)
            inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        inner.Controls.Add(_geoEnabled, 0, 0);
        inner.SetColumnSpan(_geoEnabled, 3);

        inner.Controls.Add(NewLabel("IPinfo token:"), 0, 1);
        inner.Controls.Add(_geoToken, 1, 1);
        var link = new LinkLabel
        {
            Text = "Get a free token",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(8, 4, 0, 0),
        };
        link.LinkClicked += (_, _) => OpenUrl("https://ipinfo.io/lite");
        inner.Controls.Add(link, 2, 1);

        inner.Controls.Add(NewLabel("Refresh every (days):"), 0, 2);
        inner.Controls.Add(_geoInterval, 1, 2);

        inner.Controls.Add(_geoRefresh, 0, 3);
        inner.Controls.Add(_geoStatus, 1, 3);
        inner.SetColumnSpan(_geoStatus, 2);

        var attribution = new Label
        {
            Text = "Uses IPinfo Lite (CC BY-SA 4.0). Database lives in %ProgramData%\\BlockRdpBruteForce\\geo.",
            Dock = DockStyle.Bottom,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            ForeColor = SystemColors.GrayText,
        };

        page.Controls.Add(inner);
        page.Controls.Add(attribution);
        return page;
    }

    private TabPage BuildUpdatesTab()
    {
        var page = new TabPage("Updates") { Padding = new Padding(12), UseVisualStyleBackColor = true };
        var inner = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 3,
            RowCount = 4,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (var i = 0; i < 4; i++)
            inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        inner.Controls.Add(_updateEnabled, 0, 0);
        inner.SetColumnSpan(_updateEnabled, 3);

        inner.Controls.Add(NewLabel("Check every (hours):"), 0, 1);
        inner.Controls.Add(_updateInterval, 1, 1);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left,
            Margin = Padding.Empty,
            WrapContents = false,
        };
        buttons.Controls.Add(_updateCheckNow);
        buttons.Controls.Add(_updateInstall);
        inner.Controls.Add(buttons, 0, 2);
        inner.SetColumnSpan(buttons, 3);

        inner.Controls.Add(_updateStatus, 0, 3);
        inner.SetColumnSpan(_updateStatus, 3);

        page.Controls.Add(inner);
        return page;
    }

    private TabPage BuildInterfaceTab()
    {
        var page = new TabPage("Interface") { Padding = new Padding(12), UseVisualStyleBackColor = true };
        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
        };
        stack.Controls.Add(_autostartEnabled);
        stack.Controls.Add(_autostartNote);
        page.Controls.Add(stack);
        return page;
    }

    private void LoadAutostartState()
    {
        var hkcuSet = IsHkcuAutostartSet();
        var hklmSet = IsHklmAutostartSet();

        _autostartEnabled.CheckedChanged -= OnAutostartToggled;
        _autostartEnabled.Checked = hkcuSet || hklmSet;
        _autostartEnabled.Enabled = true;
        _autostartEnabled.CheckedChanged += OnAutostartToggled;

        _autostartNote.Text = hklmSet
            ? "Currently enabled for all users (HKLM, set by the installer). Unticking will " +
              "prompt for Administrator approval to clear the machine-wide entry."
            : "Tick: write a per-user HKCU\\…\\Run entry (no admin needed). " +
              "Untick: also clear any HKLM entry, which prompts for Administrator approval.";
    }

    private static bool IsHkcuAutostartSet()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutostartRegPath, writable: false);
            return key?.GetValue(AutostartValueName) is string s && !string.IsNullOrEmpty(s);
        }
        catch { return false; }
    }

    private static bool IsHklmAutostartSet()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(AutostartRegPath, writable: false);
            return key?.GetValue(AutostartValueName) is not null;
        }
        catch { return false; }
    }

    private async void OnAutostartToggled(object? sender, EventArgs e)
    {
        var wantEnabled = _autostartEnabled.Checked;
        _autostartEnabled.Enabled = false;
        try
        {
            if (wantEnabled)
            {
                using var key = Registry.CurrentUser.CreateSubKey(AutostartRegPath, writable: true);
                key?.SetValue(AutostartValueName, $"\"{Application.ExecutablePath}\"", RegistryValueKind.String);
                _statusLabel.Text = "Autostart enabled.";
            }
            else
            {
                try
                {
                    using var key = Registry.CurrentUser.CreateSubKey(AutostartRegPath, writable: true);
                    key?.DeleteValue(AutostartValueName, throwOnMissingValue: false);
                }
                catch { /* best-effort HKCU clear */ }

                if (IsHklmAutostartSet())
                {
                    var ok = await SetHklmAutostartElevatedAsync(enable: false);
                    if (!ok)
                    {
                        _statusLabel.Text = "Autostart change cancelled.";
                        return;
                    }
                }
                _statusLabel.Text = "Autostart disabled.";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not change autostart",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            LoadAutostartState();
        }
    }

    private async Task<bool> SetHklmAutostartElevatedAsync(bool enable)
    {
        var psi = new ProcessStartInfo("reg.exe")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        if (enable)
        {
            psi.ArgumentList.Add("add");
            psi.ArgumentList.Add($@"HKLM\{AutostartRegPath}");
            psi.ArgumentList.Add("/v");
            psi.ArgumentList.Add(AutostartValueName);
            psi.ArgumentList.Add("/t");
            psi.ArgumentList.Add("REG_SZ");
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add($"\"{Application.ExecutablePath}\"");
            psi.ArgumentList.Add("/f");
        }
        else
        {
            psi.ArgumentList.Add("delete");
            psi.ArgumentList.Add($@"HKLM\{AutostartRegPath}");
            psi.ArgumentList.Add("/v");
            psi.ArgumentList.Add(AutostartValueName);
            psi.ArgumentList.Add("/f");
        }

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — user clicked No on the UAC prompt.
            return false;
        }
    }

    private bool IsDirty()
    {
        if (_loaded is null) return false;
        if ((int)_failureThreshold.Value != _loaded.FailureThreshold) return true;
        if ((int)_slidingWindow.Value != _loaded.SlidingWindowMinutes) return true;
        if ((int)_blockDuration.Value != _loaded.BlockDurationMinutes) return true;
        if ((int)_historyRetention.Value != _loaded.HistoryRetentionDays) return true;
        var scope = _firewallScope.SelectedItem as string ?? "AllPorts";
        if (!string.Equals(scope, _loaded.FirewallScope, StringComparison.Ordinal)) return true;
        if (_evaluateNla.Checked != _loaded.EvaluateNlaFallback) return true;
        if (_geoEnabled.Checked != _loaded.GeoLookupEnabled) return true;
        if (!string.Equals(_geoToken.Text, _loaded.IpInfoToken ?? string.Empty, StringComparison.Ordinal)) return true;
        if ((int)_geoInterval.Value != _loaded.GeoRefreshIntervalDays) return true;
        if (_updateEnabled.Checked != _loaded.AutoUpdateEnabled) return true;
        if ((int)_updateInterval.Value != _loaded.AutoUpdateCheckIntervalHours) return true;
        return false;
    }

    private async void OnFormClosingPrompt(object? sender, FormClosingEventArgs e)
    {
        if (_suppressClosePrompt || !IsDirty()) return;

        var choice = MessageBox.Show(this,
            "You have unsaved changes. Apply them before closing?",
            "Unsaved changes",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);

        switch (choice)
        {
            case DialogResult.Yes:
                e.Cancel = true;
                await ApplyAsync();
                if (!IsDirty())
                {
                    _suppressClosePrompt = true;
                    Close();
                }
                break;
            case DialogResult.No:
                _suppressClosePrompt = true;
                break;
            case DialogResult.Cancel:
            default:
                e.Cancel = true;
                break;
        }
    }

    private static NumericUpDown NewSpinner(int min, int max) => new()
    {
        Minimum = min,
        Maximum = max,
        Anchor = AnchorStyles.Left,
        Width = 120,
    };

    private static void AddRow(TableLayoutPanel host, int row, string label, Control control)
    {
        host.Controls.Add(new Label
        {
            Text = label,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
        }, 0, row);
        host.Controls.Add(control, 1, row);
    }

    private async Task OnUpdateCheckNowAsync()
    {
        try
        {
            _updateCheckNow.Enabled = false;
            _updateStatus.Text = "Checking…";
            var status = await _client.UpdateCheckNowAsync();
            _updateStatus.Text = FormatUpdateStatus(status);
            ApplyUpdateAvailability(status);
        }
        catch (Exception ex)
        {
            _updateStatus.Text = $"Error: {ex.Message}";
        }
        finally
        {
            _updateCheckNow.Enabled = true;
        }
    }

    private async Task OnInstallUpdateAsync()
    {
        var version = _availableUpdateVersion;
        if (string.IsNullOrEmpty(version)) return;

        var confirm = MessageBox.Show(
            this,
            $"Install BlockRdpBruteForce {version} now?\n\n" +
            "The service will briefly restart, you'll see a Windows Installer progress " +
            "dialog, and the tray will reappear automatically when the upgrade is done.",
            "Install update",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);

        if (confirm != DialogResult.Yes) return;

        try
        {
            _updateInstall.Enabled = false;
            _updateCheckNow.Enabled = false;
            _updateStatus.Text = $"Installing {version}…";
            var result = await _client.UpdateApplyAsync(version);
            if (!result.Started)
            {
                MessageBox.Show(
                    this,
                    result.Message ?? "The service refused to start the update.",
                    "Could not install update",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                _updateStatus.Text = result.Message ?? "Update did not start.";
                _updateInstall.Enabled = true;
                _updateCheckNow.Enabled = true;
                return;
            }

            // MSI will replace our exe — exit so it can.
            await Task.Delay(1500);
            _suppressClosePrompt = true;
            Application.Exit();
        }
        catch (InvalidOperationException ex)
        {
            ShowAdminError("Could not install update", ex);
            _updateInstall.Enabled = true;
            _updateCheckNow.Enabled = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not install update",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            _updateStatus.Text = $"Error: {ex.Message}";
            _updateInstall.Enabled = true;
            _updateCheckNow.Enabled = true;
        }
    }

    private void ApplyUpdateAvailability(UpdateStatusPayload s)
    {
        if (s.UpdateAvailable && !string.IsNullOrEmpty(s.LatestVersion))
        {
            _availableUpdateVersion = s.LatestVersion;
            _updateInstall.Text = $"Install update {s.LatestVersion}…";
            _updateInstall.Visible = true;
        }
        else
        {
            _availableUpdateVersion = null;
            _updateInstall.Visible = false;
        }
    }

    private static string FormatUpdateStatus(UpdateStatusPayload s)
    {
        var current = string.IsNullOrEmpty(s.CurrentVersion) ? "?" : s.CurrentVersion;
        var lastChecked = s.LastCheckUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "never";

        if (!string.IsNullOrEmpty(s.LastCheckError))
            return $"Last check failed at {lastChecked}: {s.LastCheckError} (current {current})";

        if (s.UpdateAvailable && !string.IsNullOrEmpty(s.LatestVersion))
            return $"Update {s.LatestVersion} available (current {current}) — last checked {lastChecked}";

        var latest = string.IsNullOrEmpty(s.LatestVersion) ? current : s.LatestVersion;
        return $"You're on the latest version ({latest}) — last checked {lastChecked}";
    }

    private static Label NewLabel(string text) => new()
    {
        Text = text,
        TextAlign = ContentAlignment.MiddleLeft,
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
    };

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private async Task ReloadAsync()
    {
        try
        {
            _statusLabel.Text = "Loading…";
            var c = await _client.ConfigGetAsync();
            _loaded = c;

            _failureThreshold.Value = Clamp(c.FailureThreshold ?? 5, _failureThreshold);
            _slidingWindow.Value = Clamp(c.SlidingWindowMinutes ?? 10, _slidingWindow);
            _blockDuration.Value = Clamp(c.BlockDurationMinutes ?? 1440, _blockDuration);
            _historyRetention.Value = Clamp(c.HistoryRetentionDays ?? 90, _historyRetention);

            var scope = c.FirewallScope ?? "AllPorts";
            _firewallScope.SelectedIndex = Math.Max(0, _firewallScope.Items.IndexOf(scope));
            _evaluateNla.Checked = c.EvaluateNlaFallback ?? true;

            _whitelistBox.Items.Clear();
            foreach (var w in c.Whitelist ?? new List<string>())
                _whitelistBox.Items.Add(w);

            _geoEnabled.Checked = c.GeoLookupEnabled ?? false;
            _geoToken.Text = c.IpInfoToken ?? string.Empty;
            _geoInterval.Value = Clamp(c.GeoRefreshIntervalDays ?? 7, _geoInterval);

            _updateEnabled.Checked = c.AutoUpdateEnabled ?? true;
            _updateInterval.Value = Clamp(c.AutoUpdateCheckIntervalHours ?? 24, _updateInterval);

            LoadAutostartState();

            await UpdateGeoStatusAsync();
            await UpdateUpdateStatusAsync();

            _statusLabel.Text = "Loaded.";
        }
        catch (InvalidOperationException ex)
        {
            ShowAdminError("Could not load settings", ex);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Error: {ex.Message}";
        }
    }

    private async Task UpdateGeoStatusAsync()
    {
        try
        {
            var status = await _client.GeoStatusAsync();
            _geoStatus.Text = FormatGeoStatus(status);
        }
        catch (Exception ex)
        {
            _geoStatus.Text = $"Status unavailable: {ex.Message}";
        }
    }

    private async Task UpdateUpdateStatusAsync()
    {
        try
        {
            var status = await _client.UpdateStatusAsync();
            _updateStatus.Text = FormatUpdateStatus(status);
            ApplyUpdateAvailability(status);
        }
        catch (Exception ex)
        {
            _updateStatus.Text = $"Status unavailable: {ex.Message}";
        }
    }

    private static string FormatGeoStatus(GeoStatusPayload s)
    {
        if (!s.DbPresent)
        {
            if (!s.TokenConfigured) return "No token configured. Paste a token, click Apply, then Refresh now.";
            if (!string.IsNullOrEmpty(s.LastError)) return $"Not downloaded yet — last error: {s.LastError}";
            return "Database not downloaded yet. Click Refresh now.";
        }

        var mb = s.DbBytes / 1024.0 / 1024.0;
        var modText = s.DbModifiedUtc?.ToLocalTime().ToString("yyyy-MM-dd") ?? "?";
        var refreshedText = s.LastRefreshUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "never";
        var line = $"DB date: {modText} · {mb:0.0} MB · last refreshed: {refreshedText}";
        if (!string.IsNullOrEmpty(s.LastError))
            line += $" · last error: {s.LastError}";
        return line;
    }

    private async Task OnGeoRefreshAsync()
    {
        try
        {
            _geoRefresh.Enabled = false;
            _geoStatus.Text = "Refreshing…";
            var status = await _client.GeoRefreshAsync();
            _geoStatus.Text = FormatGeoStatus(status);
        }
        catch (InvalidOperationException ex)
        {
            ShowAdminError("Could not refresh geo DB", ex);
        }
        catch (Exception ex)
        {
            _geoStatus.Text = $"Error: {ex.Message}";
        }
        finally
        {
            _geoRefresh.Enabled = true;
        }
    }

    private static decimal Clamp(int value, NumericUpDown control)
    {
        decimal v = value;
        if (v < control.Minimum) return control.Minimum;
        if (v > control.Maximum) return control.Maximum;
        return v;
    }

    private async Task ApplyAsync()
    {
        if (_loaded is null) return;

        var payload = new ConfigPayload();
        var ft = (int)_failureThreshold.Value;
        var sw = (int)_slidingWindow.Value;
        var bd = (int)_blockDuration.Value;
        var hr = (int)_historyRetention.Value;
        var scope = _firewallScope.SelectedItem as string ?? "AllPorts";
        var nla = _evaluateNla.Checked;
        var geoOn = _geoEnabled.Checked;
        var geoToken = _geoToken.Text;
        var geoIntv = (int)_geoInterval.Value;

        if (ft != _loaded.FailureThreshold) payload.FailureThreshold = ft;
        if (sw != _loaded.SlidingWindowMinutes) payload.SlidingWindowMinutes = sw;
        if (bd != _loaded.BlockDurationMinutes) payload.BlockDurationMinutes = bd;
        if (hr != _loaded.HistoryRetentionDays) payload.HistoryRetentionDays = hr;
        if (!string.Equals(scope, _loaded.FirewallScope, StringComparison.Ordinal)) payload.FirewallScope = scope;
        if (nla != _loaded.EvaluateNlaFallback) payload.EvaluateNlaFallback = nla;
        if (geoOn != _loaded.GeoLookupEnabled) payload.GeoLookupEnabled = geoOn;
        if (!string.Equals(geoToken, _loaded.IpInfoToken ?? string.Empty, StringComparison.Ordinal))
            payload.IpInfoToken = geoToken;
        if (geoIntv != _loaded.GeoRefreshIntervalDays) payload.GeoRefreshIntervalDays = geoIntv;

        var autoUpd = _updateEnabled.Checked;
        var autoIntv = (int)_updateInterval.Value;
        if (autoUpd != _loaded.AutoUpdateEnabled) payload.AutoUpdateEnabled = autoUpd;
        if (autoIntv != _loaded.AutoUpdateCheckIntervalHours) payload.AutoUpdateCheckIntervalHours = autoIntv;

        if (payload.FailureThreshold is null
            && payload.SlidingWindowMinutes is null
            && payload.BlockDurationMinutes is null
            && payload.HistoryRetentionDays is null
            && payload.FirewallScope is null
            && payload.EvaluateNlaFallback is null
            && payload.GeoLookupEnabled is null
            && payload.IpInfoToken is null
            && payload.GeoRefreshIntervalDays is null
            && payload.AutoUpdateEnabled is null
            && payload.AutoUpdateCheckIntervalHours is null)
        {
            _statusLabel.Text = "No changes to apply.";
            return;
        }

        try
        {
            _applyButton.Enabled = false;
            var result = await _client.ConfigSetAsync(payload);
            _loaded = result.Effective;
            ShowResult(result);
            await UpdateGeoStatusAsync();
            await UpdateUpdateStatusAsync();
        }
        catch (PipeValidationException ex)
        {
            ShowValidationError(ex);
        }
        catch (InvalidOperationException ex)
        {
            ShowAdminError("Could not apply settings", ex);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _applyButton.Enabled = true;
        }
    }

    private void OnAddWhitelist()
    {
        using var dlg = new EntryPromptForm("Add to whitelist", "IP address or CIDR (e.g. 10.0.0.0/8):");
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var text = dlg.Value.Trim();
        if (text.Length == 0) return;
        if (!WhitelistEvaluator.TryParse(text, out _, out _, out _))
        {
            MessageBox.Show(this,
                $"'{text}' is not a valid IP address or CIDR block.",
                "Invalid entry",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        _ = SendWhitelistAsync(text, add: true);
    }

    private async Task OnRemoveWhitelistAsync()
    {
        if (_whitelistBox.SelectedItem is not string entry) return;
        var confirm = MessageBox.Show(this,
            $"Remove {entry} from the whitelist?",
            "Confirm remove",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        await SendWhitelistAsync(entry, add: false);
    }

    private async Task SendWhitelistAsync(string entry, bool add)
    {
        try
        {
            _addWhitelist.Enabled = false;
            _removeWhitelist.Enabled = false;
            var result = add
                ? await _client.WhitelistAddAsync(entry)
                : await _client.WhitelistRemoveAsync(entry);
            _loaded = result.Effective;
            _whitelistBox.Items.Clear();
            foreach (var w in result.Effective.Whitelist ?? new List<string>())
                _whitelistBox.Items.Add(w);
            ShowResult(result);
        }
        catch (PipeValidationException ex)
        {
            ShowValidationError(ex);
        }
        catch (InvalidOperationException ex)
        {
            ShowAdminError(add ? "Could not add to whitelist" : "Could not remove from whitelist", ex);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _addWhitelist.Enabled = true;
            _removeWhitelist.Enabled = true;
        }
    }

    private void ShowAdminError(string title, Exception ex)
    {
        MessageBox.Show(this,
            ex.Message + "\n\nThis action requires running the tray as Administrator.",
            title,
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
        _statusLabel.Text = $"Error: {ex.Message}";
    }

    private void ShowValidationError(PipeValidationException ex)
    {
        MessageBox.Show(this,
            ex.Message,
            "Cannot apply settings",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
        _statusLabel.Text = $"Rejected: {ex.Message}";
        _tabs.SelectedTab = _generalTab;
        _failureThreshold.Focus();
        _failureThreshold.Select(0, _failureThreshold.Text.Length);
    }

    private void ShowResult(ConfigSetResult result)
    {
        if (result.AppliedHot.Count > 0 && !result.RestartRequired)
            _statusLabel.Text = "Applied. Active immediately.";
        else if (result.AppliedHot.Count > 0 && result.RestartRequired)
            _statusLabel.Text = "Whitelist active immediately; other changes require service restart.";
        else
            _statusLabel.Text = "Saved. Restart the service for changes to take effect.";
    }

    private sealed class EntryPromptForm : Form
    {
        private readonly TextBox _input;

        public string Value => _input.Text;

        public EntryPromptForm(string title, string prompt)
        {
            Text = title;
            Width = 380;
            Height = 150;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;

            var label = new Label
            {
                Text = prompt,
                Dock = DockStyle.Top,
                Height = 24,
                Padding = new Padding(8, 8, 8, 0),
            };
            _input = new TextBox
            {
                Dock = DockStyle.Top,
                Margin = new Padding(8),
            };
            var ok = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Anchor = AnchorStyles.Right,
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Anchor = AnchorStyles.Right,
            };
            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 36,
                Padding = new Padding(8),
            };
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(ok);

            Controls.Add(_input);
            Controls.Add(label);
            Controls.Add(bottom);

            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}

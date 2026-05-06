using System.Diagnostics;
using System.Runtime.Versioning;
using BlockRdpBruteForce.Detection;
using BlockRdpBruteForce.Ipc;

namespace BlockRdpBruteForce.Tray.Forms;

[SupportedOSPlatform("windows")]
public sealed class SettingsForm : Form
{
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

    private ConfigPayload? _loaded;

    public SettingsForm(PipeClient client)
    {
        _client = client;

        Text = "BlockRdpBruteForce — Settings";
        Width = 600;
        Height = 740;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(540, 640);
        FormBorderStyle = FormBorderStyle.Sizable;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 8,
            Padding = new Padding(12),
            AutoSize = false,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 6; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

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

        AddRow(layout, 0, "Failure threshold (count):", _failureThreshold);
        AddRow(layout, 1, "Sliding window (minutes):", _slidingWindow);
        AddRow(layout, 2, "Block duration (minutes, 0 = permanent):", _blockDuration);
        AddRow(layout, 3, "Firewall scope:", _firewallScope);
        AddRow(layout, 4, string.Empty, _evaluateNla);
        AddRow(layout, 5, "History retention (days, 0 = keep forever):", _historyRetention);

        var whitelistGroup = new GroupBox
        {
            Text = "Whitelist (IP or CIDR — applied immediately)",
            Dock = DockStyle.Fill,
        };
        var whitelistLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(8),
        };
        whitelistLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        whitelistLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        whitelistLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        whitelistLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _whitelistBox = new ListBox { Dock = DockStyle.Fill };
        _addWhitelist = new Button { Text = "Add…", Dock = DockStyle.Fill, Height = 28 };
        _removeWhitelist = new Button { Text = "Remove", Dock = DockStyle.Fill, Height = 28 };
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

        whitelistLayout.Controls.Add(_whitelistBox, 0, 0);
        whitelistLayout.Controls.Add(sideButtons, 1, 0);
        whitelistLayout.SetRowSpan(_whitelistBox, 2);
        whitelistGroup.Controls.Add(whitelistLayout);

        layout.Controls.Add(whitelistGroup, 0, 6);
        layout.SetColumnSpan(whitelistGroup, 2);

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

        var geoGroup = BuildGeoGroup();
        layout.Controls.Add(geoGroup, 0, 7);
        layout.SetColumnSpan(geoGroup, 2);

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

        Controls.Add(layout);
        Controls.Add(bottom);

        Shown += async (_, _) => await ReloadAsync();
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

    private GroupBox BuildGeoGroup()
    {
        var group = new GroupBox
        {
            Text = "IP Geolocation (IPinfo Lite — CC BY-SA 4.0)",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };

        var inner = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 3,
            RowCount = 5,
            Padding = new Padding(8),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (var i = 0; i < 5; i++)
            inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Row 0: enable checkbox spans columns
        inner.Controls.Add(_geoEnabled, 0, 0);
        inner.SetColumnSpan(_geoEnabled, 3);

        // Row 1: token + link
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

        // Row 2: interval
        inner.Controls.Add(NewLabel("Refresh every (days):"), 0, 2);
        inner.Controls.Add(_geoInterval, 1, 2);

        // Row 3: refresh button + status
        inner.Controls.Add(_geoRefresh, 0, 3);
        inner.Controls.Add(_geoStatus, 1, 3);
        inner.SetColumnSpan(_geoStatus, 2);

        group.Controls.Add(inner);
        return group;
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

            await UpdateGeoStatusAsync();

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

        if (payload.FailureThreshold is null
            && payload.SlidingWindowMinutes is null
            && payload.BlockDurationMinutes is null
            && payload.HistoryRetentionDays is null
            && payload.FirewallScope is null
            && payload.EvaluateNlaFallback is null
            && payload.GeoLookupEnabled is null
            && payload.IpInfoToken is null
            && payload.GeoRefreshIntervalDays is null)
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

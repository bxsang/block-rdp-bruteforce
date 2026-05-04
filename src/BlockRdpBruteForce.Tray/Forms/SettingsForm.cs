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
    private readonly ComboBox _firewallScope;
    private readonly CheckBox _evaluateNla;
    private readonly ListBox _whitelistBox;
    private readonly Button _addWhitelist;
    private readonly Button _removeWhitelist;
    private readonly Button _applyButton;
    private readonly Button _reloadButton;
    private readonly Button _closeButton;
    private readonly Label _statusLabel;

    private ConfigPayload? _loaded;

    public SettingsForm(PipeClient client)
    {
        _client = client;

        Text = "BlockRdpBruteForce — Settings";
        Width = 580;
        Height = 540;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(520, 480);
        FormBorderStyle = FormBorderStyle.Sizable;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 7,
            Padding = new Padding(12),
            AutoSize = false,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 6; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _failureThreshold = NewSpinner(1, 1000);
        _slidingWindow = NewSpinner(1, 1440);
        _blockDuration = NewSpinner(0, 525_600);

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

            var scope = c.FirewallScope ?? "AllPorts";
            _firewallScope.SelectedIndex = Math.Max(0, _firewallScope.Items.IndexOf(scope));
            _evaluateNla.Checked = c.EvaluateNlaFallback ?? true;

            _whitelistBox.Items.Clear();
            foreach (var w in c.Whitelist ?? new List<string>())
                _whitelistBox.Items.Add(w);

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

    private static decimal Clamp(int value, NumericUpDown control)
    {
        if (value < control.Minimum) return control.Minimum;
        if (value > control.Maximum) return control.Maximum;
        return value;
    }

    private async Task ApplyAsync()
    {
        if (_loaded is null) return;

        var payload = new ConfigPayload();
        var ft = (int)_failureThreshold.Value;
        var sw = (int)_slidingWindow.Value;
        var bd = (int)_blockDuration.Value;
        var scope = _firewallScope.SelectedItem as string ?? "AllPorts";
        var nla = _evaluateNla.Checked;

        if (ft != _loaded.FailureThreshold) payload.FailureThreshold = ft;
        if (sw != _loaded.SlidingWindowMinutes) payload.SlidingWindowMinutes = sw;
        if (bd != _loaded.BlockDurationMinutes) payload.BlockDurationMinutes = bd;
        if (!string.Equals(scope, _loaded.FirewallScope, StringComparison.Ordinal)) payload.FirewallScope = scope;
        if (nla != _loaded.EvaluateNlaFallback) payload.EvaluateNlaFallback = nla;

        if (payload.FailureThreshold is null
            && payload.SlidingWindowMinutes is null
            && payload.BlockDurationMinutes is null
            && payload.FirewallScope is null
            && payload.EvaluateNlaFallback is null)
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

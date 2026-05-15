using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.ServiceProcess;
using BlockRdpBruteForce.Detection;
using BlockRdpBruteForce.Ipc;
using Microsoft.Win32;

namespace BlockRdpBruteForce.Tray.Forms;

[SupportedOSPlatform("windows")]
public sealed class SettingsForm : Form
{
    private const string AutostartRegPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AutostartValueName = "BlockRdpBruteForceTray";
    private const string ServiceName = "BlockRdpBruteForce";
    private static readonly TimeSpan ServiceWaitTimeout = TimeSpan.FromSeconds(30);

    private readonly PipeClient _client;

    private readonly NumericUpDown _failureThreshold;
    private readonly NumericUpDown _slidingWindow;
    private readonly TextBox _blockDurations;
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

    private readonly RadioButton _autostartOff;
    private readonly RadioButton _autostartHkcu;
    private readonly RadioButton _autostartHklm;
    private readonly Label _autostartNote;
    private bool _autostartReloading;

    private readonly Label _serviceStatusLabel;
    private readonly Button _serviceStartButton;
    private readonly Button _serviceStopButton;
    private readonly Button _serviceRestartButton;
    private readonly Label _serviceNote;
    private readonly ToolTip _serviceTooltip;
    private readonly System.Windows.Forms.Timer _serviceStatusTimer;
    private readonly bool _isAdmin;
    private bool _serviceActionInFlight;

    private readonly TabControl _tabs;
    private readonly TabPage _generalTab;
    private readonly TabPage _whitelistTab;
    private readonly TabPage _geoTab;
    private readonly TabPage _updatesTab;
    private readonly TabPage _interfaceTab;
    private readonly TabPage _serviceTab;

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
        _blockDurations = new TextBox
        {
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            PlaceholderText = "1440  or  60, 240, 1440, 0  (last 0 = permanent)",
        };
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

        _autostartOff = new RadioButton
        {
            Text = "Off — don't start at sign-in",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
        };
        _autostartHkcu = new RadioButton
        {
            Text = "Start when I sign in (this user only)",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
        };
        _autostartHklm = new RadioButton
        {
            Text = "Start when anyone signs in (all users — requires admin)",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
        };
        AttachAutostartHandlers(true);
        _autostartNote = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Height = 52,
            ForeColor = SystemColors.GrayText,
            Padding = new Padding(0, 8, 0, 0),
        };

        _isAdmin = IsRunningAsAdmin();
        _serviceTooltip = new ToolTip();
        _serviceStatusLabel = new Label
        {
            Text = "Status: checking…",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Height = 28,
        };
        _serviceStartButton = new Button { Text = "Start", AutoSize = true, Anchor = AnchorStyles.Left };
        _serviceStopButton = new Button { Text = "Stop", AutoSize = true, Anchor = AnchorStyles.Left };
        _serviceRestartButton = new Button { Text = "Restart", AutoSize = true, Anchor = AnchorStyles.Left };
        _serviceStartButton.Click += async (_, _) => await OnServiceStartAsync();
        _serviceStopButton.Click += async (_, _) => await OnServiceStopAsync();
        _serviceRestartButton.Click += async (_, _) => await OnServiceRestartAsync();
        _serviceNote = new Label
        {
            Text = "Stopping the service halts active blocking until it is started again. "
                 + "Existing firewall rules remain in place.",
            AutoSize = false,
            Dock = DockStyle.Top,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = SystemColors.GrayText,
            Height = 36,
        };
        _serviceStatusTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _serviceStatusTimer.Tick += async (_, _) => await RefreshServiceStatusAsync();

        _generalTab = BuildGeneralTab();
        _whitelistTab = BuildWhitelistTab();
        _geoTab = BuildGeoTab();
        _updatesTab = BuildUpdatesTab();
        _interfaceTab = BuildInterfaceTab();
        _serviceTab = BuildServiceTab();

        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs.TabPages.Add(_generalTab);
        _tabs.TabPages.Add(_whitelistTab);
        _tabs.TabPages.Add(_geoTab);
        _tabs.TabPages.Add(_updatesTab);
        _tabs.TabPages.Add(_interfaceTab);
        _tabs.TabPages.Add(_serviceTab);

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

        Shown += async (_, _) =>
        {
            await ReloadAsync();
            await RefreshServiceStatusAsync();
            _serviceStatusTimer.Start();
        };
        FormClosed += (_, _) => _serviceStatusTimer.Stop();
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
        AddRow(layout, 2, "Block duration (minutes; comma list for repeat offenders):", _blockDurations);
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

        var autostartGroup = new GroupBox
        {
            Text = "Tray autostart",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 8, 12, 8),
        };
        var autostartStack = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
        };
        autostartStack.Controls.Add(_autostartOff);
        autostartStack.Controls.Add(_autostartHkcu);
        autostartStack.Controls.Add(_autostartHklm);
        autostartStack.Controls.Add(_autostartNote);
        autostartGroup.Controls.Add(autostartStack);

        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
        };
        stack.Controls.Add(autostartGroup);
        page.Controls.Add(stack);
        return page;
    }

    private TabPage BuildServiceTab()
    {
        var page = new TabPage("Service") { Padding = new Padding(12), UseVisualStyleBackColor = true };

        var statusRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 1,
            AutoSize = true,
            Height = 32,
        };
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusRow.Controls.Add(_serviceStatusLabel, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 8),
            Margin = Padding.Empty,
        };
        buttons.Controls.Add(_serviceStartButton);
        buttons.Controls.Add(_serviceStopButton);
        buttons.Controls.Add(_serviceRestartButton);

        if (!_isAdmin)
        {
            const string tip = "Requires running the tray as Administrator. "
                + "Use the tray's \"Restart as Administrator\" menu item.";
            _serviceStartButton.Enabled = false;
            _serviceStopButton.Enabled = false;
            _serviceRestartButton.Enabled = false;
            _serviceTooltip.SetToolTip(_serviceStartButton, tip);
            _serviceTooltip.SetToolTip(_serviceStopButton, tip);
            _serviceTooltip.SetToolTip(_serviceRestartButton, tip);
        }

        page.Controls.Add(_serviceNote);
        page.Controls.Add(buttons);
        page.Controls.Add(statusRow);
        return page;
    }

    private static bool IsRunningAsAdmin()
    {
        var sid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var sidBytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(sidBytes, 0);
        return CheckTokenMembership(IntPtr.Zero, sidBytes, out var isMember) && isMember;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CheckTokenMembership(
        IntPtr tokenHandle, byte[] sidToCheck, out bool isMember);

    private static ServiceControllerStatus? TryGetServiceStatus(out bool installed)
    {
        installed = false;
        try
        {
            using var sc = new ServiceController(ServiceName);
            var status = sc.Status;
            installed = true;
            return status;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private async Task RefreshServiceStatusAsync()
    {
        var snapshot = await Task.Run(() =>
        {
            var status = TryGetServiceStatus(out var installed);
            return (Installed: installed, Status: status);
        });

        if (IsDisposed) return;

        if (!snapshot.Installed)
        {
            _serviceStatusLabel.Text = "Status: service not installed";
            if (_isAdmin && !_serviceActionInFlight)
            {
                _serviceStartButton.Enabled = false;
                _serviceStopButton.Enabled = false;
                _serviceRestartButton.Enabled = false;
            }
            return;
        }

        _serviceStatusLabel.Text = $"Status: {DescribeStatus(snapshot.Status!.Value)}";

        if (_isAdmin && !_serviceActionInFlight)
            ApplyServiceButtonsForStatus(snapshot.Status.Value);
    }

    private void ApplyServiceButtonsForStatus(ServiceControllerStatus status)
    {
        _serviceStartButton.Enabled = status == ServiceControllerStatus.Stopped;
        _serviceStopButton.Enabled =
            status == ServiceControllerStatus.Running
            || status == ServiceControllerStatus.Paused;
        _serviceRestartButton.Enabled =
            status == ServiceControllerStatus.Running
            || status == ServiceControllerStatus.Paused;
    }

    private static string DescribeStatus(ServiceControllerStatus s) => s switch
    {
        ServiceControllerStatus.Running => "Running",
        ServiceControllerStatus.Stopped => "Stopped",
        ServiceControllerStatus.Paused => "Paused",
        ServiceControllerStatus.StartPending => "Starting…",
        ServiceControllerStatus.StopPending => "Stopping…",
        ServiceControllerStatus.PausePending => "Pausing…",
        ServiceControllerStatus.ContinuePending => "Resuming…",
        _ => s.ToString(),
    };

    private async Task OnServiceStartAsync()
    {
        await RunServiceActionAsync("Starting…", "Service started.", sc =>
        {
            if (sc.Status == ServiceControllerStatus.Running) return;
            if (sc.Status != ServiceControllerStatus.StartPending) sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, ServiceWaitTimeout);
        });
    }

    private async Task OnServiceStopAsync()
    {
        var confirm = MessageBox.Show(this,
            "Stop the BlockRdpBruteForce service?\n\n"
          + "Active blocking will stop until the service is started again. "
          + "Existing firewall rules remain in place.",
            "Stop service",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;

        await RunServiceActionAsync("Stopping…", "Service stopped.", sc =>
        {
            if (sc.Status == ServiceControllerStatus.Stopped) return;
            if (sc.CanStop) sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, ServiceWaitTimeout);
        });
    }

    private async Task OnServiceRestartAsync()
    {
        var confirm = MessageBox.Show(this,
            "Restart the BlockRdpBruteForce service?\n\n"
          + "Blocking will pause for a few seconds while the service restarts.",
            "Restart service",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);
        if (confirm != DialogResult.Yes) return;

        await RunServiceActionAsync("Restarting…", "Service restarted.", sc =>
        {
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                if (sc.CanStop) sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, ServiceWaitTimeout);
                sc.Refresh();
            }
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, ServiceWaitTimeout);
        });
    }

    private async Task RunServiceActionAsync(
        string pendingText, string successText, Action<ServiceController> action)
    {
        _serviceActionInFlight = true;
        _serviceStartButton.Enabled = false;
        _serviceStopButton.Enabled = false;
        _serviceRestartButton.Enabled = false;
        _serviceStatusLabel.Text = $"Status: {pendingText}";

        try
        {
            await Task.Run(() =>
            {
                using var sc = new ServiceController(ServiceName);
                action(sc);
            });
            _statusLabel.Text = successText;
        }
        catch (System.ServiceProcess.TimeoutException ex)
        {
            MessageBox.Show(this,
                $"The service did not reach the expected state in {ServiceWaitTimeout.TotalSeconds:0}s: {ex.Message}",
                "Service action timed out",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            _statusLabel.Text = $"Error: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this,
                ex.Message + "\n\nThe service may not be installed, or you lack permission to control it.",
                "Service action failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            _statusLabel.Text = $"Error: {ex.Message}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Service action failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            _statusLabel.Text = $"Error: {ex.Message}";
        }
        finally
        {
            _serviceActionInFlight = false;
            await RefreshServiceStatusAsync();
        }
    }

    private enum AutostartMode { Off, CurrentUser, AllUsers }

    private static AutostartMode CurrentAutostartMode()
    {
        if (IsHklmAutostartSet()) return AutostartMode.AllUsers;
        if (IsHkcuAutostartSet()) return AutostartMode.CurrentUser;
        return AutostartMode.Off;
    }

    private void AttachAutostartHandlers(bool attach)
    {
        if (attach)
        {
            _autostartOff.CheckedChanged += OnAutostartChanged;
            _autostartHkcu.CheckedChanged += OnAutostartChanged;
            _autostartHklm.CheckedChanged += OnAutostartChanged;
        }
        else
        {
            _autostartOff.CheckedChanged -= OnAutostartChanged;
            _autostartHkcu.CheckedChanged -= OnAutostartChanged;
            _autostartHklm.CheckedChanged -= OnAutostartChanged;
        }
    }

    private void SetAutostartEnabled(bool enabled)
    {
        _autostartOff.Enabled = enabled;
        _autostartHkcu.Enabled = enabled;
        _autostartHklm.Enabled = enabled;
    }

    private void LoadAutostartState()
    {
        _autostartReloading = true;
        try
        {
            var mode = CurrentAutostartMode();
            _autostartOff.Checked = mode == AutostartMode.Off;
            _autostartHkcu.Checked = mode == AutostartMode.CurrentUser;
            _autostartHklm.Checked = mode == AutostartMode.AllUsers;
            SetAutostartEnabled(true);

            _autostartNote.Text = mode switch
            {
                AutostartMode.AllUsers =>
                    "Tray launches for every user at sign-in (HKLM Run). " +
                    "Changing this clears the machine-wide entry and prompts for Administrator approval.",
                AutostartMode.CurrentUser =>
                    "Tray launches for this user only (HKCU Run, no admin needed). " +
                    "Switching to \"all users\" prompts for Administrator approval.",
                _ =>
                    "Tray will not auto-launch. " +
                    "\"This user\" is silent; \"all users\" prompts for Administrator approval.",
            };
        }
        finally
        {
            _autostartReloading = false;
        }
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

    private async void OnAutostartChanged(object? sender, EventArgs e)
    {
        if (_autostartReloading) return;
        if (sender is not RadioButton rb || !rb.Checked) return;

        var target =
            rb == _autostartHklm ? AutostartMode.AllUsers :
            rb == _autostartHkcu ? AutostartMode.CurrentUser :
            AutostartMode.Off;
        var current = CurrentAutostartMode();
        if (target == current) return;

        SetAutostartEnabled(false);
        try
        {
            // UAC-prompting HKLM change first so cancellation leaves state untouched.
            var hklmShouldBeSet = target == AutostartMode.AllUsers;
            if (IsHklmAutostartSet() != hklmShouldBeSet)
            {
                var ok = await SetHklmAutostartElevatedAsync(enable: hklmShouldBeSet);
                if (!ok)
                {
                    _statusLabel.Text = "Autostart change cancelled.";
                    return;
                }
            }

            if (target == AutostartMode.CurrentUser)
            {
                using var key = Registry.CurrentUser.CreateSubKey(AutostartRegPath, writable: true);
                key?.SetValue(AutostartValueName, $"\"{Application.ExecutablePath}\"", RegistryValueKind.String);
            }
            else
            {
                try
                {
                    using var key = Registry.CurrentUser.CreateSubKey(AutostartRegPath, writable: true);
                    key?.DeleteValue(AutostartValueName, throwOnMissingValue: false);
                }
                catch { /* best-effort HKCU clear */ }
            }

            _statusLabel.Text = target switch
            {
                AutostartMode.AllUsers => "Autostart enabled for all users.",
                AutostartMode.CurrentUser => "Autostart enabled for this user.",
                _ => "Autostart disabled.",
            };
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
        if (TryParseLadder(_blockDurations.Text, out var durations, out _) &&
            !LaddersEqual(durations, _loaded.BlockDurationMinutes ?? new List<int>())) return true;
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
            "You'll see a User Account Control prompt — click Yes to allow the updater. " +
            "A progress window will then download the installer and run it; the service " +
            "and tray will restart automatically when the upgrade is done.",
            "Install update",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);

        if (confirm != DialogResult.Yes) return;

        try
        {
            _updateInstall.Enabled = false;
            _updateCheckNow.Enabled = false;
            _updateStatus.Text = $"Preparing {version}…";
            var result = await _client.UpdateApplyAsync(version);
            if (!result.Started || string.IsNullOrEmpty(result.UpdaterPath))
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

            // The service staged the updater binary; we launch it here with the
            // "runas" verb so Windows produces a UAC prompt inside this user's
            // session. Launching elevated from the service across sessions
            // produces STATUS_DLL_INIT_FAILED on modern Windows.
            _updateStatus.Text = $"Installing {version}…";
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = result.UpdaterPath,
                    Arguments = result.UpdaterArgs ?? string.Empty,
                    UseShellExecute = true,
                    Verb = "runas",
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception wex) when (wex.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED — user declined the UAC prompt.
                _updateStatus.Text = "Update cancelled by user.";
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
            _blockDurations.Text = FormatLadder(c.BlockDurationMinutes ?? new List<int> { 1440 });
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

    private static string FormatLadder(IReadOnlyList<int> ladder)
    {
        if (ladder.Count == 0) return string.Empty;
        return string.Join(", ", ladder);
    }

    private static bool TryParseLadder(string text, out List<int> ladder, out string error)
    {
        ladder = new List<int>();
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return true;

        var parts = text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var trimmed = parts[i].Trim();
            if (!int.TryParse(trimmed, System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out var n))
            {
                error = $"entry #{i + 1} '{trimmed}' is not a whole number";
                ladder.Clear();
                return false;
            }
            if (n < 0)
            {
                error = $"entry #{i + 1} ({n}) is negative; must be >= 0";
                ladder.Clear();
                return false;
            }
            ladder.Add(n);
        }

        for (var i = 0; i < ladder.Count - 1; i++)
        {
            if (ladder[i] == 0)
            {
                error = $"entry #{i + 1} is 0 (permanent); only the last entry may be 0";
                ladder.Clear();
                return false;
            }
        }

        return true;
    }

    private static bool LaddersEqual(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
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
        var hr = (int)_historyRetention.Value;
        var scope = _firewallScope.SelectedItem as string ?? "AllPorts";
        var nla = _evaluateNla.Checked;
        var geoOn = _geoEnabled.Checked;
        var geoToken = _geoToken.Text;
        var geoIntv = (int)_geoInterval.Value;

        if (!TryParseLadder(_blockDurations.Text, out var durations, out var durationsError))
        {
            MessageBox.Show(this,
                $"Block duration is invalid: {durationsError}",
                "Cannot apply settings",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            _statusLabel.Text = $"Rejected: {durationsError}";
            _tabs.SelectedTab = _generalTab;
            _blockDurations.Focus();
            return;
        }
        if (durations.Count == 0)
        {
            MessageBox.Show(this,
                "Block duration must contain at least one value (e.g. 1440 for 24 hours).",
                "Cannot apply settings",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            _tabs.SelectedTab = _generalTab;
            _blockDurations.Focus();
            return;
        }

        if (ft != _loaded.FailureThreshold) payload.FailureThreshold = ft;
        if (sw != _loaded.SlidingWindowMinutes) payload.SlidingWindowMinutes = sw;
        if (!LaddersEqual(durations, _loaded.BlockDurationMinutes ?? new List<int>()))
            payload.BlockDurationMinutes = durations;
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

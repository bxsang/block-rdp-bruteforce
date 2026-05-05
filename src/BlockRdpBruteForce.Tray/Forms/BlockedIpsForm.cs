using System.Net;
using System.Runtime.Versioning;
using BlockRdpBruteForce.Ipc;

namespace BlockRdpBruteForce.Tray.Forms;

[SupportedOSPlatform("windows")]
public sealed class BlockedIpsForm : Form
{
    private readonly PipeClient _client;
    private readonly DataGridView _grid;
    private readonly Button _refreshButton;
    private readonly Button _unblockButton;
    private readonly CheckBox _showHistoryCheckbox;
    private readonly Label _statusLabel;

    public BlockedIpsForm(PipeClient client)
    {
        _client = client;

        Text = "BlockRdpBruteForce — Blocked IPs";
        Width = 760;
        Height = 460;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(540, 320);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = SystemColors.Window,
        };
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Ip", HeaderText = "IP", SortMode = DataGridViewColumnSortMode.Automatic });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Count", HeaderText = "Times blocked", SortMode = DataGridViewColumnSortMode.Automatic, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "FirstSeen", HeaderText = "First seen (UTC)", SortMode = DataGridViewColumnSortMode.Automatic });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "LastSeen", HeaderText = "Last seen (UTC)", SortMode = DataGridViewColumnSortMode.Automatic });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "BlockedUntil", HeaderText = "Expires (UTC)", SortMode = DataGridViewColumnSortMode.Automatic });

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            ColumnCount = 4,
            Padding = new Padding(8, 6, 8, 6),
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _refreshButton = new Button { Text = "Refresh", AutoSize = true };
        _refreshButton.Click += async (_, _) => await ReloadAsync();

        _showHistoryCheckbox = new CheckBox
        {
            Text = "Show history",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(8, 0, 0, 0),
        };
        _showHistoryCheckbox.CheckedChanged += async (_, _) => await ReloadAsync();

        _unblockButton = new Button { Text = "Unblock selected", AutoSize = true };
        _unblockButton.Click += async (_, _) => await UnblockSelectedAsync();

        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        };

        bottom.Controls.Add(_refreshButton, 0, 0);
        bottom.Controls.Add(_showHistoryCheckbox, 1, 0);
        bottom.Controls.Add(_statusLabel, 2, 0);
        bottom.Controls.Add(_unblockButton, 3, 0);

        Controls.Add(_grid);
        Controls.Add(bottom);

        Shown += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            _statusLabel.Text = "Loading...";
            var entries = await _client.ListAsync();
            var nowUtc = DateTime.UtcNow;
            var showHistory = _showHistoryCheckbox.Checked;

            var active = 0;
            var historical = 0;
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (var e in entries.OrderBy(x => x.Ip, StringComparer.Ordinal))
            {
                var isActive = !e.BlockedUntilUtc.HasValue || e.BlockedUntilUtc.Value > nowUtc;
                if (isActive) active++;
                else historical++;

                if (!isActive && !showHistory) continue;

                var rowIndex = _grid.Rows.Add(
                    e.Ip,
                    e.Count.ToString(),
                    e.FirstSeenUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                    e.LastSeenUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                    e.BlockedUntilUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? "permanent");

                if (!isActive)
                    _grid.Rows[rowIndex].DefaultCellStyle.ForeColor = SystemColors.GrayText;
            }
            _grid.ResumeLayout();
            _statusLabel.Text = historical > 0
                ? $"{active} blocked, {historical} in history"
                : $"{active} blocked";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Error: {ex.Message}";
        }
    }

    private async Task UnblockSelectedAsync()
    {
        if (_grid.SelectedRows.Count == 0)
        {
            _statusLabel.Text = "Select a row first.";
            return;
        }
        var ipText = _grid.SelectedRows[0].Cells["Ip"].Value?.ToString();
        if (string.IsNullOrWhiteSpace(ipText) || !IPAddress.TryParse(ipText, out var ip))
        {
            _statusLabel.Text = "Selected row has no valid IP.";
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"Unblock {ip}?",
            "Confirm unblock",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        try
        {
            _unblockButton.Enabled = false;
            var result = await _client.UnblockAsync(ip);
            _statusLabel.Text = result.WasBlocked
                ? $"Unblocked {result.Ip}"
                : $"{result.Ip} was not blocked";
            await ReloadAsync();
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show(this, ex.Message, "Permission denied", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this,
                ex.Message + "\n\nManual unblock requires running this app as Administrator.",
                "Could not unblock",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _unblockButton.Enabled = true;
        }
    }
}

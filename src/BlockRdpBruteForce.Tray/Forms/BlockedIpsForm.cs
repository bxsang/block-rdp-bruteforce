using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
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
        Width = 1040;
        Height = 460;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 320);

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
        _grid.Columns.Add(new DataGridViewImageColumn
        {
            Name = "Flag",
            HeaderText = "",
            Width = 36,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            Resizable = DataGridViewTriState.False,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            ImageLayout = DataGridViewImageCellLayout.Zoom,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                // Image cells default to "no value" rendering an error glyph
                // when the cell is empty; clear it so unknown countries show
                // a blank cell instead.
                NullValue = null,
            },
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Country", HeaderText = "Country", SortMode = DataGridViewColumnSortMode.Automatic, FillWeight = 50 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Asn", HeaderText = "ASN", SortMode = DataGridViewColumnSortMode.Automatic, FillWeight = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "AsName", HeaderText = "Org", SortMode = DataGridViewColumnSortMode.Automatic, FillWeight = 130 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Count", HeaderText = "Times blocked", SortMode = DataGridViewColumnSortMode.Automatic, FillWeight = 60, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "FirstSeen", HeaderText = "First seen", SortMode = DataGridViewColumnSortMode.Automatic, FillWeight = 110 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "LastSeen", HeaderText = "Last seen", SortMode = DataGridViewColumnSortMode.Automatic, FillWeight = 110 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "BlockedUntil", HeaderText = "Expires", SortMode = DataGridViewColumnSortMode.Automatic, FillWeight = 110 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Remaining", HeaderText = "Remaining", SortMode = DataGridViewColumnSortMode.Automatic, FillWeight = 80, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } });

        _grid.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            var columnName = _grid.Columns[e.ColumnIndex].Name;
            if (columnName != "Flag" && columnName != "Country") return;
            var code = _grid.Rows[e.RowIndex].Cells["Country"].Value?.ToString();
            if (string.IsNullOrEmpty(code)) return;
            e.ToolTipText = CountryNameLookup.Get(code) ?? code;
        };

        var contextMenu = new ContextMenuStrip();
        var copyIpItem = new ToolStripMenuItem("Copy IP", null, (_, _) => CopySelectedIp())
        {
            ShortcutKeyDisplayString = "Ctrl+C",
        };
        contextMenu.Items.Add(copyIpItem);
        _grid.ContextMenuStrip = contextMenu;
        _grid.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
            {
                _grid.ClearSelection();
                _grid.Rows[e.RowIndex].Selected = true;
            }
        };
        _grid.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.C)
            {
                CopySelectedIp();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };

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

            var sortedColumn = _grid.SortedColumn;
            var sortOrder = _grid.SortOrder;

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

                // The Flag cell accepts null (NullValue = null on the column),
                // so suppress the nullable-element warning from params object[].
                var rowIndex = _grid.Rows.Add(
                    e.Ip,
                    FlagImageProvider.Get(e.CountryCode)!,
                    e.CountryCode ?? string.Empty,
                    e.Asn ?? string.Empty,
                    e.AsName ?? string.Empty,
                    e.Count.ToString(),
                    e.FirstSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    e.LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    e.BlockedUntilUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "permanent",
                    FormatRemaining(e.BlockedUntilUtc, nowUtc, isActive));

                if (!isActive)
                    _grid.Rows[rowIndex].DefaultCellStyle.ForeColor = SystemColors.GrayText;
            }
            _grid.ResumeLayout();

            if (sortedColumn != null && sortOrder != SortOrder.None)
            {
                _grid.Sort(sortedColumn, sortOrder == SortOrder.Ascending
                    ? ListSortDirection.Ascending
                    : ListSortDirection.Descending);
            }

            _statusLabel.Text = historical > 0
                ? $"{active} blocked, {historical} in history"
                : $"{active} blocked";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Error: {ex.Message}";
        }
    }

    private static string FormatRemaining(DateTime? blockedUntilUtc, DateTime nowUtc, bool isActive)
    {
        if (!isActive) return string.Empty;
        if (!blockedUntilUtc.HasValue) return "permanent";

        var remaining = blockedUntilUtc.Value - nowUtc;
        if (remaining <= TimeSpan.Zero) return string.Empty;

        if (remaining.TotalDays >= 1)
        {
            var days = (int)remaining.TotalDays;
            var hours = remaining.Hours;
            return hours > 0 ? $"{days}d {hours}h" : $"{days}d";
        }
        if (remaining.TotalHours >= 1)
        {
            var hours = (int)remaining.TotalHours;
            var minutes = remaining.Minutes;
            return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        }
        if (remaining.TotalMinutes >= 1)
        {
            return $"{(int)remaining.TotalMinutes}m";
        }
        return $"{(int)remaining.TotalSeconds}s";
    }

    private void CopySelectedIp()
    {
        if (_grid.SelectedRows.Count == 0) return;
        var ipText = _grid.SelectedRows[0].Cells["Ip"].Value?.ToString();
        if (string.IsNullOrWhiteSpace(ipText)) return;

        try
        {
            Clipboard.SetText(ipText);
            _statusLabel.Text = $"Copied {ipText}";
        }
        catch (ExternalException)
        {
            _statusLabel.Text = "Clipboard unavailable, try again.";
        }
    }

    private static class CountryNameLookup
    {
        private static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

        public static string? Get(string countryCode)
        {
            if (countryCode.Length != 2) return null;
            return Cache.GetOrAdd(countryCode, code =>
            {
                try
                {
                    return new RegionInfo(code).EnglishName;
                }
                catch (ArgumentException)
                {
                    return null;
                }
            });
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

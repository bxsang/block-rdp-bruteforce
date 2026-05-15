using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace BlockRdpBruteForce.Tray.Forms;

[SupportedOSPlatform("windows")]
public sealed class LogViewerForm : Form
{
    private const int MaxEntries = 5000;
    private const long InitialTailBytes = 2L * 1024 * 1024;
    private const int TailIntervalMs = 1000;

    // Matches the Serilog file outputTemplate in Program.cs:
    // [{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] {SourceContext}: {Message}
    private static readonly Regex EntryPrefix = new(
        @"^\[(?<ts>[\d\-]+ [\d:.]+ [+\-][\d:]+)\] \[(?<lvl>\w{3})\] ",
        RegexOptions.Compiled);

    private readonly string _logFolder;
    private readonly ComboBox _dayPicker;
    private readonly CheckBox _tailCheckbox;
    private readonly RichTextBox _content;
    private readonly CheckBox _showInfo;
    private readonly CheckBox _showWarn;
    private readonly CheckBox _showError;
    private readonly CheckBox _showDebug;
    private readonly TextBox _findBox;
    private readonly Button _copyButton;
    private readonly Button _closeButton;
    private readonly Label _statusLabel;
    private readonly System.Windows.Forms.Timer _timer;

    private readonly List<LogEntry> _entries = new();
    private readonly byte[] _readBuffer = new byte[64 * 1024];
    private FileStream? _stream;
    private long _lastPos;
    private string _carryLine = string.Empty;
    private string? _currentFile;

    public LogViewerForm(string logFolder)
    {
        _logFolder = logFolder;

        Text = "BlockRdpBruteForce — Log viewer";
        Width = 1100;
        Height = 600;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 360);

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            ColumnCount = 4,
            Padding = new Padding(8, 6, 8, 4),
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        top.Controls.Add(
            new Label
            {
                Text = "Day:",
                AutoSize = true,
                Margin = new Padding(0, 6, 4, 0),
            }, 0, 0);

        _dayPicker = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Width = 230,
        };
        _dayPicker.SelectedIndexChanged += (_, _) => OnDayChanged();
        top.Controls.Add(_dayPicker, 1, 0);

        _tailCheckbox = new CheckBox
        {
            Text = "Live tail",
            AutoSize = true,
            Margin = new Padding(12, 6, 0, 0),
            Checked = true,
        };
        _tailCheckbox.CheckedChanged += (_, _) => RescheduleTail();
        top.Controls.Add(_tailCheckbox, 2, 0);

        _content = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Both,
            Font = new Font(FontFamily.GenericMonospace, 9f),
            BackColor = SystemColors.Window,
            DetectUrls = false,
            HideSelection = false,
        };

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            ColumnCount = 8,
            Padding = new Padding(8, 6, 8, 6),
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _showInfo = new CheckBox { Text = "INF", Checked = true, AutoSize = true, Margin = new Padding(0, 6, 6, 0) };
        _showWarn = new CheckBox { Text = "WRN", Checked = true, AutoSize = true, Margin = new Padding(0, 6, 6, 0) };
        _showError = new CheckBox { Text = "ERR", Checked = true, AutoSize = true, Margin = new Padding(0, 6, 6, 0) };
        _showDebug = new CheckBox { Text = "DBG/VRB", Checked = false, AutoSize = true, Margin = new Padding(0, 6, 6, 0) };
        foreach (var cb in new[] { _showInfo, _showWarn, _showError, _showDebug })
            cb.CheckedChanged += (_, _) => Render();

        var findLabel = new Label { Text = "Find:", AutoSize = true, Margin = new Padding(8, 8, 4, 0) };
        _findBox = new TextBox
        {
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Width = 195,
            Margin = new Padding(0, 4, 0, 0),
        };
        _findBox.TextChanged += (_, _) => Render();

        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(8, 0, 0, 0),
        };

        _copyButton = new Button { Text = "Copy", AutoSize = true };
        _copyButton.Click += (_, _) => CopyVisible();
        _closeButton = new Button { Text = "Close", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
        _closeButton.Click += (_, _) => Close();

        bottom.Controls.Add(_showInfo, 0, 0);
        bottom.Controls.Add(_showWarn, 1, 0);
        bottom.Controls.Add(_showError, 2, 0);
        bottom.Controls.Add(_showDebug, 3, 0);
        bottom.Controls.Add(findLabel, 4, 0);
        bottom.Controls.Add(_findBox, 5, 0);
        bottom.Controls.Add(_statusLabel, 6, 0);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0),
            WrapContents = false,
        };
        buttons.Controls.Add(_copyButton);
        buttons.Controls.Add(_closeButton);
        bottom.Controls.Add(buttons, 7, 0);

        // Order matters: Fill control first, then docked siblings.
        Controls.Add(_content);
        Controls.Add(bottom);
        Controls.Add(top);

        _timer = new System.Windows.Forms.Timer { Interval = TailIntervalMs };
        _timer.Tick += (_, _) => Tick();

        Shown += (_, _) => PopulateDayPicker();
        FormClosed += (_, _) =>
        {
            _timer.Stop();
            _timer.Dispose();
            _stream?.Dispose();
            _stream = null;
        };
    }

    private void PopulateDayPicker()
    {
        _dayPicker.Items.Clear();
        if (!Directory.Exists(_logFolder))
        {
            _statusLabel.Text = $"Log folder not found: {_logFolder}";
            return;
        }

        var files = Directory.EnumerateFiles(_logFolder, "service-*.log")
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .OrderByDescending(n => n, StringComparer.Ordinal)
            .ToArray();

        if (files.Length == 0)
        {
            _statusLabel.Text = "No log files yet.";
            return;
        }

        foreach (var f in files)
            _dayPicker.Items.Add(f!);
        _dayPicker.SelectedIndex = 0; // triggers OnDayChanged
    }

    private bool IsLatestDaySelected =>
        _dayPicker.Items.Count > 0 && _dayPicker.SelectedIndex == 0;

    private void OnDayChanged()
    {
        var name = _dayPicker.SelectedItem as string;
        if (string.IsNullOrEmpty(name)) return;

        _stream?.Dispose();
        _stream = null;
        _entries.Clear();
        _carryLine = string.Empty;
        _currentFile = Path.Combine(_logFolder, name);

        try
        {
            _stream = new FileStream(
                _currentFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var len = _stream.Length;
            var startAt = Math.Max(0, len - InitialTailBytes);
            _stream.Seek(startAt, SeekOrigin.Begin);

            // If we started mid-file, skip the partial line so the first parsed line is a real entry header.
            if (startAt > 0)
            {
                int b;
                while ((b = _stream.ReadByte()) != -1 && b != '\n') { }
            }

            ReadAndAppend();
            _lastPos = _stream.Position;
            Render(scrollToEnd: true);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Error opening {name}: {ex.Message}";
        }

        _tailCheckbox.Enabled = IsLatestDaySelected;
        if (!IsLatestDaySelected) _tailCheckbox.Checked = false;
        RescheduleTail();
    }

    private void RescheduleTail()
    {
        if (_tailCheckbox.Checked && _tailCheckbox.Enabled) _timer.Start();
        else _timer.Stop();
    }

    private void Tick()
    {
        if (_stream is null || _currentFile is null) return;
        try
        {
            var len = _stream.Length;
            if (len < _lastPos)
            {
                // Truncated or rotated — reopen from byte 0.
                _stream.Dispose();
                _stream = new FileStream(
                    _currentFile,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                _lastPos = 0;
                _carryLine = string.Empty;
            }
            else if (len == _lastPos)
            {
                return;
            }

            _stream.Seek(_lastPos, SeekOrigin.Begin);
            ReadAndAppend();
            _lastPos = _stream.Position;
            Render(scrollToEnd: true);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Tail error: {ex.Message}";
            _timer.Stop();
        }
    }

    private void ReadAndAppend()
    {
        if (_stream is null) return;

        var sb = new StringBuilder(_carryLine);
        int read;
        while ((read = _stream.Read(_readBuffer, 0, _readBuffer.Length)) > 0)
            sb.Append(Encoding.UTF8.GetString(_readBuffer, 0, read));
        var text = sb.ToString();

        var start = 0;
        int newlineIdx;
        while ((newlineIdx = text.IndexOf('\n', start)) >= 0)
        {
            var lineEnd = newlineIdx;
            if (lineEnd > start && text[lineEnd - 1] == '\r') lineEnd--;
            AppendLine(text.Substring(start, lineEnd - start));
            start = newlineIdx + 1;
        }
        _carryLine = start < text.Length ? text.Substring(start) : string.Empty;
    }

    private void AppendLine(string line)
    {
        var m = EntryPrefix.Match(line);
        if (m.Success)
        {
            _entries.Add(new LogEntry(m.Groups["lvl"].Value, line));
            if (_entries.Count > MaxEntries) _entries.RemoveAt(0);
        }
        else if (_entries.Count > 0 && line.Length > 0)
        {
            // Continuation line (exception stack, multi-line message) — fold into previous entry.
            var prev = _entries[^1];
            _entries[^1] = prev with { Body = prev.Body + "\n" + line };
        }
    }

    private bool LevelEnabled(string level) => level switch
    {
        "INF" => _showInfo.Checked,
        "WRN" => _showWarn.Checked,
        "ERR" or "FTL" => _showError.Checked,
        _ => _showDebug.Checked,
    };

    private static Color LevelColor(string level) => level switch
    {
        "ERR" or "FTL" => Color.FromArgb(180, 0, 0),
        "WRN" => Color.FromArgb(160, 100, 0),
        "DBG" or "VRB" => SystemColors.GrayText,
        _ => SystemColors.WindowText,
    };

    private void Render(bool scrollToEnd = false)
    {
        var find = _findBox.Text.Trim();
        var hasFind = find.Length > 0;
        var visible = 0;
        var hidden = 0;

        SendMessage(_content.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        try
        {
            _content.Clear();
            foreach (var e in _entries)
            {
                if (!LevelEnabled(e.Level)) { hidden++; continue; }
                if (hasFind && e.Body.IndexOf(find, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    hidden++;
                    continue;
                }

                _content.SelectionStart = _content.TextLength;
                _content.SelectionLength = 0;
                _content.SelectionColor = LevelColor(e.Level);
                _content.AppendText(e.Body + "\n");
                visible++;
            }
        }
        finally
        {
            SendMessage(_content.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            // Refresh = Invalidate + Update. Just Invalidate is not enough here:
            // after WM_SETREDRAW toggles, the control's cached paint can show as
            // blank until something (e.g. a mouse selection) forces WM_PAINT.
            _content.Refresh();
        }

        if (scrollToEnd)
        {
            _content.SelectionStart = _content.TextLength;
            _content.SelectionLength = 0;
            _content.ScrollToCaret();
        }

        _statusLabel.Text = hidden > 0
            ? $"{visible:N0} lines, {hidden:N0} hidden"
            : $"{visible:N0} lines";
    }

    private void CopyVisible()
    {
        try
        {
            if (_content.TextLength == 0)
            {
                _statusLabel.Text = "Nothing to copy.";
                return;
            }
            Clipboard.SetText(_content.Text);
            _statusLabel.Text = "Copied.";
        }
        catch (ExternalException)
        {
            _statusLabel.Text = "Clipboard unavailable, try again.";
        }
    }

    private const int WM_SETREDRAW = 0x000B;

    [DllImport("user32.dll")]
    private static extern int SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private readonly record struct LogEntry(string Level, string Body);
}

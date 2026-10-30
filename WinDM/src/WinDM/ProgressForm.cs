using WinDM.Core;
using WinDM.Engine;

namespace WinDM;

/// <summary>Per-download window: status, speed limiter, completion options, and per-connection progress.</summary>
internal sealed class ProgressForm : Form
{
    private readonly DownloadManager _manager;
    private readonly DownloadItem _item;
    private readonly Label _url = new() { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Top, Height = 22 };
    private readonly Dictionary<string, Label> _values = [];
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Top, Height = 18, Maximum = 1000 };
    private readonly Button _details = new() { Text = "<< Hide details", AutoSize = true };
    private readonly Button _pause = new() { Text = "Pause", AutoSize = true };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true };
    private readonly SegmentBar _segments = new() { Dock = DockStyle.Top, Height = 14 };
    private readonly ListView _connections = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
    private readonly Panel _detailsPanel = new() { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
    private readonly CheckBox _useLimit = new() { Text = "Use speed limiter for this download", AutoSize = true };
    private readonly NumericUpDown _limit = new() { Minimum = 1, Maximum = 1_000_000, Value = 512, Width = 90 };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };

    public ProgressForm(DownloadManager manager, DownloadItem item)
    {
        _manager = manager;
        _item = item;
        Icon = Platform.AppIcon;
        Font = SystemFonts.MessageBoxFont ?? Font;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(560, 560);
        MinimumSize = new Size(480, 300);
        Padding = new Padding(12);

        var tabs = new TabControl { Dock = DockStyle.Top, Height = 210 };
        tabs.TabPages.Add(StatusTab());
        tabs.TabPages.Add(LimiterTab());
        tabs.TabPages.Add(CompletionTab());

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(0, 6, 0, 0) };
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        right.Controls.AddRange([_cancel, _pause]);
        buttons.Controls.Add(_details);

        _connections.Columns.Add("N.", 40);
        _connections.Columns.Add("Downloaded", 110, HorizontalAlignment.Right);
        _connections.Columns.Add("Info", 300);
        _detailsPanel.Controls.Add(_connections);
        _detailsPanel.Controls.Add(_segments);
        _detailsPanel.Controls.Add(new Label { Text = "Start positions and download progress by connections", Dock = DockStyle.Top, Height = 20, ForeColor = SystemColors.GrayText });

        var buttonRow = new Panel { Dock = DockStyle.Top, Height = 40 };
        buttonRow.Controls.Add(buttons);
        buttonRow.Controls.Add(right);
        Controls.Add(_detailsPanel);
        Controls.Add(buttonRow);
        Controls.Add(_bar);
        Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8 });
        Controls.Add(tabs);

        _details.Click += (_, _) =>
        {
            _detailsPanel.Visible = !_detailsPanel.Visible;
            _details.Text = _detailsPanel.Visible ? "<< Hide details" : "Show details >>";
            Height = _detailsPanel.Visible ? 560 : 330;
        };
        _pause.Click += (_, _) =>
        {
            if (_item.IsFinished) Platform.Open(_item.Snapshot.FilePath ?? _item.Folder);
            else if (_item.IsActive || _item.Snapshot.Status == DownloadStatus.Queued) _manager.Stop(_item);
            else _manager.Resume(_item);
        };
        _cancel.Click += (_, _) =>
        {
            if (!_item.IsFinished && (_item.IsActive || _item.Snapshot.DownloadedBytes > 0)) _manager.Stop(_item);
            Close();
        };
        _timer.Tick += (_, _) => UpdateView();
        _timer.Start();
        UpdateView();
    }

    private TabPage StatusTab()
    {
        var page = new TabPage("Download status") { Padding = new Padding(10) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var name in new[] { "Status", "File size", "Downloaded", "Transfer rate", "Time left", "Resume capability", "Stream parts" })
        {
            grid.Controls.Add(new Label { Text = name, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 3, 0, 3) });
            var value = new Label { AutoSize = true, Margin = new Padding(0, 3, 0, 3) };
            _values[name] = value;
            grid.Controls.Add(value);
        }
        page.Controls.Add(grid);
        page.Controls.Add(_url);
        return page;
    }

    private TabPage LimiterTab()
    {
        var page = new TabPage("Speed Limiter") { Padding = new Padding(10) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var row = new FlowLayoutPanel { AutoSize = true };
        row.Controls.AddRange([new Label { Text = "Maximum download speed", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, _limit,
                               new Label { Text = "KB/s", AutoSize = true, Margin = new Padding(4, 6, 0, 0) }]);
        flow.Controls.AddRange([_useLimit, row, new Label { Text = "When off, the global limiter from Options applies.", AutoSize = true, ForeColor = SystemColors.GrayText }]);
        page.Controls.Add(flow);

        _useLimit.Checked = _item.SpeedLimitKB is not null;
        _limit.Value = Math.Clamp(_item.SpeedLimitKB ?? 512, 1, 1_000_000);
        row.Enabled = _useLimit.Checked;
        _useLimit.CheckedChanged += (_, _) =>
        {
            row.Enabled = _useLimit.Checked;
            _manager.SetSpeedLimit(_item, _useLimit.Checked ? (int)_limit.Value : null);
        };
        _limit.ValueChanged += (_, _) => { if (_useLimit.Checked) _manager.SetSpeedLimit(_item, (int)_limit.Value); };
        return page;
    }

    private TabPage CompletionTab()
    {
        var page = new TabPage("Options on completion") { Padding = new Padding(10) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        CheckBox Box(string text, bool value, Action<bool> set)
        {
            var box = new CheckBox { Text = text, AutoSize = true, Checked = value };
            box.CheckedChanged += (_, _) => set(box.Checked);
            return box;
        }
        flow.Controls.AddRange(
        [
            Box("Show download complete dialog", _item.ShowCompleteDialog, v => _item.ShowCompleteDialog = v),
            Box("Open the file when done", _item.OpenWhenDone, v => _item.OpenWhenDone = v),
            Box("Put the computer to sleep when done", _item.SleepWhenDone, v => _item.SleepWhenDone = v),
        ]);
        page.Controls.Add(flow);
        return page;
    }

    private void UpdateView()
    {
        var s = _item.Snapshot;
        var percent = s.Fraction is double f ? $"{(int)(f * 100)}% " : "";
        Text = _item.IsFinished ? $"Complete — {_item.DisplayName}" : $"{percent}{_item.DisplayName}";
        _url.Text = _item.Request.Url.ToString();
        _values["Status"].Text = s.Status switch
        {
            DownloadStatus.Queued => "Waiting…",
            DownloadStatus.Probing => "Connecting…",
            DownloadStatus.Downloading => "Receiving data…",
            DownloadStatus.Paused => "Paused",
            DownloadStatus.Completed => "Complete",
            _ => s.Error ?? "Error",
        };
        _values["Status"].ForeColor = s.Status == DownloadStatus.Failed ? Color.Firebrick : SystemColors.ControlText;
        _values["File size"].Text = s.TotalBytes is long total ? Format.Bytes(total) : s.PartsTotal > 0 ? "Stream" : "Unknown";
        _values["Downloaded"].Text = Format.Bytes(s.DownloadedBytes) + (s.Fraction is double fr ? $" ({fr * 100:0.00}%)" : "");
        _values["Transfer rate"].Text = _item.IsActive ? Format.Rate(s.BytesPerSecond) : "";
        _values["Time left"].Text = Format.Duration(_item.TimeLeftSeconds);
        _values["Resume capability"].Text = s.Status is DownloadStatus.Queued or DownloadStatus.Probing ? "" : s.Resumable ? "Yes" : "No";
        _values["Stream parts"].Text = s.PartsTotal > 0 ? $"{s.PartsDone} of {s.PartsTotal}" : "—";
        _bar.Value = (int)((_item.IsFinished ? 1 : s.Fraction ?? 0) * 1000);
        _pause.Text = _item.IsFinished ? "Open" : _item.IsActive || s.Status == DownloadStatus.Queued ? "Pause" : "Resume";
        _cancel.Text = _item.IsFinished ? "Close" : "Cancel";

        _segments.Snapshot = s;
        _segments.Invalidate();
        _connections.BeginUpdate();
        while (_connections.Items.Count > s.Segments.Count) _connections.Items.RemoveAt(_connections.Items.Count - 1);
        for (var i = 0; i < s.Segments.Count; i++)
        {
            string[] cells = [$"{i + 1}", Format.Bytes(s.Segments[i].Downloaded), s.Segments[i].Info];
            if (i >= _connections.Items.Count) _connections.Items.Add(new ListViewItem(cells));
            else for (var c = 0; c < 3; c++) if (_connections.Items[i].SubItems[c].Text != cells[c]) _connections.Items[i].SubItems[c].Text = cells[c];
        }
        _connections.EndUpdate();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        base.OnFormClosed(e);
    }

    /// <summary>Each connection's range drawn at its position in the file, with a red mark where it started.</summary>
    private sealed class SegmentBar : Control
    {
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public ProgressSnapshot? Snapshot { get; set; }

        public SegmentBar() => DoubleBuffered = true;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            g.FillRectangle(SystemBrushes.ControlLight, bounds);
            var s = Snapshot;
            using var fill = new SolidBrush(Color.FromArgb(0, 120, 215));
            if (s?.Status == DownloadStatus.Completed) g.FillRectangle(fill, bounds);
            else if (s is { TotalBytes: > 0 } && s.Segments.Count > 0)
            {
                var scale = (double)Width / s.TotalBytes.Value;
                foreach (var segment in s.Segments)
                {
                    var x = (int)(segment.Start * scale);
                    g.FillRectangle(fill, x, 0, Math.Max(1, (int)(segment.Downloaded * scale)), Height);
                    g.FillRectangle(Brushes.Red, x, 0, 1, Height);
                }
            }
            else if (s?.Fraction is double f) g.FillRectangle(fill, 0, 0, (int)(Width * f), Height);
            g.DrawRectangle(SystemPens.ControlDark, bounds);
        }
    }
}

/// <summary>Shown when a download finishes (unless turned off).</summary>
internal sealed class CompleteForm : Form
{
    public CompleteForm(DownloadManager manager, DownloadItem item)
    {
        Text = "Download complete";
        Icon = Platform.AppIcon;
        Font = SystemFonts.MessageBoxFont ?? Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 210);
        TopMost = true;

        var path = item.Snapshot.FilePath ?? "";
        var size = item.Snapshot.TotalBytes ?? item.Snapshot.DownloadedBytes;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var title = new Label { Text = item.DisplayName, Font = new Font(Font, FontStyle.Bold), AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, Height = 24 };
        layout.Controls.Add(title);
        layout.SetColumnSpan(title, 2);
        void Row(string label, string value)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = SystemColors.GrayText });
            layout.Controls.Add(new Label { Text = value, AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, Height = 20 });
        }
        Row("Downloaded", Format.Bytes(size));
        Row("Address", item.Request.Url.ToString());
        Row("Saved as", path);
        var dontShow = new CheckBox { Text = "Don't show this dialog again", AutoSize = true };
        layout.Controls.Add(dontShow);
        layout.SetColumnSpan(dontShow, 2);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(10, 6, 10, 6) };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        var open = new Button { Text = "Open", AutoSize = true };
        var folder = new Button { Text = "Open Folder", AutoSize = true };
        buttons.Controls.AddRange([close, open, folder]);
        open.Click += (_, _) => { Platform.Open(path); Close(); };
        folder.Click += (_, _) => { Platform.ShowInExplorer(path); Close(); };
        close.Click += (_, _) => Close();
        AcceptButton = open;
        CancelButton = close;
        FormClosed += (_, _) =>
        {
            if (!dontShow.Checked) return;
            var settings = OptionsForm.Clone(manager.Settings);
            settings.ShowCompleteDialog = false;
            manager.ApplySettings(settings);
        };
        Controls.Add(layout);
        Controls.Add(buttons);
    }
}

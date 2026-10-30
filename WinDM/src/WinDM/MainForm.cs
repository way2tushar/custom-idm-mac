using WinDM.Core;
using WinDM.Engine;

namespace WinDM;

/// <summary>Main window: toolbar, category tree, download list, menus, and the tray icon.</summary>
internal sealed class MainForm : Form
{
    private readonly DownloadManager _manager;
    private readonly Bridge _bridge;
    private readonly ToolStrip _toolbar = new() { ImageScalingSize = new Size(32, 32), GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 2, 6, 2) };
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false, ShowLines = false, FullRowSelect = true, ItemHeight = 24, BorderStyle = BorderStyle.None };
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, BorderStyle = BorderStyle.None };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
    private readonly NotifyIcon _tray = new() { Text = "WinDM", Visible = true };
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 500 };
    private readonly Dictionary<Guid, ListViewItem> _rows = [];
    private readonly Dictionary<Guid, ProgressForm> _progressWindows = [];
    private readonly ImageList _fileIcons = new() { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
    private readonly bool _startHidden;
    private bool _exiting;
    private int _sortColumn = 6;
    private bool _sortDescending = true;

    private ToolStripButton _resume = null!, _stop = null!, _stopAll = null!, _delete = null!, _deleteCompleted = null!,
        _startQueue = null!, _stopQueue = null!;

    public MainForm(DownloadManager manager, bool startHidden)
    {
        _manager = manager;
        _startHidden = startHidden;
        Text = "WinDM";
        Icon = Platform.AppIcon;
        _tray.Icon = Platform.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1150, 620);
        MinimumSize = new Size(820, 420);
        Font = SystemFonts.MessageBoxFont ?? Font;

        BuildMenu();
        BuildToolbar();
        BuildTree();
        BuildList();
        BuildTray();

        _split.Panel1.Controls.Add(_tree);
        _split.Panel2.Controls.Add(_list);
        Controls.Add(_split);
        Controls.Add(_toolbar);
        Controls.Add(MainMenuStrip!);
        Load += (_, _) => _split.SplitterDistance = 200;

        _bridge = new Bridge(() => _manager.Settings);
        _bridge.DownloadRequested += request => BeginInvoke(() => HandleBrowserRequest(request));
        _bridge.ShowRequested += () => BeginInvoke(ShowMain);

        _manager.Completed += item => BeginInvoke(() => OnCompleted(item));
        _manager.QueueFinished += action => BeginInvoke(() =>
        {
            if (action == WhenQueueDone.Exit) ExitApp();
            else if (action == WhenQueueDone.Sleep) Platform.Sleep();
        });

        _refresh.Tick += (_, _) => RefreshList();
        _refresh.Start();
        RefreshList();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Started only now: bridge callbacks switch to the UI thread with BeginInvoke, which needs the handle.
        if (_manager.Settings.BrowserBridge && !_bridge.Start())
            _tray.ShowBalloonTip(5000, "WinDM", $"Port {Bridge.Port} is in use, so browser extensions can't reach WinDM.", ToolTipIcon.Warning);
    }

    protected override void SetVisibleCore(bool value)
    {
        // Started at login with --background: stay in the tray until opened.
        if (_startHidden && !IsHandleCreated) { CreateHandle(); value = false; }
        base.SetVisibleCore(value);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (FFmpeg.Find() is null && !_manager.Settings.FFmpegPromptDismissed)
            BeginInvoke(() => new FFmpegForm(_manager).Show(this));
    }

    // ----- Building the window -----

    private void BuildMenu()
    {
        var menu = new MenuStrip();
        var tasks = new ToolStripMenuItem("&Tasks");
        tasks.DropDownItems.AddRange(
        [
            Item("&Add New Download…", Keys.Control | Keys.N, (_, _) => ShowAdd(null)),
            Item("Add Links from &Clipboard to Queue", Keys.Control | Keys.Shift | Keys.V, (_, _) => AddFromClipboard()),
            new ToolStripSeparator(),
            Item("&Start Queue", Keys.None, (_, _) => _manager.StartQueue()),
            Item("S&top Queue", Keys.None, (_, _) => _manager.StopQueue()),
            Item("Sc&heduler…", Keys.Control | Keys.K, (_, _) => ShowScheduler()),
            new ToolStripSeparator(),
            Item("E&xit", Keys.None, (_, _) => ExitApp()),
        ]);
        var downloads = new ToolStripMenuItem("&Downloads");
        downloads.DropDownItems.AddRange(
        [
            Item("&Resume", Keys.Control | Keys.R, (_, _) => ForSelected(_manager.Resume)),
            Item("&Stop", Keys.Control | Keys.OemPeriod, (_, _) => ForSelected(_manager.Stop)),
            Item("Stop &All", Keys.Control | Keys.Shift | Keys.OemPeriod, (_, _) => _manager.StopAll()),
            Item("Re&download", Keys.None, (_, _) => ForSelected(_manager.Redownload)),
            new ToolStripSeparator(),
            Item("&Open", Keys.Control | Keys.O, (_, _) => ForSelected(Open)),
            Item("Open &Folder", Keys.Control | Keys.Shift | Keys.O, (_, _) => ForSelected(Reveal)),
            Item("&Copy Address", Keys.None, (_, _) => CopyAddresses()),
            Item("&Properties", Keys.Control | Keys.I, (_, _) => ForSelected(ShowProgress)),
            new ToolStripSeparator(),
            Item("&Delete", Keys.Delete, (_, _) => DeleteSelected(false)),
            Item("Delete All &Completed", Keys.None, (_, _) => _manager.DeleteCompleted()),
            new ToolStripSeparator(),
            Item("Turn Speed &Limiter On/Off", Keys.Control | Keys.L, (_, _) => ToggleLimiter()),
            Item("&Options…", Keys.Control | Keys.Oemcomma, (_, _) => ShowOptions()),
        ]);
        var view = new ToolStripMenuItem("&View");
        view.DropDownItems.Add(Item("Show/Hide &Categories", Keys.Control | Keys.B, (_, _) => _split.Panel1Collapsed = !_split.Panel1Collapsed));
        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(Item("&About WinDM", Keys.None, (_, _) => MessageBox.Show(this,
            "WinDM 1.0\nMulti-connection download manager with browser integration and stream downloads.",
            "About WinDM", MessageBoxButtons.OK, MessageBoxIcon.Information)));
        menu.Items.AddRange([tasks, downloads, view, help]);
        MainMenuStrip = menu;
    }

    private static ToolStripMenuItem Item(string text, Keys keys, EventHandler click) =>
        new(text, null, click) { ShortcutKeys = keys };

    private void BuildToolbar()
    {
        ToolStripButton Button(string text, string glyph, Color color, EventHandler click)
        {
            var button = new ToolStripButton(text, Glyphs.Render(glyph, color, 32), click)
            {
                TextImageRelation = TextImageRelation.ImageAboveText,
                ImageScaling = ToolStripItemImageScaling.SizeToFit,
                AutoSize = true,
                Padding = new Padding(4, 0, 4, 0),
            };
            _toolbar.Items.Add(button);
            return button;
        }
        Button("Add URL", Glyphs.Add, Color.SeaGreen, (_, _) => ShowAdd(null));
        _resume = Button("Resume", Glyphs.Play, Color.RoyalBlue, (_, _) => ForSelected(_manager.Resume));
        _stop = Button("Stop", Glyphs.Stop, Color.DarkOrange, (_, _) => ForSelected(_manager.Stop));
        _stopAll = Button("Stop All", Glyphs.Cancel, Color.Firebrick, (_, _) => _manager.StopAll());
        _toolbar.Items.Add(new ToolStripSeparator());
        _delete = Button("Delete", Glyphs.Delete, Color.DimGray, (_, _) => DeleteSelected(false));
        _deleteCompleted = Button("Delete Completed", Glyphs.Completed, Color.DimGray, (_, _) => _manager.DeleteCompleted());
        _toolbar.Items.Add(new ToolStripSeparator());
        Button("Options", Glyphs.Settings, Color.SlateGray, (_, _) => ShowOptions());
        Button("Scheduler", Glyphs.Calendar, Color.MediumOrchid, (_, _) => ShowScheduler());
        _startQueue = Button("Start Queue", Glyphs.List, Color.Teal, (_, _) => _manager.StartQueue());
        _stopQueue = Button("Stop Queue", Glyphs.Pause, Color.PaleVioletRed, (_, _) => _manager.StopQueue());
    }

    private sealed record Node(SidebarScope Scope, FileCategory? Category);
    private enum SidebarScope { All, Unfinished, Finished, Queue }

    private void BuildTree()
    {
        var images = new ImageList { ImageSize = new Size(18, 18), ColorDepth = ColorDepth.Depth32Bit };
        void Image(string key, string glyph, Color color) => images.Images.Add(key, Glyphs.Render(glyph, color, 18));
        Image("all", Glyphs.Download, Color.RoyalBlue);
        Image("unfinished", Glyphs.Recent, Color.DarkOrange);
        Image("finished", Glyphs.Completed, Color.SeaGreen);
        Image("queue", Glyphs.List, Color.Teal);
        Image("Compressed", Glyphs.Package, Color.SaddleBrown);
        Image("Documents", Glyphs.Document, Color.SteelBlue);
        Image("Music", Glyphs.Audio, Color.MediumVioletRed);
        Image("Programs", Glyphs.Apps, Color.DarkSlateBlue);
        Image("Video", Glyphs.Video, Color.Crimson);
        _tree.ImageList = images;

        TreeNode Scope(string title, string image, SidebarScope scope, bool withCategories)
        {
            var node = new TreeNode(title) { Tag = new Node(scope, null), ImageKey = image, SelectedImageKey = image };
            if (withCategories)
                foreach (var category in Enum.GetValues<FileCategory>().Where(c => c != FileCategory.General))
                    node.Nodes.Add(new TreeNode(category.ToString()) { Tag = new Node(scope, category), ImageKey = category.ToString(), SelectedImageKey = category.ToString() });
            return node;
        }
        _tree.Nodes.AddRange(
        [
            Scope("All Downloads", "all", SidebarScope.All, true),
            Scope("Unfinished", "unfinished", SidebarScope.Unfinished, true),
            Scope("Finished", "finished", SidebarScope.Finished, true),
            Scope("Main Queue", "queue", SidebarScope.Queue, false),
        ]);
        _tree.Nodes[0].Expand();
        _tree.SelectedNode = _tree.Nodes[0];
        _tree.AfterSelect += (_, _) => { _rows.Clear(); _list.Items.Clear(); RefreshList(); };
    }

    private void BuildList()
    {
        _list.SmallImageList = _fileIcons;
        (string Title, int Width, HorizontalAlignment Align)[] columns =
        [
            ("File Name", 260, HorizontalAlignment.Left), ("Q", 26, HorizontalAlignment.Center), ("Size", 80, HorizontalAlignment.Right),
            ("Status", 80, HorizontalAlignment.Left), ("Time left", 70, HorizontalAlignment.Right), ("Transfer rate", 90, HorizontalAlignment.Right),
            ("Last Try Date", 130, HorizontalAlignment.Left), ("Description", 180, HorizontalAlignment.Left),
        ];
        foreach (var (title, width, align) in columns) _list.Columns.Add(title, width, align);
        _list.ColumnClick += (_, e) =>
        {
            _sortDescending = e.Column == _sortColumn && !_sortDescending;
            _sortColumn = e.Column;
            _rows.Clear();
            _list.Items.Clear();
            RefreshList();
        };
        _list.DoubleClick += (_, _) => ForSelected(item => { if (item.IsFinished) Open(item); else ShowProgress(item); });
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            new ToolStripMenuItem("Open", null, (_, _) => ForSelected(Open)),
            new ToolStripMenuItem("Open Folder", null, (_, _) => ForSelected(Reveal)),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Resume Download", null, (_, _) => ForSelected(_manager.Resume)),
            new ToolStripMenuItem("Stop Download", null, (_, _) => ForSelected(_manager.Stop)),
            new ToolStripMenuItem("Redownload", null, (_, _) => ForSelected(_manager.Redownload)),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Add to Main Queue", null, (_, _) => ForSelected(i => _manager.SetQueued(i, true))),
            new ToolStripMenuItem("Remove from Queue", null, (_, _) => ForSelected(i => _manager.SetQueued(i, false))),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Copy Address", null, (_, _) => CopyAddresses()),
            new ToolStripMenuItem("Delete", null, (_, _) => DeleteSelected(false)),
            new ToolStripMenuItem("Delete and Remove File", null, (_, _) => DeleteSelected(true)),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Properties", null, (_, _) => ForSelected(ShowProgress)),
        ]);
        _list.ContextMenuStrip = menu;
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            new ToolStripMenuItem("Open WinDM", null, (_, _) => ShowMain()) { Font = new Font(menu.Font, FontStyle.Bold) },
            new ToolStripMenuItem("Add URL…", null, (_, _) => ShowAdd(null)),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Stop All", null, (_, _) => _manager.StopAll()),
            new ToolStripMenuItem("Start Queue", null, (_, _) => _manager.StartQueue()),
            new ToolStripMenuItem("Stop Queue", null, (_, _) => _manager.StopQueue()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()),
        ]);
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowMain();
    }

    // ----- List contents -----

    private IEnumerable<DownloadItem> VisibleItems()
    {
        var node = _tree.SelectedNode?.Tag as Node ?? new Node(SidebarScope.All, null);
        return _manager.Items.Where(item =>
            (node.Scope switch
            {
                SidebarScope.Unfinished => !item.IsFinished,
                SidebarScope.Finished => item.IsFinished,
                SidebarScope.Queue => item.InQueue,
                _ => true,
            }) && (node.Category is null || item.Category == node.Category));
    }

    private static string StatusText(DownloadItem item)
    {
        var s = item.Snapshot;
        string Percent() => s.Fraction is double f ? $"{f * 100:0.00}%" : "";
        return s.Status switch
        {
            DownloadStatus.Completed => "Complete",
            DownloadStatus.Queued => item.InQueue ? "Queued" : "Starting…",
            DownloadStatus.Probing => "Connecting…",
            DownloadStatus.Downloading => Percent(),
            DownloadStatus.Paused => s.DownloadedBytes > 0 ? Percent() : item.InQueue ? "Queued" : "Stopped",
            _ => "Error",
        };
    }

    private static string[] Cells(DownloadItem item)
    {
        var s = item.Snapshot;
        return
        [
            item.DisplayName,
            item.InQueue ? "◷" : "",
            s.TotalBytes is long total ? Format.Bytes(total) : s.DownloadedBytes > 0 ? Format.Bytes(s.DownloadedBytes) : "",
            StatusText(item),
            Format.Duration(item.TimeLeftSeconds),
            item.IsActive ? Format.Rate(s.BytesPerSecond) : "",
            (item.LastTry ?? item.AddedAt).ToString("g"),
            item.Description,
        ];
    }

    private IComparable SortKey(DownloadItem item) => _sortColumn switch
    {
        0 => item.DisplayName,
        2 => item.Snapshot.TotalBytes ?? -1,
        3 => item.IsFinished ? 2 : item.Snapshot.Fraction ?? 0,
        4 => item.TimeLeftSeconds ?? double.MaxValue,
        5 => item.Snapshot.BytesPerSecond,
        7 => item.Description,
        _ => item.LastTry ?? item.AddedAt,
    };

    private void RefreshList()
    {
        var items = VisibleItems().ToList();
        items = (_sortDescending ? items.OrderByDescending(SortKey) : items.OrderBy(SortKey)).ToList();
        var visible = items.Select(i => i.Id).ToHashSet();

        _list.BeginUpdate();
        foreach (var gone in _rows.Keys.Where(id => !visible.Contains(id)).ToList())
        {
            _list.Items.Remove(_rows[gone]);
            _rows.Remove(gone);
        }
        foreach (var item in items)
        {
            var cells = Cells(item);
            if (!_rows.TryGetValue(item.Id, out var row))
            {
                var ext = Path.GetExtension(item.DisplayName);
                if (!_fileIcons.Images.ContainsKey(ext)) _fileIcons.Images.Add(ext, Platform.FileIcon(item.DisplayName));
                row = new ListViewItem(cells) { Tag = item.Id, ImageKey = ext };
                _rows[item.Id] = row;
                _list.Items.Add(row);
            }
            else
            {
                for (var i = 0; i < cells.Length; i++)
                    if (row.SubItems[i].Text != cells[i]) row.SubItems[i].Text = cells[i];
            }
            row.ForeColor = item.Snapshot.Status == DownloadStatus.Failed ? Color.Firebrick : SystemColors.WindowText;
            row.ToolTipText = item.Snapshot.Error ?? "";
        }
        _list.EndUpdate();

        var active = _manager.Items.Where(i => i.IsActive).ToList();
        _tray.Text = active.Count == 0 ? "WinDM" : $"WinDM — {active.Count} downloading, {Format.Rate(active.Sum(i => i.Snapshot.BytesPerSecond))}";
        UpdateButtons();
    }

    private List<DownloadItem> Selected() =>
        _list.SelectedItems.Cast<ListViewItem>().Select(r => _manager.Find((Guid)r.Tag!)).OfType<DownloadItem>().ToList();

    private void ForSelected(Action<DownloadItem> action) { foreach (var item in Selected()) action(item); }

    private void UpdateButtons()
    {
        var selected = Selected();
        var all = _manager.Items;
        _resume.Enabled = selected.Any(i => i.Snapshot.Status is DownloadStatus.Paused or DownloadStatus.Failed);
        _stop.Enabled = selected.Any(i => i.IsActive || i.Snapshot.Status == DownloadStatus.Queued);
        _stopAll.Enabled = all.Any(i => i.IsActive || i.Snapshot.Status == DownloadStatus.Queued);
        _delete.Enabled = selected.Count > 0;
        _deleteCompleted.Enabled = all.Any(i => i.IsFinished);
        _startQueue.Enabled = !_manager.QueueRunning && all.Any(i => i.InQueue && !i.IsFinished);
        _stopQueue.Enabled = _manager.QueueRunning;
    }

    // ----- Actions -----

    /// <summary>A download from a browser extension or a macdm:// link.</summary>
    public void HandleBrowserRequest(DownloadRequest request)
    {
        if (_manager.Settings.ShowStartDialog) ShowAdd(request);
        else StartAndShowProgress(_manager.Add(request));
    }

    private void StartAndShowProgress(DownloadItem item)
    {
        RefreshList();
        if (_manager.Settings.ShowProgressDialog) ShowProgress(item);
    }

    private void ShowAdd(DownloadRequest? request)
    {
        var form = new AddForm(_manager, request);
        form.Added += item => { if (!item.InQueue) StartAndShowProgress(item); else RefreshList(); };
        form.Show();
        form.Activate();
    }

    private void AddFromClipboard()
    {
        var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (Uri.TryCreate(word, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
                _manager.Add(new DownloadRequest { Url = url }, startNow: false, toQueue: true);
        RefreshList();
    }

    private void ShowProgress(DownloadItem item)
    {
        if (_progressWindows.TryGetValue(item.Id, out var existing) && !existing.IsDisposed)
        {
            existing.Activate();
            return;
        }
        var form = new ProgressForm(_manager, item);
        form.FormClosed += (_, _) => _progressWindows.Remove(item.Id);
        _progressWindows[item.Id] = form;
        form.Show();
    }

    private void OnCompleted(DownloadItem item)
    {
        if (_progressWindows.TryGetValue(item.Id, out var progress) && !progress.IsDisposed) progress.Close();
        if (_manager.Settings.PlaySound) System.Media.SystemSounds.Asterisk.Play();
        if (item.OpenWhenDone && item.Snapshot.FilePath is { } path) Platform.Open(path);
        if (item.ShowCompleteDialog && _manager.Settings.ShowCompleteDialog) new CompleteForm(_manager, item).Show();
        else _tray.ShowBalloonTip(4000, "Download complete", item.DisplayName, ToolTipIcon.Info);
        if (item.SleepWhenDone) Platform.Sleep();
    }

    private void Open(DownloadItem item) { if (item.Snapshot.FilePath is { } path) Platform.Open(path); }
    private void Reveal(DownloadItem item) => Platform.ShowInExplorer(item.Snapshot.FilePath ?? item.Folder);

    private void CopyAddresses()
    {
        var text = string.Join(Environment.NewLine, Selected().Select(i => i.Request.Url.ToString()));
        if (text.Length > 0) Clipboard.SetText(text);
    }

    private void DeleteSelected(bool deleteFile)
    {
        var selected = Selected();
        if (selected.Count == 0) return;
        var message = (selected.Count == 1 ? "Delete this download from the list?" : $"Delete {selected.Count} downloads from the list?")
            + Environment.NewLine + (deleteFile ? "Downloaded files will be deleted." : "Downloaded files stay on disk. Partial data is discarded.");
        if (MessageBox.Show(this, message, "WinDM", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        foreach (var item in selected) _manager.Remove(item, deleteFile);
        RefreshList();
    }

    private void ToggleLimiter()
    {
        var settings = OptionsForm.Clone(_manager.Settings);
        settings.SpeedLimitEnabled = !settings.SpeedLimitEnabled;
        _manager.ApplySettings(settings);
        _tray.ShowBalloonTip(2000, "WinDM", settings.SpeedLimitEnabled ? $"Speed limiter on ({settings.SpeedLimitKB} KB/s)" : "Speed limiter off", ToolTipIcon.Info);
    }

    private void ShowOptions()
    {
        using var form = new OptionsForm(_manager);
        if (form.ShowDialog(this) == DialogResult.OK && _manager.Settings.BrowserBridge)
        {
            _bridge.Stop();
            _bridge.Start();
        }
        else if (!_manager.Settings.BrowserBridge)
        {
            _bridge.Stop();
        }
    }

    private void ShowScheduler()
    {
        using var form = new SchedulerForm(_manager);
        form.ShowDialog(this);
    }

    private void ShowMain()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    // ----- Closing -----

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window keeps WinDM running in the tray: downloads continue, extensions keep working.
        if (!_exiting && e.CloseReason == CloseReason.UserClosing && _manager.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _exiting = true;
        _refresh.Stop();
        _bridge.Stop();
        _manager.PauseAllForExit();
        Thread.Sleep(300); // let jobs save their resume state
        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }
}

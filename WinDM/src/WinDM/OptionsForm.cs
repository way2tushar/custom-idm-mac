using System.Text.Json;
using WinDM.Core;
using WinDM.Engine;

namespace WinDM;

/// <summary>Options: General, Save To, File Types, Connection, Downloads, Proxy. Changes apply on OK.</summary>
internal sealed class OptionsForm : Form
{
    private readonly DownloadManager _manager;
    private readonly AppSettings _settings;
    private readonly List<Action> _apply = [];

    public static AppSettings Clone(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;

    public OptionsForm(DownloadManager manager)
    {
        _manager = manager;
        _settings = Clone(manager.Settings);
        Text = "Options";
        Icon = Platform.AppIcon;
        Font = SystemFonts.MessageBoxFont ?? Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(600, 500);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.AddRange([General(), SaveTo(), FileTypes(), Connection(), Downloads(), Proxy()]);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 8, 8, 4) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };
        buttons.Controls.AddRange([cancel, ok]);
        Controls.Add(tabs);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) =>
        {
            foreach (var apply in _apply) apply();
            try { Platform.RunAtLogin = _settings.RunAtLogin; } catch { }
            _manager.ApplySettings(_settings);
        };
    }

    // ----- Helpers -----

    private static TabPage Page(string title, out FlowLayoutPanel flow)
    {
        var page = new TabPage(title) { Padding = new Padding(12), AutoScroll = true };
        flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        page.Controls.Add(flow);
        return page;
    }

    private static Label Heading(string text) => new() { Text = text, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold), Margin = new Padding(0, 10, 0, 4) };
    private static Label Note(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(540, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 0, 6) };

    private CheckBox Check(string text, bool value, Action<bool> set)
    {
        var box = new CheckBox { Text = text, AutoSize = true, Checked = value };
        _apply.Add(() => set(box.Checked));
        return box;
    }

    private Control FolderPicker(string value, Action<string> set, string placeholder = "")
    {
        var row = new Panel { Width = 540, Height = 28 };
        var text = new TextBox { Text = value, PlaceholderText = placeholder, Dock = DockStyle.Fill };
        var browse = new Button { Text = "Browse…", Dock = DockStyle.Right, Width = 80 };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = text.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK) text.Text = dialog.SelectedPath;
        };
        row.Controls.Add(text);
        row.Controls.Add(browse);
        _apply.Add(() => set(text.Text.Trim()));
        return row;
    }

    // ----- Tabs -----

    private TabPage General()
    {
        var page = Page("General", out var flow);
        var ffmpegStatus = new Label { AutoSize = true, Margin = new Padding(0, 6, 8, 0) };
        var ffmpegInstall = new Button { Text = "Install…", AutoSize = true };
        void UpdateFFmpeg()
        {
            var found = FFmpeg.Find() is not null;
            ffmpegStatus.Text = found ? "ffmpeg: installed ✔" : "ffmpeg: not installed";
            ffmpegStatus.ForeColor = found ? Color.SeaGreen : Color.DarkOrange;
            ffmpegInstall.Visible = !found;
        }
        ffmpegInstall.Click += (_, _) => { using var form = new FFmpegForm(_manager); form.ShowDialog(this); UpdateFFmpeg(); };
        UpdateFFmpeg();
        var ffmpegRow = new FlowLayoutPanel { AutoSize = true };
        ffmpegRow.Controls.AddRange([ffmpegStatus, ffmpegInstall]);

        _settings.RunAtLogin = Platform.RunAtLogin;
        flow.Controls.AddRange(
        [
            Heading("Running in the background"),
            Check("Launch WinDM when I sign in to Windows", _settings.RunAtLogin, v => _settings.RunAtLogin = v),
            Check("Keep running in the notification area when the window is closed", _settings.MinimizeToTray, v => _settings.MinimizeToTray = v),
            Note("Downloads continue and browser extensions keep working while WinDM is in the notification area. Exit from its icon's menu."),
            Heading("Browser integration"),
            Check("Accept downloads from browser extensions", _settings.BrowserBridge, v => _settings.BrowserBridge = v),
            Check("Capture downloads started in the browser", _settings.BrowserCapture, v => _settings.BrowserCapture = v),
            Check("Show the \"Download with MacDM\" button over videos", _settings.VideoGrabber, v => _settings.VideoGrabber = v),
            Note($"Use the MacDM extension in Chrome or Edge; it connects to http://127.0.0.1:{Bridge.Port}."),
            Heading("Video tools"),
            ffmpegRow,
            Heading("Sounds"),
            Check("Play a sound when a download completes", _settings.PlaySound, v => _settings.PlaySound = v),
        ]);
        return page;
    }

    private TabPage SaveTo()
    {
        var page = Page("Save To", out var flow);
        flow.Controls.Add(Heading("Default download folder"));
        flow.Controls.Add(FolderPicker(_settings.DownloadFolder, v => { if (v.Length > 0) _settings.DownloadFolder = v; }));
        flow.Controls.Add(Heading("Category folders"));
        flow.Controls.Add(Note("Leave empty to use the default folder."));
        foreach (var category in Enum.GetValues<FileCategory>())
        {
            flow.Controls.Add(new Label { Text = category.ToString(), AutoSize = true, Margin = new Padding(0, 6, 0, 0) });
            flow.Controls.Add(FolderPicker(_settings.CategoryFolders.GetValueOrDefault(category) ?? "", v =>
            {
                if (v.Length > 0) _settings.CategoryFolders[category] = v; else _settings.CategoryFolders.Remove(category);
            }, "Default folder"));
        }
        return page;
    }

    private TabPage FileTypes()
    {
        var page = Page("File Types", out var flow);
        TextBox Editor(string value, int height) => new() { Text = value, Multiline = true, Width = 540, Height = height, ScrollBars = ScrollBars.Vertical, Font = new Font(FontFamily.GenericMonospace, 9) };

        var types = Editor(_settings.CaptureTypes, 90);
        var typesDefault = new Button { Text = "Default", AutoSize = true };
        typesDefault.Click += (_, _) => types.Text = CaptureRules.DefaultTypes;
        var sites = Editor(_settings.ExcludedSites, 60);
        var sitesDefault = new Button { Text = "Default", AutoSize = true };
        sitesDefault.Click += (_, _) => sites.Text = CaptureRules.DefaultSites;
        var addresses = _settings.ExcludedAddresses.ToList();
        var count = new Label { AutoSize = true, Margin = new Padding(0, 6, 8, 0) };
        void UpdateCount() => count.Text = addresses.Count == 0 ? "No addresses" : $"{addresses.Count} address{(addresses.Count == 1 ? "" : "es")}";
        UpdateCount();
        var edit = new Button { Text = "Edit List…", AutoSize = true };
        edit.Click += (_, _) => { using var editor = new AddressListForm(addresses); editor.ShowDialog(this); UpdateCount(); };
        var addressRow = new FlowLayoutPanel { AutoSize = true };
        addressRow.Controls.AddRange([count, edit]);
        _apply.Add(() =>
        {
            _settings.CaptureTypes = types.Text.Trim();
            _settings.ExcludedSites = sites.Text.Trim();
            _settings.ExcludedAddresses = addresses;
        });

        flow.Controls.AddRange(
        [
            Heading("Automatically download these file types"),
            types, typesDefault,
            Note("Extensions separated by spaces. Applies to downloads the browser starts; links you send with \"Download with MacDM\" are always accepted."),
            Heading("Don't download automatically from these sites"),
            sites, sitesDefault,
            Note("Names separated by spaces; *.example.com covers every subdomain."),
            Heading("Don't download automatically from these addresses"),
            addressRow,
        ]);
        return page;
    }

    private TabPage Connection()
    {
        var page = Page("Connection", out var flow);
        var connections = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        int[] choices = [1, 2, 4, 8, 16, 24, 32];
        connections.Items.AddRange(choices.Cast<object>().ToArray());
        connections.SelectedItem = choices.Contains(_settings.Connections) ? _settings.Connections : 8;
        _apply.Add(() => _settings.Connections = (int)connections.SelectedItem!);
        flow.Controls.AddRange(
        [
            Heading("Max. connections number"),
            connections,
            Note("Each download is split into this many ranges at most. A connection that finishes takes over half of the largest remaining range. Applies to downloads started or resumed after the change."),
        ]);
        return page;
    }

    private TabPage Downloads()
    {
        var page = Page("Downloads", out var flow);
        var rename = new RadioButton { Text = "Add a number to the new file", AutoSize = true, Checked = _settings.Duplicates == DuplicateAction.Rename };
        var overwrite = new RadioButton { Text = "Replace the existing file", AutoSize = true, Checked = _settings.Duplicates == DuplicateAction.Overwrite };
        var duplicates = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown };
        duplicates.Controls.AddRange([rename, overwrite]);
        _apply.Add(() => _settings.Duplicates = overwrite.Checked ? DuplicateAction.Overwrite : DuplicateAction.Rename);

        var limit = new NumericUpDown { Minimum = 16, Maximum = 1_048_576, Increment = 64, Value = Math.Clamp(_settings.SpeedLimitKB, 16, 1_048_576), Width = 100 };
        _apply.Add(() => _settings.SpeedLimitKB = (int)limit.Value);
        var limitRow = new FlowLayoutPanel { AutoSize = true };
        limitRow.Controls.AddRange([new Label { Text = "Maximum download speed", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, limit,
                                    new Label { Text = "KB/s", AutoSize = true, Margin = new Padding(4, 6, 0, 0) }]);

        flow.Controls.AddRange(
        [
            Heading("Dialogs"),
            Check("Show start download dialog for browser downloads", _settings.ShowStartDialog, v => _settings.ShowStartDialog = v),
            Check("Show download progress dialog", _settings.ShowProgressDialog, v => _settings.ShowProgressDialog = v),
            Check("Show download complete dialog", _settings.ShowCompleteDialog, v => _settings.ShowCompleteDialog = v),
            Heading("If a file with the same name exists"),
            duplicates,
            Heading("Speed Limiter"),
            Check("Turn on speed limiter", _settings.SpeedLimitEnabled, v => _settings.SpeedLimitEnabled = v),
            limitRow,
            Note("Applies to each download that has no limit of its own."),
        ]);
        return page;
    }

    private TabPage Proxy()
    {
        var page = Page("Proxy", out var flow);
        var host = new TextBox { Text = _settings.ProxyHost, Width = 300, PlaceholderText = "proxy.example.com" };
        var port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = Math.Clamp(_settings.ProxyPort, 1, 65535), Width = 90 };
        _apply.Add(() => { _settings.ProxyHost = host.Text.Trim(); _settings.ProxyPort = (int)port.Value; });
        flow.Controls.AddRange(
        [
            Heading("Proxy server"),
            Check("Use a manual HTTP/HTTPS proxy", _settings.ProxyEnabled, v => _settings.ProxyEnabled = v),
            new Label { Text = "Address", AutoSize = true }, host,
            new Label { Text = "Port", AutoSize = true }, port,
            Note("When off, the Windows proxy settings apply."),
        ]);
        return page;
    }

    /// <summary>Addresses WinDM won't take over; * matches anything.</summary>
    private sealed class AddressListForm : Form
    {
        public AddressListForm(List<string> addresses)
        {
            Text = "Addresses WinDM won't take over";
            Icon = Platform.AppIcon;
            Font = SystemFonts.MessageBoxFont ?? Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(500, 320);
            Padding = new Padding(12);

            var list = new ListBox { Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended, IntegralHeight = false };
            list.Items.AddRange(addresses.Cast<object>().ToArray());
            var entry = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "https://example.com/files/*" };
            var add = new Button { Text = "Add", Dock = DockStyle.Right, Width = 70 };
            var remove = new Button { Text = "Remove", Dock = DockStyle.Right, Width = 80 };
            var row = new Panel { Dock = DockStyle.Bottom, Height = 30 };
            row.Controls.AddRange([entry, add, remove]);
            var done = new Button { Text = "Done", Dock = DockStyle.Bottom, DialogResult = DialogResult.OK };
            Controls.Add(list);
            Controls.Add(row);
            Controls.Add(done);
            Controls.Add(new Label { Text = "Full addresses; * matches anything.", Dock = DockStyle.Top, Height = 22, ForeColor = SystemColors.GrayText });
            AcceptButton = done;

            void Add()
            {
                var address = entry.Text.Trim();
                if (address.Length == 0 || addresses.Contains(address)) return;
                addresses.Add(address);
                list.Items.Add(address);
                entry.Clear();
            }
            add.Click += (_, _) => Add();
            entry.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { Add(); e.SuppressKeyPress = true; } };
            remove.Click += (_, _) =>
            {
                foreach (var selected in list.SelectedItems.Cast<string>().ToList())
                {
                    addresses.Remove(selected);
                    list.Items.Remove(selected);
                }
            };
        }
    }
}

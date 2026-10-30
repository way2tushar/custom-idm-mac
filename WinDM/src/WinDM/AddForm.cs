using System.Diagnostics;
using System.Text;
using WinDM.Core;
using WinDM.Engine;

namespace WinDM;

/// <summary>Download file info: address, category, save location, description, and a duplicate warning,
/// with Start Download / Download Later / Cancel.</summary>
internal sealed class AddForm : Form
{
    private readonly DownloadManager _manager;
    private readonly DownloadRequest? _initial;
    private readonly TextBox _url = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _category = new() { Dock = DockStyle.Left, Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _folder = new() { Dock = DockStyle.Fill };
    private readonly TextBox _name = new() { Dock = DockStyle.Fill, PlaceholderText = "Detected from the server" };
    private readonly CheckBox _remember = new() { AutoSize = true };
    private readonly TextBox _description = new() { Dock = DockStyle.Fill, Multiline = true, Height = 44 };
    private readonly Label _warning = new() { AutoSize = false, Dock = DockStyle.Fill, ForeColor = Color.DarkOrange, Height = 34 };
    private readonly RadioButton _rename = new() { AutoSize = true, Checked = true };
    private readonly RadioButton _replace = new() { Text = "Replace the existing file", AutoSize = true };
    private readonly FlowLayoutPanel _duplicateChoice = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly Button _start = new() { Text = "Start Download", AutoSize = true };
    private readonly Button _later = new() { Text = "Download Later", AutoSize = true };
    private bool _userPickedCategory, _userPickedFolder, _filling;

    public event Action<DownloadItem>? Added;

    public AddForm(DownloadManager manager, DownloadRequest? initial)
    {
        _manager = manager;
        _initial = initial;
        Text = "Download File Info";
        Icon = Platform.AppIcon;
        Font = SystemFonts.MessageBoxFont ?? Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(580, 330);
        TopMost = initial is not null; // from the browser: come to the front

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(string label, Control control)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 6, 0, 0) });
            grid.Controls.Add(control);
        }
        var browse = new Button { Text = "…", Width = 32, Dock = DockStyle.Right };
        var folderRow = new Panel { Dock = DockStyle.Fill, Height = 26 };
        folderRow.Controls.Add(_folder);
        folderRow.Controls.Add(browse);
        _category.Items.AddRange(Enum.GetNames<FileCategory>());

        Row("URL", _url);
        Row("Category", _category);
        Row("Save to", folderRow);
        Row("File name", _name);
        Row("", _remember);
        Row("Description", _description);
        _duplicateChoice.Controls.AddRange([_rename, _replace]);
        Row("", _warning);
        Row("", _duplicateChoice);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 46, Padding = new Padding(10, 8, 10, 6) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange([_start, _later, cancel]);
        Controls.Add(grid);
        Controls.Add(buttons);
        AcceptButton = _start;
        CancelButton = cancel;

        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = _folder.Text, UseDescriptionForTitle = true, Description = "Save downloads to" };
            if (dialog.ShowDialog(this) == DialogResult.OK) { _folder.Text = dialog.SelectedPath; _userPickedFolder = true; }
        };
        _url.TextChanged += (_, _) => Guess();
        _name.TextChanged += (_, _) => Guess();
        _folder.TextChanged += (_, _) => UpdateDuplicate();
        _category.SelectedIndexChanged += (_, _) =>
        {
            if (_filling) return;
            _userPickedCategory = true;
            if (!_userPickedFolder) _folder.Text = _manager.Settings.FolderFor(SelectedCategory);
        };
        _start.Click += (_, _) => Submit(true);
        _later.Click += (_, _) => Submit(false);
        cancel.Click += (_, _) => Close();

        Fill();
    }

    private FileCategory SelectedCategory => Enum.Parse<FileCategory>((string)(_category.SelectedItem ?? nameof(FileCategory.General)));

    private Uri? Url => Uri.TryCreate(_url.Text.Trim(), UriKind.Absolute, out var url) && url.Scheme is "http" or "https" ? url : null;

    private bool IsStream => _initial?.IsStream == true || (Url is { } u && (u.AbsolutePath.EndsWith(".m3u8") || u.AbsolutePath.EndsWith(".mpd")));

    private void Fill()
    {
        if (_initial is not null)
        {
            _url.Text = _initial.Url.ToString();
            _name.Text = _initial.FileName ?? "";
        }
        else if (Clipboard.ContainsText() && Clipboard.GetText().Trim() is var clip && clip.StartsWith("http") && !clip.Contains(' '))
        {
            _url.Text = clip;
        }
        Guess();
    }

    /// <summary>Picks the category and folder from the file name until the user chooses them.</summary>
    private void Guess()
    {
        var category = IsStream ? FileCategory.Video : Categories.Of(PlannedName);
        _filling = true;
        if (!_userPickedCategory) _category.SelectedItem = category.ToString();
        _filling = false;
        if (!_userPickedFolder) _folder.Text = _manager.Settings.FolderFor(SelectedCategory);
        _remember.Text = $"Remember this folder for the \"{SelectedCategory}\" category";
        _start.Enabled = _later.Enabled = Url is not null;
        UpdateDuplicate();
    }

    /// <summary>The name the file will get, as far as it can be known before the server answers.</summary>
    private string PlannedName
    {
        get
        {
            var given = Names.Sanitize(_name.Text.Trim());
            var name = given ?? Names.Sanitize(Uri.UnescapeDataString(Path.GetFileName(Url?.AbsolutePath ?? ""))) ?? "";
            return IsStream && name.Length > 0 && !name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(name) + ".mp4" : name;
        }
    }

    private bool FileExists => PlannedName.Length > 0 && Directory.Exists(_folder.Text) && File.Exists(Path.Combine(_folder.Text, PlannedName));

    private void UpdateDuplicate()
    {
        var listed = Url is { } url && _manager.HasAddress(url);
        var exists = FileExists;
        _warning.Visible = listed || exists;
        _duplicateChoice.Visible = exists;
        _warning.Text = (exists, listed) switch
        {
            (true, true) => $"⚠ This address is already in your downloads, and \"{PlannedName}\" exists in this folder.",
            (true, false) => $"⚠ \"{PlannedName}\" already exists in this folder.",
            _ => "⚠ This address is already in your downloads. It will be downloaded again as a separate file.",
        };
        if (exists) _rename.Text = $"Save as \"{Path.GetFileName(Names.UniquePath(_folder.Text, PlannedName, false))}\"";
    }

    private void Submit(bool startNow)
    {
        if (Url is not { } url) return;
        var folder = Environment.ExpandEnvironmentVariables(_folder.Text.Trim());
        try { Directory.CreateDirectory(folder); }
        catch (Exception error)
        {
            MessageBox.Show(this, $"Can't use that folder: {error.Message}", "WinDM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_remember.Checked)
        {
            var settings = OptionsForm.Clone(_manager.Settings);
            settings.CategoryFolders[SelectedCategory] = folder;
            _manager.ApplySettings(settings);
        }
        var request = (_initial ?? new DownloadRequest { Url = url }) with
        {
            Url = url,
            FileName = Names.Sanitize(_name.Text.Trim()),
        };
        var item = _manager.Add(request, folder, _description.Text.Trim(), startNow, toQueue: !startNow,
            replaceExisting: FileExists ? _replace.Checked : null);
        Added?.Invoke(item);
        Close();
    }
}

/// <summary>"Video tools": shown at first launch when ffmpeg is missing, and from Options.
/// Installs ffmpeg with winget only when the user clicks Install.</summary>
internal sealed class FFmpegForm : Form
{
    private readonly DownloadManager _manager;
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Top, Height = 40 };
    private readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font(FontFamily.GenericMonospace, 8.5f), Visible = false };
    private readonly Button _install = new() { Text = "Install", AutoSize = true };
    private readonly Button _close = new() { Text = "Not Now", AutoSize = true };
    private readonly CheckBox _dontShow = new() { Text = "Don't show this at startup", AutoSize = true };

    public FFmpegForm(DownloadManager manager)
    {
        _manager = manager;
        Text = "Video Tools";
        Icon = Platform.AppIcon;
        Font = SystemFonts.MessageBoxFont ?? Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(540, 330);
        Padding = new Padding(14);

        var intro = new Label
        {
            Dock = DockStyle.Top, Height = 54,
            Text = "WinDM uses the free ffmpeg tool to join video streams with their audio and save them as MP4. " +
                   "Without it, ordinary downloads still work, but streams are saved as .ts files, and streams with a " +
                   "separate audio track are saved as two files.",
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 6, 0, 0) };
        buttons.Controls.AddRange([_install, _close, _dontShow]);
        Controls.Add(_log);
        Controls.Add(_status);
        Controls.Add(intro);
        Controls.Add(buttons);

        _install.Click += async (_, _) => await InstallAsync();
        _close.Click += (_, _) => Close();
        FormClosed += (_, _) =>
        {
            if (!_dontShow.Checked) return;
            var settings = OptionsForm.Clone(_manager.Settings);
            settings.FFmpegPromptDismissed = true;
            _manager.ApplySettings(settings);
        };
        Refresh(null);
    }

    private void Refresh(string? failure)
    {
        if (FFmpeg.Find() is { } path)
        {
            _status.Text = $"✔ ffmpeg is installed ({path}).";
            _status.ForeColor = Color.SeaGreen;
            _install.Visible = false;
            _dontShow.Visible = false;
            _close.Text = "Done";
        }
        else
        {
            _status.Text = failure ?? "ffmpeg isn't installed. Click Install to add it with winget (Windows Package Manager); this takes a minute or two.";
            _status.ForeColor = failure is null ? SystemColors.ControlText : Color.Firebrick;
        }
    }

    private async Task InstallAsync()
    {
        _install.Enabled = false;
        _log.Visible = true;
        _status.Text = "Installing ffmpeg with winget…";
        var start = new ProcessStartInfo("winget")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "install", "--id", "Gyan.FFmpeg", "-e", "--accept-source-agreements", "--accept-package-agreements" })
            start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start)!;
            process.OutputDataReceived += (_, e) => { if (e.Data is { } line) BeginInvoke(() => _log.AppendText(line + Environment.NewLine)); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) BeginInvoke(() => _log.AppendText(line + Environment.NewLine)); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            Refresh(FFmpeg.Find() is null ? $"Installation didn't finish (winget exit code {process.ExitCode})." : null);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Refresh("winget isn't available. Install \"App Installer\" from the Microsoft Store, or download ffmpeg from ffmpeg.org and add it to PATH.");
        }
        _install.Enabled = true;
    }
}

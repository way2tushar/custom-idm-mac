using WinDM.Core;

namespace WinDM;

/// <summary>Main queue scheduler: when to start and stop, how many files at once, and what to do when done;
/// plus the list of files in the queue.</summary>
internal sealed class SchedulerForm : Form
{
    private readonly DownloadManager _manager;
    private readonly AppSettings _settings;
    private readonly CheckBox _startEnabled = new() { Text = "Start download at", AutoSize = true };
    private readonly DateTimePicker _start = TimePicker();
    private readonly RadioButton _once = new() { Text = "Once", AutoSize = true };
    private readonly RadioButton _daily = new() { Text = "Daily on:", AutoSize = true };
    private readonly Dictionary<DayOfWeek, CheckBox> _days = [];
    private readonly CheckBox _stopEnabled = new() { Text = "Stop download at", AutoSize = true };
    private readonly DateTimePicker _stop = TimePicker();
    private readonly NumericUpDown _concurrent = new() { Minimum = 1, Maximum = 10, Width = 60 };
    private readonly ComboBox _whenDone = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly Label _state = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 8, 0, 0) };
    private readonly ListView _queue = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };

    private static DateTimePicker TimePicker() => new() { Format = DateTimePickerFormat.Time, ShowUpDown = true, Width = 110 };

    public SchedulerForm(DownloadManager manager)
    {
        _manager = manager;
        _settings = OptionsForm.Clone(manager.Settings);
        Text = "Scheduler";
        Icon = Platform.AppIcon;
        Font = SystemFonts.MessageBoxFont ?? Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(540, 440);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(ScheduleTab());
        tabs.TabPages.Add(QueueTab());
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 8, 8, 4) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };
        var startNow = new Button { Text = "Start Now", AutoSize = true };
        var stopNow = new Button { Text = "Stop", AutoSize = true };
        buttons.Controls.AddRange([cancel, ok, stopNow, startNow]);
        Controls.Add(tabs);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        startNow.Click += (_, _) => { Save(); _manager.StartQueue(); UpdateState(); };
        stopNow.Click += (_, _) => { _manager.StopQueue(); UpdateState(); };
        ok.Click += (_, _) => Save();
        Load();
        UpdateState();
    }

    private TabPage ScheduleTab()
    {
        var page = new TabPage("Schedule") { Padding = new Padding(12) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        FlowLayoutPanel Row(params Control[] controls)
        {
            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
            row.Controls.AddRange(controls);
            return row;
        }
        var days = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(20, 0, 0, 6) };
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            var box = new CheckBox { Text = day.ToString()[..3], AutoSize = true };
            _days[day] = box;
            days.Controls.Add(box);
        }
        _whenDone.Items.AddRange(["Do nothing", "Exit WinDM", "Put the computer to sleep"]);

        flow.Controls.AddRange(
        [
            Row(_startEnabled, _start),
            Row(_once, _daily),
            days,
            Row(_stopEnabled, _stop),
            Row(new Label { Text = "Download", AutoSize = true, Margin = new Padding(0, 6, 4, 0) }, _concurrent,
                new Label { Text = "files at the same time", AutoSize = true, Margin = new Padding(4, 6, 0, 0) }),
            Row(new Label { Text = "When done:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) }, _whenDone),
            _state,
        ]);
        page.Controls.Add(flow);

        void Sync()
        {
            _start.Enabled = _once.Enabled = _daily.Enabled = _startEnabled.Checked;
            days.Enabled = _startEnabled.Checked && _daily.Checked;
            _stop.Enabled = _stopEnabled.Checked;
        }
        _startEnabled.CheckedChanged += (_, _) => Sync();
        _daily.CheckedChanged += (_, _) => Sync();
        _stopEnabled.CheckedChanged += (_, _) => Sync();
        HandleCreated += (_, _) => Sync();
        return page;
    }

    private TabPage QueueTab()
    {
        var page = new TabPage("Files in the queue") { Padding = new Padding(12) };
        _queue.Columns.Add("File Name", 300);
        _queue.Columns.Add("Status", 100);
        var remove = new Button { Text = "Remove from Queue", AutoSize = true, Dock = DockStyle.Bottom };
        remove.Click += (_, _) =>
        {
            foreach (ListViewItem row in _queue.SelectedItems)
                if (_manager.Find((Guid)row.Tag!) is { } item) _manager.SetQueued(item, false);
            FillQueue();
        };
        page.Controls.Add(_queue);
        page.Controls.Add(remove);
        FillQueue();
        return page;
    }

    private void FillQueue()
    {
        _queue.Items.Clear();
        foreach (var item in _manager.Items.Where(i => i.InQueue))
            _queue.Items.Add(new ListViewItem([item.DisplayName, item.Snapshot.Status.ToString()]) { Tag = item.Id });
    }

    private static DateTime AtMinute(int minute) => DateTime.Today.AddMinutes(minute);
    private static int MinuteOf(DateTime time) => time.Hour * 60 + time.Minute;

    private new void Load()
    {
        _startEnabled.Checked = _settings.SchedulerEnabled;
        _start.Value = AtMinute(_settings.SchedulerStartMinute);
        _daily.Checked = _settings.SchedulerDaily;
        _once.Checked = !_settings.SchedulerDaily;
        foreach (var (day, box) in _days) box.Checked = _settings.SchedulerDays.Contains(day);
        _stopEnabled.Checked = _settings.SchedulerStopEnabled;
        _stop.Value = AtMinute(_settings.SchedulerStopMinute);
        _concurrent.Value = Math.Clamp(_settings.QueueMaxConcurrent, 1, 10);
        _whenDone.SelectedIndex = (int)_settings.SchedulerWhenDone;
    }

    private void Save()
    {
        _settings.SchedulerEnabled = _startEnabled.Checked;
        _settings.SchedulerStartMinute = MinuteOf(_start.Value);
        _settings.SchedulerDaily = _daily.Checked;
        _settings.SchedulerDays = _days.Where(d => d.Value.Checked).Select(d => d.Key).ToList();
        _settings.SchedulerStopEnabled = _stopEnabled.Checked;
        _settings.SchedulerStopMinute = MinuteOf(_stop.Value);
        _settings.QueueMaxConcurrent = (int)_concurrent.Value;
        _settings.SchedulerWhenDone = (WhenQueueDone)Math.Max(0, _whenDone.SelectedIndex);
        _manager.ApplySettings(_settings);
    }

    private void UpdateState() =>
        _state.Text = _manager.QueueRunning ? "The main queue is running." : "The main queue is stopped.";
}

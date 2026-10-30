using System.Text.Json;
using WinDM.Engine;

namespace WinDM.Core;

public sealed class DownloadItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required DownloadRequest Request { get; init; }
    public DateTime AddedAt { get; init; } = DateTime.Now;
    public required string Folder { get; set; }
    public string Description { get; set; } = "";
    public DateTime? LastTry { get; set; }
    public bool InQueue { get; set; }
    /// <summary>Per-download limit in KB/s; null uses the global limiter.</summary>
    public int? SpeedLimitKB { get; set; }
    public bool ShowCompleteDialog { get; set; } = true;
    public bool OpenWhenDone { get; set; }
    public bool SleepWhenDone { get; set; }
    /// <summary>Chosen in the add dialog when the file already exists; null follows Options.</summary>
    public bool? ReplaceExisting { get; set; }
    public ProgressSnapshot Snapshot { get; set; } = new();

    internal IDownloadJob? Job;

    public string DisplayName => Snapshot.FileName ?? Request.FileName ?? Path.GetFileName(Request.Url.AbsolutePath);
    public FileCategory Category => Request.IsStream ? FileCategory.Video : Categories.Of(DisplayName);
    public bool IsFinished => Snapshot.Status == DownloadStatus.Completed;
    public bool IsActive => Snapshot.Status is DownloadStatus.Probing or DownloadStatus.Downloading;

    public double? TimeLeftSeconds =>
        IsActive && Snapshot.TotalBytes is long total && Snapshot.BytesPerSecond > 1
            ? (total - Snapshot.DownloadedBytes) / Snapshot.BytesPerSecond : null;
}

/// <summary>
/// Owns the download list, the main queue and its scheduler, and persists everything.
/// Thread-safe: UI calls and engine progress callbacks both go through one lock.
/// </summary>
public sealed class DownloadManager
{
    private readonly object _gate = new();
    private readonly List<DownloadItem> _items = [];
    private readonly Timer _scheduler;
    private DateTime _lastScheduleCheck = DateTime.Now;

    public AppSettings Settings { get; private set; }
    public bool QueueRunning { get; private set; }

    /// <summary>A download finished (raised on a background thread).</summary>
    public event Action<DownloadItem>? Completed;
    /// <summary>The scheduler's "when done" action asks the app to exit or sleep.</summary>
    public event Action<WhenQueueDone>? QueueFinished;

    private static string StorePath => Path.Combine(AppSettings.DataFolder, "downloads.json");

    public DownloadManager(AppSettings settings)
    {
        Settings = settings;
        Http.Proxy = settings.ProxyAddress;
        Load();
        _scheduler = new Timer(_ => CheckSchedule(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
    }

    public IReadOnlyList<DownloadItem> Items { get { lock (_gate) return _items.ToList(); } }

    public DownloadItem? Find(Guid id) { lock (_gate) return _items.FirstOrDefault(i => i.Id == id); }

    // ----- Adding -----

    public DownloadItem Add(DownloadRequest request, string? folder = null, string description = "",
                            bool startNow = true, bool toQueue = false, bool? replaceExisting = null)
    {
        var name = request.FileName ?? Path.GetFileName(request.Url.AbsolutePath);
        var item = new DownloadItem
        {
            Request = request,
            Folder = folder ?? Settings.FolderFor(request.IsStream ? FileCategory.Video : Categories.Of(name)),
            Description = description,
            InQueue = toQueue,
            ShowCompleteDialog = Settings.ShowCompleteDialog,
            ReplaceExisting = replaceExisting,
            Snapshot = new ProgressSnapshot { FileName = request.FileName, Status = startNow ? DownloadStatus.Queued : DownloadStatus.Paused },
        };
        lock (_gate) _items.Insert(0, item);
        Save();
        if (startNow) Schedule();
        return item;
    }

    public bool HasAddress(Uri url) { lock (_gate) return _items.Any(i => i.Request.Url == url); }

    // ----- Actions -----

    public void Resume(DownloadItem item)
    {
        lock (_gate)
        {
            if (item.Snapshot.Status is not (DownloadStatus.Paused or DownloadStatus.Failed)) return;
            item.Snapshot = item.Snapshot with { Status = DownloadStatus.Queued, Error = null };
        }
        Schedule();
    }

    public void Stop(DownloadItem item)
    {
        IDownloadJob? job;
        lock (_gate)
        {
            if (item.Snapshot.Status == DownloadStatus.Queued)
            {
                item.Snapshot = item.Snapshot with { Status = DownloadStatus.Paused };
                return;
            }
            job = item.Job;
        }
        job?.Pause();
    }

    public void StopAll()
    {
        QueueRunning = false;
        foreach (var item in Items) Stop(item);
    }

    public void Redownload(DownloadItem item)
    {
        lock (_gate)
        {
            item.Job?.Cancel();
            item.Job = null;
            if (item.Snapshot.FilePath is { } file) try { File.Delete(file); } catch { }
            item.Snapshot = new ProgressSnapshot { FileName = item.Snapshot.FileName, Status = DownloadStatus.Queued };
        }
        Schedule();
    }

    public void Remove(DownloadItem item, bool deleteFile = false)
    {
        lock (_gate)
        {
            if (item.IsFinished)
            {
                if (deleteFile && item.Snapshot.FilePath is { } file) try { File.Delete(file); } catch { }
            }
            else
            {
                (item.Job ?? CreateJob(item)).Cancel();
            }
            _items.Remove(item);
        }
        Save();
        Schedule();
    }

    public void DeleteCompleted()
    {
        lock (_gate) _items.RemoveAll(i => i.IsFinished);
        Save();
    }

    public void SetQueued(DownloadItem item, bool queued)
    {
        lock (_gate) item.InQueue = queued;
        Save();
    }

    public void SetSpeedLimit(DownloadItem item, int? kilobytes)
    {
        lock (_gate)
        {
            item.SpeedLimitKB = kilobytes;
            item.Job?.SetSpeedLimit(EffectiveLimit(item));
        }
        Save();
    }

    /// <summary>Re-applies options to running jobs after the Options dialog saves.</summary>
    public void ApplySettings(AppSettings settings)
    {
        Settings = settings;
        settings.Save();
        Http.Proxy = settings.ProxyAddress;
        lock (_gate) foreach (var item in _items) item.Job?.SetSpeedLimit(EffectiveLimit(item));
        Schedule();
    }

    // ----- Queue and scheduler -----

    public void StartQueue()
    {
        QueueRunning = true;
        lock (_gate)
            foreach (var item in _items.Where(i => i.InQueue && i.Snapshot.Status is DownloadStatus.Paused or DownloadStatus.Failed))
                item.Snapshot = item.Snapshot with { Status = DownloadStatus.Queued, Error = null };
        Schedule();
    }

    public void StopQueue()
    {
        QueueRunning = false;
        foreach (var item in Items.Where(i => i.InQueue)) Stop(item);
    }

    private void CheckSchedule()
    {
        var now = DateTime.Now;
        var previous = _lastScheduleCheck;
        _lastScheduleCheck = now;
        var s = Settings;
        if (!s.SchedulerEnabled) return;
        bool Crossed(int minute) { var target = now.Date.AddMinutes(minute); return previous < target && target <= now; }
        if (!QueueRunning && (!s.SchedulerDaily || s.SchedulerDays.Contains(now.DayOfWeek)) && Crossed(s.SchedulerStartMinute)) StartQueue();
        if (QueueRunning && s.SchedulerStopEnabled && Crossed(s.SchedulerStopMinute)) StopQueue();
    }

    /// <summary>Starts queued downloads. Ones added directly start at once; the main queue runs only
    /// while started, up to its own simultaneous-download limit.</summary>
    private void Schedule()
    {
        var finishedQueue = false;
        lock (_gate)
        {
            var slots = Math.Max(1, Settings.QueueMaxConcurrent) - _items.Count(i => i.InQueue && i.IsActive);
            foreach (var item in Enumerable.Reverse(_items).Where(i => i.Snapshot.Status == DownloadStatus.Queued).ToList())
            {
                if (item.InQueue)
                {
                    if (!QueueRunning || slots <= 0) continue;
                    slots--;
                }
                Begin(item);
            }
            if (QueueRunning && !_items.Any(i => i.InQueue && i.Snapshot.Status is DownloadStatus.Queued or DownloadStatus.Probing or DownloadStatus.Downloading))
            {
                QueueRunning = false;
                finishedQueue = Settings.SchedulerEnabled;
            }
        }
        if (finishedQueue && Settings.SchedulerWhenDone != WhenQueueDone.Nothing) QueueFinished?.Invoke(Settings.SchedulerWhenDone);
    }

    private void Begin(DownloadItem item)
    {
        var job = item.Job ?? CreateJob(item);
        job.MaxConnections = Math.Max(1, Settings.Connections);
        job.ReplaceExisting = item.ReplaceExisting ?? Settings.Duplicates == DuplicateAction.Overwrite;
        job.SetSpeedLimit(EffectiveLimit(item));
        item.Job = job;
        item.LastTry = DateTime.Now;
        item.Snapshot = item.Snapshot with { Status = DownloadStatus.Probing };
        job.Start();
    }

    private long? EffectiveLimit(DownloadItem item) =>
        item.SpeedLimitKB is int kb ? kb * 1024L : Settings.SpeedLimitBytes;

    private IDownloadJob CreateJob(DownloadItem item)
    {
        IDownloadJob job = item.Request.IsStream
            ? new StreamDownload(item.Id, item.Request, item.Folder, Settings.Connections)
            : new SegmentedDownload(item.Id, item.Request, item.Folder, Settings.Connections);
        job.Progress += snapshot =>
        {
            bool changed;
            lock (_gate)
            {
                changed = item.Snapshot.Status != snapshot.Status;
                item.Snapshot = snapshot;
            }
            if (!changed) return;
            Save();
            if (snapshot.Status == DownloadStatus.Completed) Completed?.Invoke(item);
            Schedule();
        };
        return job;
    }

    // ----- Persistence -----

    private sealed record Record(Guid Id, DownloadRequest Request, DateTime AddedAt, string Folder, string Description,
        DateTime? LastTry, bool InQueue, int? SpeedLimitKB, bool ShowCompleteDialog, DownloadStatus Status,
        string? FileName, long? TotalBytes, long DownloadedBytes, string? FilePath, string? Error, int PartsDone, int PartsTotal);

    public void Save()
    {
        List<Record> records;
        lock (_gate)
            records = _items.Select(i => new Record(i.Id, i.Request, i.AddedAt, i.Folder, i.Description, i.LastTry, i.InQueue,
                i.SpeedLimitKB, i.ShowCompleteDialog, i.Snapshot.Status, i.Snapshot.FileName, i.Snapshot.TotalBytes,
                i.Snapshot.DownloadedBytes, i.Snapshot.FilePath, i.Snapshot.Error, i.Snapshot.PartsDone, i.Snapshot.PartsTotal)).ToList();
        try
        {
            Directory.CreateDirectory(AppSettings.DataFolder);
            File.WriteAllText(StorePath + ".tmp", JsonSerializer.Serialize(records, AppSettings.Json));
            File.Move(StorePath + ".tmp", StorePath, overwrite: true);
        }
        catch { /* best effort */ }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var records = JsonSerializer.Deserialize<List<Record>>(File.ReadAllText(StorePath), AppSettings.Json) ?? [];
            foreach (var r in records)
                _items.Add(new DownloadItem
                {
                    Id = r.Id, Request = r.Request, AddedAt = r.AddedAt, Folder = r.Folder, Description = r.Description,
                    LastTry = r.LastTry, InQueue = r.InQueue, SpeedLimitKB = r.SpeedLimitKB, ShowCompleteDialog = r.ShowCompleteDialog,
                    // Anything interrupted by quitting comes back stopped; its saved progress resumes it.
                    Snapshot = new ProgressSnapshot
                    {
                        Status = r.Status is DownloadStatus.Queued or DownloadStatus.Probing or DownloadStatus.Downloading ? DownloadStatus.Paused : r.Status,
                        FileName = r.FileName, TotalBytes = r.TotalBytes, DownloadedBytes = r.DownloadedBytes,
                        FilePath = r.FilePath, Error = r.Error, PartsDone = r.PartsDone, PartsTotal = r.PartsTotal, Resumable = true,
                    },
                });
        }
        catch { /* a damaged list starts empty */ }
    }

    public void PauseAllForExit()
    {
        foreach (var item in Items.Where(i => i.IsActive)) item.Job?.Pause();
        Save();
    }
}

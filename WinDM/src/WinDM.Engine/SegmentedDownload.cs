using System.Net;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace WinDM.Engine;

/// <summary>
/// Multi-connection download with dynamic segmentation: the file starts as N equal ranges, and whenever
/// a connection finishes its range it takes over the second half of the largest range still in progress.
/// Progress is saved next to a sparse .part file so a paused or interrupted download resumes.
/// </summary>
public sealed class SegmentedDownload : IDownloadJob
{
    private const long MinimumSplit = 512 * 1024;
    private const int MaxRetries = 8;

    private sealed class Segment
    {
        public long Start;
        public long End; // inclusive; long.MaxValue = until the stream ends
        public long Downloaded;
        public bool Active;
        public string Info = "Idle";
        public long Position => Start + Downloaded;
        public long Remaining => End == long.MaxValue ? long.MaxValue : Math.Max(0, End - Position + 1);
        public bool Done => End != long.MaxValue && Position > End;
    }

    private sealed record SavedSegment(long Start, long End, long Downloaded);
    private sealed record SavedState(string Url, long Total, string? ETag, string? LastModified, string FileName, List<SavedSegment> Segments);

    public Guid Id { get; }
    public DownloadRequest Request { get; }
    public string Directory { get; }
    public int MaxConnections { get; set; }
    public bool ReplaceExisting { get; set; }
    public event Action<ProgressSnapshot>? Progress;

    private readonly object _gate = new();
    private readonly SpeedLimiter _limiter = new();
    private List<Segment> _segments = [];
    private ProbeResult? _probe;
    private DownloadStatus _status = DownloadStatus.Queued;
    private CancellationTokenSource? _cts;
    private Task? _run;
    private long _bytesThisTick;
    private double _speed;
    private string? _finalPath;
    private string? _error;

    private string WorkDirectory => Path.Combine(Directory, ".windm");
    private string PartPath => Path.Combine(WorkDirectory, $"{Id}.part");
    private string StatePath => Path.Combine(WorkDirectory, $"{Id}.json");

    public SegmentedDownload(Guid id, DownloadRequest request, string directory, int maxConnections = 8)
    {
        Id = id;
        Request = request;
        Directory = directory;
        MaxConnections = Math.Max(1, maxConnections);
    }

    public void SetSpeedLimit(long? bytesPerSecond) => _limiter.Limit = bytesPerSecond;

    public void Start()
    {
        lock (_gate)
        {
            if (_status is DownloadStatus.Probing or DownloadStatus.Downloading or DownloadStatus.Completed) return;
            _status = DownloadStatus.Probing;
            _error = null;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _run = Task.Run(() => RunAsync(token));
        }
        Emit();
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_status is not (DownloadStatus.Probing or DownloadStatus.Downloading)) return;
            _status = DownloadStatus.Paused;
            _cts?.Cancel();
        }
        Emit();
    }

    /// <summary>Stops the download and deletes its partial data.</summary>
    public void Cancel()
    {
        Task? run;
        lock (_gate)
        {
            _status = DownloadStatus.Failed;
            _error = "Cancelled";
            _cts?.Cancel();
            run = _run;
        }
        void Delete()
        {
            try { File.Delete(PartPath); } catch { }
            try { File.Delete(StatePath); } catch { }
        }
        if (run is null) Delete(); else run.ContinueWith(_ => Delete(), TaskScheduler.Default);
        Emit();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var http = Http.Create();
        using var ticker = new CancellationTokenSource();
        var tick = TickAsync(ticker.Token);
        try
        {
            var probe = await Probe.RunAsync(http, Request, ct).ConfigureAwait(false);
            System.IO.Directory.CreateDirectory(WorkDirectory);
            lock (_gate)
            {
                _probe = probe;
                _segments = Restore(probe) ?? Fresh(probe);
            }
            using (var handle = File.OpenHandle(PartPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
            {
                if (probe.AcceptsRanges && probe.TotalBytes is long total) RandomAccess.SetLength(handle, total);
                lock (_gate) { if (_status == DownloadStatus.Probing) _status = DownloadStatus.Downloading; }

                // One failure stops every connection.
                using var failure = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var connections = probe.AcceptsRanges ? MaxConnections : 1;
                var workers = Enumerable.Range(0, connections).Select(_ => WorkerAsync(http, handle, failure)).ToArray();
                await Task.WhenAll(workers).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                lock (_gate)
                {
                    if (!_segments.All(s => s.Done)) throw new DownloadException("Download ended before all data arrived");
                    if (probe.TotalBytes is null) RandomAccess.SetLength(handle, _segments[0].Position);
                }
            }
            Finish(probe);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SaveState(); // paused or cancelled; status already set
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                _status = DownloadStatus.Failed;
                _error = (error as AggregateException)?.InnerException?.Message ?? error.Message;
            }
            SaveState();
        }
        finally
        {
            ticker.Cancel();
            try { await tick.ConfigureAwait(false); } catch (OperationCanceledException) { }
            Emit();
        }
    }

    private List<Segment> Fresh(ProbeResult probe)
    {
        try { File.Delete(PartPath); } catch { }
        if (!probe.AcceptsRanges || probe.TotalBytes is not long total || total <= 0)
            return [new Segment { Start = 0, End = probe.TotalBytes is long known ? known - 1 : long.MaxValue }];
        var count = (int)Math.Max(1, Math.Min(MaxConnections, total / MinimumSplit));
        var size = total / count;
        return Enumerable.Range(0, count).Select(i => new Segment
        {
            Start = i * size,
            End = i == count - 1 ? total - 1 : (i + 1) * size - 1,
        }).ToList();
    }

    /// <summary>Saved progress is reused only if the server still describes the same file.</summary>
    private List<Segment>? Restore(ProbeResult probe)
    {
        try
        {
            if (!probe.AcceptsRanges || !File.Exists(StatePath) || !File.Exists(PartPath)) return null;
            var state = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(StatePath));
            if (state is null || state.Total != probe.TotalBytes) return null;
            if (probe.ETag is not null && state.ETag != probe.ETag) return null;
            if (probe.LastModified is not null && state.LastModified != probe.LastModified) return null;
            return state.Segments.Select(s => new Segment { Start = s.Start, End = s.End, Downloaded = s.Downloaded }).ToList();
        }
        catch
        {
            return null;
        }
    }

    // ----- Connections -----

    private async Task WorkerAsync(HttpClient http, SafeFileHandle handle, CancellationTokenSource failure)
    {
        try
        {
            while (NextSegment() is { } segment)
                await DownloadAsync(http, handle, segment, failure.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!failure.IsCancellationRequested)
        {
            failure.Cancel();
            throw;
        }
    }

    /// <summary>An idle unfinished range, or the back half of the largest range still downloading.</summary>
    private Segment? NextSegment()
    {
        lock (_gate)
        {
            if (_segments.FirstOrDefault(s => !s.Active && !s.Done) is { } idle)
            {
                idle.Active = true;
                return idle;
            }
            if (_probe?.AcceptsRanges != true) return null;
            var largest = _segments.Where(s => s.Active && !s.Done).MaxBy(s => s.Remaining);
            if (largest is null || largest.Remaining < 2 * MinimumSplit) return null;
            var middle = largest.Position + largest.Remaining / 2;
            var back = new Segment { Start = middle, End = largest.End, Active = true };
            largest.End = middle - 1; // its connection stops when it reaches the new end
            _segments.Insert(_segments.IndexOf(largest) + 1, back);
            return back;
        }
    }

    private async Task DownloadAsync(HttpClient http, SafeFileHandle handle, Segment segment, CancellationToken ct)
    {
        var probe = _probe!;
        var buffer = new byte[64 * 1024];
        for (var attempt = 0; ; )
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                long from, to;
                lock (_gate)
                {
                    if (!probe.AcceptsRanges) segment.Downloaded = 0; // no ranges: a retry starts over
                    from = segment.Position;
                    to = segment.End;
                    segment.Info = "Connecting…";
                }
                using var message = probe.AcceptsRanges
                    ? Http.Get(probe.FinalUrl, Request, from, to == long.MaxValue ? null : to)
                    : Http.Get(probe.FinalUrl, Request);
                using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (probe.AcceptsRanges && response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.PartialContent)
                    throw new DownloadException("Server stopped honoring byte ranges");
                Http.EnsureSuccess(response, ranged: false);
                lock (_gate) segment.Info = "Receiving data…";

                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                while (true)
                {
                    long allowed, position;
                    lock (_gate) { allowed = segment.Remaining; position = segment.Position; }
                    if (allowed <= 0) break; // reached its end, possibly moved by a split
                    var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, allowed)), ct).ConfigureAwait(false);
                    if (read == 0) break;
                    RandomAccess.Write(handle, buffer.AsSpan(0, read), position);
                    lock (_gate)
                    {
                        segment.Downloaded += read;
                        _bytesThisTick += read;
                    }
                    attempt = 0;
                    await _limiter.ThrottleAsync(read, ct).ConfigureAwait(false);
                }

                lock (_gate)
                {
                    if (segment.End == long.MaxValue) segment.End = segment.Position - 1; // unknown length: stream end = done
                    if (!segment.Done) throw new IOException("Connection closed early");
                    segment.Info = "Completed";
                    segment.Active = false;
                }
                return;
            }
            catch (Exception error) when (error is not OperationCanceledException and not DownloadException)
            {
                attempt++;
                if (attempt > MaxRetries) throw new DownloadException($"Gave up after repeated failures: {error.Message}", error);
                lock (_gate) segment.Info = $"Connection lost, retry {attempt}…";
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << (attempt - 1))), ct).ConfigureAwait(false);
            }
        }
    }

    // ----- Completion and progress -----

    private void Finish(ProbeResult probe)
    {
        var destination = Names.UniquePath(Directory, probe.FileName, ReplaceExisting);
        File.Move(PartPath, destination);
        try { File.Delete(StatePath); } catch { }
        lock (_gate)
        {
            _finalPath = destination;
            _status = DownloadStatus.Completed;
        }
    }

    private void SaveState()
    {
        SavedState? state;
        lock (_gate)
        {
            if (_probe is not { AcceptsRanges: true, TotalBytes: long total } probe) return;
            state = new SavedState(probe.FinalUrl.ToString(), total, probe.ETag, probe.LastModified, probe.FileName,
                _segments.Select(s => new SavedSegment(s.Start, s.End, s.Downloaded)).ToList());
        }
        try
        {
            var temporary = StatePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, StatePath, overwrite: true);
        }
        catch { /* best effort; the next save tries again */ }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        for (var ticks = 1; ; ticks++)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            lock (_gate)
            {
                var instant = _bytesThisTick / 0.5;
                _speed = _speed == 0 ? instant : _speed * 0.7 + instant * 0.3;
                _bytesThisTick = 0;
            }
            if (ticks % 4 == 0) SaveState();
            Emit();
        }
    }

    private void Emit()
    {
        ProgressSnapshot snapshot;
        lock (_gate)
        {
            snapshot = new ProgressSnapshot
            {
                Status = _status,
                FileName = _finalPath is not null ? Path.GetFileName(_finalPath) : _probe?.FileName,
                TotalBytes = _probe?.TotalBytes,
                DownloadedBytes = _segments.Sum(s => s.Downloaded),
                BytesPerSecond = _status == DownloadStatus.Downloading ? _speed : 0,
                Segments = _segments.Select(s => new SegmentInfo(s.Start, s.End, s.Downloaded, s.Active, s.Done ? "Completed" : s.Info)).ToList(),
                ActiveConnections = _segments.Count(s => s.Active && !s.Done),
                Resumable = _probe?.AcceptsRanges ?? false,
                FilePath = _finalPath,
                Error = _error,
            };
            if (_status != DownloadStatus.Downloading) _speed = 0;
        }
        Progress?.Invoke(snapshot);
    }
}

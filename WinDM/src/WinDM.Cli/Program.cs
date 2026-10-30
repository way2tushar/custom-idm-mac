using WinDM.Core;
using WinDM.Engine;

// Usage:
//   windm-cli <url> [--connections N] [--output DIR] [--id GUID] [--variant BANDWIDTH] [--limit KBPS]
//   windm-cli --bridge      run the browser bridge and download manager without a window (for testing)
// Passing the same --id again resumes an interrupted download.

var arguments = args.ToList();
string? Option(string name)
{
    var index = arguments.IndexOf(name);
    if (index < 0 || index + 1 >= arguments.Count) return null;
    var value = arguments[index + 1];
    arguments.RemoveRange(index, 2);
    return value;
}

if (arguments.Remove("--bridge"))
{
    var settings = AppSettings.Load();
    settings.ShowStartDialog = false;
    var manager = new DownloadManager(settings);
    var bridge = new Bridge(() => manager.Settings);
    bridge.DownloadRequested += request =>
    {
        var item = manager.Add(request);
        Console.WriteLine($"added {request.Kind} {request.Url} -> {item.Folder}");
    };
    manager.Completed += item => Console.WriteLine($"completed {item.Snapshot.FilePath}");
    if (!bridge.Start()) { Console.Error.WriteLine($"port {Bridge.Port} is in use"); return 1; }
    Console.WriteLine($"bridge listening on 127.0.0.1:{Bridge.Port}; Ctrl+C to stop");
    var stop = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
    await stop.Task;
    manager.PauseAllForExit();
    return 0;
}

var connections = int.TryParse(Option("--connections"), out var c) ? c : 8;
var output = Option("--output") ?? Directory.GetCurrentDirectory();
var id = Guid.TryParse(Option("--id"), out var g) ? g : Guid.NewGuid();
var variant = long.TryParse(Option("--variant"), out var v) ? v : (long?)null;
var limit = long.TryParse(Option("--limit"), out var l) ? l * 1024 : (long?)null;
if (arguments.Count == 0 || !Uri.TryCreate(arguments[0], UriKind.Absolute, out var url))
{
    Console.Error.WriteLine("usage: windm-cli <url> [--connections N] [--output DIR] [--id GUID] [--variant BANDWIDTH] [--limit KBPS]");
    return 2;
}

Console.WriteLine($"id {id}");
var request = new DownloadRequest { Url = url, VariantBandwidth = variant };
IDownloadJob job = request.IsStream
    ? new StreamDownload(id, request, output, connections)
    : new SegmentedDownload(id, request, output, connections);
job.SetSpeedLimit(limit);
var done = new TaskCompletionSource<int>();
job.Progress += s =>
{
    var percent = s.Fraction is double f ? $"{f * 100,5:F1}%" : "  ?  ";
    Console.WriteLine($"{s.Status} {percent} {s.BytesPerSecond / 1048576:F2} MB/s  conns {s.ActiveConnections}  segs {s.Segments.Count}  parts {s.PartsDone}/{s.PartsTotal}  {s.FileName}");
    if (s.Status == DownloadStatus.Completed) { Console.WriteLine($"saved {s.FilePath}"); done.TrySetResult(0); }
    if (s.Status == DownloadStatus.Failed) { Console.WriteLine($"error {s.Error}"); done.TrySetResult(1); }
    if (s.Status == DownloadStatus.Paused) done.TrySetResult(130);
};
Console.CancelKeyPress += (_, e) => { e.Cancel = true; job.Pause(); };
// Scripts send SIGTERM (background jobs ignore Ctrl+C); pause so progress is saved for resuming.
using var term = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; job.Pause(); });
job.Start();
return await done.Task;

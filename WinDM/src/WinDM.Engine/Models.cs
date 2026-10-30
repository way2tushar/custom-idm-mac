namespace WinDM.Engine;

public enum DownloadKind { File, Hls, Dash }

public enum DownloadStatus { Queued, Probing, Downloading, Paused, Completed, Failed }

/// <summary>What to download, plus the browser context needed to fetch it (cookies, referrer, user agent).</summary>
public sealed record DownloadRequest
{
    public required Uri Url { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? FileName { get; init; }
    public DownloadKind Kind { get; init; } = DownloadKind.File;
    /// <summary>For a multi-quality stream, the bandwidth of the chosen quality; null picks the best.</summary>
    public long? VariantBandwidth { get; init; }

    /// <summary>Streams are recognised by an explicit kind or a .m3u8 / .mpd path.</summary>
    public bool IsStream => Kind != DownloadKind.File ||
        Url.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
        Url.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase);
}

public sealed record SegmentInfo(long Start, long End, long Downloaded, bool Active, string Info);

public sealed record ProgressSnapshot
{
    public DownloadStatus Status { get; init; } = DownloadStatus.Queued;
    public string? FileName { get; init; }
    public long? TotalBytes { get; init; }
    public long DownloadedBytes { get; init; }
    public double BytesPerSecond { get; init; }
    public IReadOnlyList<SegmentInfo> Segments { get; init; } = [];
    public int ActiveConnections { get; init; }
    public bool Resumable { get; init; }
    public string? FilePath { get; init; }
    public string? Error { get; init; }
    /// <summary>Streams count progress in media segments, since their byte size is unknown up front.</summary>
    public int PartsDone { get; init; }
    public int PartsTotal { get; init; }

    public double? Fraction =>
        TotalBytes is > 0 ? Math.Min(1, (double)DownloadedBytes / TotalBytes.Value)
        : PartsTotal > 0 ? Math.Min(1, (double)PartsDone / PartsTotal)
        : null;
}

/// <summary>What the app needs from any download job, whether a file or a stream.</summary>
public interface IDownloadJob
{
    int MaxConnections { get; set; }
    bool ReplaceExisting { get; set; }
    /// <summary>Raised on a background thread; marshal to the UI thread before touching controls.</summary>
    event Action<ProgressSnapshot>? Progress;
    void Start();
    void Pause();
    void Cancel();
    void SetSpeedLimit(long? bytesPerSecond);
}

public sealed class DownloadException(string message, Exception? inner = null) : Exception(message, inner);

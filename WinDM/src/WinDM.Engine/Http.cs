using System.Net;
using System.Net.Http.Headers;

namespace WinDM.Engine;

/// <summary>Shared HTTP setup: browser headers, optional manual proxy, no cookie jar.</summary>
public static class Http
{
    /// <summary>Manual HTTP/HTTPS proxy ("host:port"); null uses the system proxy. Read when a job starts.</summary>
    public static string? Proxy { get; set; }

    public static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            // Cookies come from the browser in the Cookie header; a jar would replace them.
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = 64,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        if (!string.IsNullOrWhiteSpace(Proxy))
        {
            handler.Proxy = new WebProxy($"http://{Proxy}");
            handler.UseProxy = true;
        }
        // Default HTTP/1.1 keeps each range on its own TCP connection; HTTP/2 would multiplex them onto one.
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static HttpRequestMessage Get(Uri url, DownloadRequest request, long? from = null, long? to = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in request.Headers)
        {
            if (name.Equals("Range", StringComparison.OrdinalIgnoreCase)) continue;
            message.Headers.TryAddWithoutValidation(name, value);
        }
        if (from is not null) message.Headers.Range = new RangeHeaderValue(from, to);
        return message;
    }

    public static void EnsureSuccess(HttpResponseMessage response, bool ranged)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Server responded with HTTP {(int)response.StatusCode}");
        if (ranged && response.StatusCode != HttpStatusCode.PartialContent)
            throw new DownloadException("Server stopped honoring byte ranges");
    }
}

/// <summary>Token bucket shared by all connections of one download.</summary>
public sealed class SpeedLimiter
{
    private readonly object _gate = new();
    private long? _limit;
    private double _tokens;
    private long _last = Environment.TickCount64;

    public long? Limit
    {
        get { lock (_gate) return _limit; }
        set { lock (_gate) { _limit = value is > 0 ? value : null; _tokens = 0; } }
    }

    public async Task ThrottleAsync(int bytes, CancellationToken ct)
    {
        TimeSpan wait;
        lock (_gate)
        {
            if (_limit is not long limit) return;
            var now = Environment.TickCount64;
            // Refill, allowing at most a quarter second of burst.
            _tokens = Math.Min(limit / 4.0, _tokens + (now - _last) / 1000.0 * limit);
            _last = now;
            _tokens -= bytes;
            if (_tokens >= 0) return;
            wait = TimeSpan.FromSeconds(-_tokens / limit);
        }
        await Task.Delay(wait, ct).ConfigureAwait(false);
    }
}

public static class Names
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>A name that is valid on Windows (and macOS), or null when nothing usable is left.</summary>
    public static string? Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var invalid = new HashSet<char>("<>:\"/\\|?*");
        var chars = name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim().TrimEnd('.', ' ').TrimStart('.');
        if (cleaned.Length == 0) return null;
        if (Reserved.Contains(Path.GetFileNameWithoutExtension(cleaned))) cleaned = "_" + cleaned;
        return cleaned.Length > 200 ? cleaned[..200] : cleaned;
    }

    /// <summary>"name.ext", or "name (1).ext", "name (2).ext"… when taken; or the name itself when replacing.</summary>
    public static string UniquePath(string directory, string name, bool replace)
    {
        var candidate = Path.Combine(directory, name);
        if (replace)
        {
            if (File.Exists(candidate)) File.Delete(candidate);
            return candidate;
        }
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var counter = 1; File.Exists(candidate); counter++)
            candidate = Path.Combine(directory, $"{stem} ({counter}){ext}");
        return candidate;
    }

    private static readonly Dictionary<string, string> MimeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["video/mp4"] = ".mp4", ["video/webm"] = ".webm", ["video/quicktime"] = ".mov", ["video/x-matroska"] = ".mkv",
        ["audio/mpeg"] = ".mp3", ["audio/mp4"] = ".m4a", ["audio/ogg"] = ".ogg", ["audio/wav"] = ".wav",
        ["application/pdf"] = ".pdf", ["application/zip"] = ".zip", ["application/x-7z-compressed"] = ".7z",
        ["application/vnd.rar"] = ".rar", ["application/x-rar-compressed"] = ".rar", ["application/gzip"] = ".gz",
        ["application/x-msdownload"] = ".exe", ["application/x-msi"] = ".msi", ["application/x-apple-diskimage"] = ".dmg",
        ["image/jpeg"] = ".jpg", ["image/png"] = ".png",
    };

    public static string WithExtension(string name, string? mimeType) =>
        Path.HasExtension(name) || mimeType is null || !MimeExtensions.TryGetValue(mimeType, out var ext) ? name : name + ext;
}

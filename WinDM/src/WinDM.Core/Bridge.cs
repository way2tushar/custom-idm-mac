using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinDM.Engine;

namespace WinDM.Core;

/// <summary>
/// Minimal HTTP/1.1 endpoint on 127.0.0.1:9614 for the browser extensions (same protocol as MacDM, so the
/// MacDM Chrome extension works unchanged with Chrome and Edge on Windows):
///
///   GET  /ping      -> {"app":"WinDM","capture":…,"grabber":…,"types":[…],"excludedSites":[…],"excludedAddresses":[…]}
///   GET  /show      -> brings the main window forward (used when WinDM is started a second time)
///   POST /download  {"url","filename","referrer","cookies","userAgent","kind":"file|hls|dash","variant","auto"}
///
/// Only loopback connections are accepted, and browser requests must come from an extension origin, so web
/// pages cannot push downloads. Automatic captures from excluded sites/addresses are answered 409.
/// A raw socket is used rather than HttpListener, which needs administrator rights for 127.0.0.1.
/// </summary>
public sealed class Bridge
{
    public const int Port = 9614;

    private readonly Func<AppSettings> _settings;
    private TcpListener? _listener;

    public event Action<DownloadRequest>? DownloadRequested;
    public event Action? ShowRequested;

    public Bridge(Func<AppSettings> settings) => _settings = settings;

    /// <summary>False when another program already uses the port (usually WinDM itself).</summary>
    public bool Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            _ = AcceptLoopAsync(_listener);
            return true;
        }
        catch (SocketException)
        {
            _listener = null;
            return false;
        }
    }

    public void Stop()
    {
        _listener?.Stop();
        _listener = null;
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (true)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch { return; } // stopped
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream).ConfigureAwait(false);
                if (request is null) return;
                var (status, body, origin) = Respond(request.Value);
                var head = new StringBuilder($"HTTP/1.1 {status} {(status < 300 ? "OK" : "Error")}\r\n");
                head.Append($"Content-Type: application/json\r\nConnection: close\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n");
                if (origin is not null)
                    head.Append($"Access-Control-Allow-Origin: {origin}\r\nAccess-Control-Allow-Headers: Content-Type\r\nAccess-Control-Allow-Methods: GET, POST\r\n");
                head.Append("\r\n");
                var bytes = Encoding.UTF8.GetBytes(head + body);
                await stream.WriteAsync(bytes).ConfigureAwait(false);
            }
            catch { /* client went away */ }
        }
    }

    private readonly record struct HttpRequest(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new List<byte>();
        var chunk = new byte[16 * 1024];
        while (buffer.Count < 1_000_000)
        {
            var read = await stream.ReadAsync(chunk).ConfigureAwait(false);
            if (read == 0) return null;
            buffer.AddRange(chunk.AsSpan(0, read));
            var data = buffer.ToArray();
            var separator = IndexOf(data, "\r\n\r\n"u8);
            if (separator < 0) continue;
            var lines = Encoding.UTF8.GetString(data, 0, separator).Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length < 2) return null;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
                if (line.IndexOf(':') is var colon and > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            var length = int.TryParse(headers.GetValueOrDefault("Content-Length"), out var l) ? l : 0;
            var bodyStart = separator + 4;
            if (data.Length - bodyStart < length) continue;
            return new HttpRequest(first[0], first[1].Split('?')[0], headers, data[bodyStart..(bodyStart + length)]);
        }
        return null;
    }

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> pattern) => data.AsSpan().IndexOf(pattern);

    private sealed class Payload
    {
        public string? Url { get; set; }
        public string? Filename { get; set; }
        public string? Referrer { get; set; }
        public string? Cookies { get; set; }
        public string? UserAgent { get; set; }
        public string? Kind { get; set; }
        public long? Variant { get; set; }
        public bool? Auto { get; set; }
    }

    private static readonly JsonSerializerOptions PayloadJson = new() { PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };

    private (int Status, string Body, string? Origin) Respond(HttpRequest request)
    {
        var origin = request.Headers.GetValueOrDefault("Origin");
        var trusted = origin is null || origin.StartsWith("chrome-extension://") || origin.StartsWith("moz-extension://")
                      || origin.StartsWith("extension://"); // Edge
        if (!trusted) return (403, """{"error":"forbidden origin"}""", null);
        if (request.Method == "OPTIONS") return (204, "", origin);

        var settings = _settings();
        switch (request.Method, request.Path)
        {
            case ("GET", "/ping"):
                var rules = new CaptureRules(settings);
                var status = new Dictionary<string, object>
                {
                    ["app"] = "WinDM", ["version"] = "1.0",
                    ["capture"] = settings.BrowserCapture, ["grabber"] = settings.VideoGrabber,
                    ["types"] = rules.Types.Order().ToList(), ["excludedSites"] = rules.Sites, ["excludedAddresses"] = rules.Addresses,
                };
                return (200, JsonSerializer.Serialize(status), origin);

            case ("GET", "/show"):
                ShowRequested?.Invoke();
                return (200, """{"shown":true}""", origin);

            case ("POST", "/download"):
                Payload? payload;
                try { payload = JsonSerializer.Deserialize<Payload>(request.Body, PayloadJson); }
                catch (JsonException) { payload = null; }
                if (payload?.Url is null || !Uri.TryCreate(payload.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
                    return (400, """{"error":"expected JSON with an http(s) url"}""", origin);
                // Automatic captures respect Options → File Types exclusions; the browser keeps those.
                if (payload.Auto == true && !new CaptureRules(settings).Allows(url))
                    return (409, """{"accepted":false,"excluded":true}""", origin);
                DownloadRequested?.Invoke(ToRequest(payload, url));
                return (202, """{"accepted":true}""", origin);

            default:
                return (404, """{"error":"not found"}""", origin);
        }
    }

    private static DownloadRequest ToRequest(Payload payload, Uri url)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(payload.Referrer)) headers["Referer"] = payload.Referrer;
        if (!string.IsNullOrEmpty(payload.Cookies)) headers["Cookie"] = payload.Cookies;
        if (!string.IsNullOrEmpty(payload.UserAgent)) headers["User-Agent"] = payload.UserAgent;
        return new DownloadRequest
        {
            Url = url,
            Headers = headers,
            FileName = Names.Sanitize(Path.GetFileName(payload.Filename ?? "")),
            Kind = payload.Kind?.ToLowerInvariant() switch { "hls" => DownloadKind.Hls, "dash" => DownloadKind.Dash, _ => DownloadKind.File },
            VariantBandwidth = payload.Variant,
        };
    }

    /// <summary>Turns a `macdm://download?url=…` link (the extensions' fallback when the app isn't running)
    /// into a request; null for `macdm://launch` and anything unusable.</summary>
    public static DownloadRequest? FromLink(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != "macdm") return null;
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "");
        if (!query.TryGetValue("url", out var target) || !Uri.TryCreate(target, UriKind.Absolute, out var url)
            || url.Scheme is not ("http" or "https")) return null;
        return ToRequest(new Payload
        {
            Url = target,
            Referrer = query.GetValueOrDefault("referrer"),
            Filename = query.GetValueOrDefault("filename"),
            Kind = query.GetValueOrDefault("kind"),
            Variant = long.TryParse(query.GetValueOrDefault("variant"), out var v) ? v : null,
        }, url);
    }
}

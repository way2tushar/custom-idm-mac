using System.Net;

namespace WinDM.Engine;

public sealed record ProbeResult(Uri FinalUrl, long? TotalBytes, bool AcceptsRanges, string FileName, string? ETag, string? LastModified);

/// <summary>Learns size, range support and file name with a one-byte ranged GET
/// (more reliable than HEAD, which many CDNs answer differently).</summary>
public static class Probe
{
    public static async Task<ProbeResult> RunAsync(HttpClient http, DownloadRequest request, CancellationToken ct)
    {
        using var message = Http.Get(request.Url, request, 0, 0);
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Server responded with HTTP {(int)response.StatusCode}");

        long? total = null;
        var ranges = false;
        if (response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.Length is long length)
        {
            total = length;
            ranges = true;
        }
        else if (response.Content.Headers.ContentLength is > 0)
        {
            total = response.Content.Headers.ContentLength;
        }

        var finalUrl = response.RequestMessage?.RequestUri ?? request.Url;
        var disposition = response.Content.Headers.ContentDisposition;
        var name = Names.Sanitize(request.FileName)
            ?? Names.Sanitize(disposition?.FileNameStar?.Trim('"'))
            ?? Names.Sanitize(disposition?.FileName?.Trim('"'))
            ?? Names.Sanitize(Uri.UnescapeDataString(Path.GetFileName(finalUrl.AbsolutePath)))
            ?? "download";

        return new ProbeResult(
            finalUrl, total, ranges,
            Names.WithExtension(name, response.Content.Headers.ContentType?.MediaType),
            response.Headers.ETag?.ToString(),
            response.Content.Headers.LastModified?.ToString("R"));
    }
}

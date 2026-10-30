using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WinDM.Engine;

/// <summary>One track's media segments, from an HLS media playlist or a DASH representation.</summary>
public sealed class MediaPlaylist
{
    public sealed record Key(Uri Uri, byte[]? IV);
    public sealed record Segment(Uri Url, Key? Key, long Sequence, long? From = null, long? To = null);

    public List<Segment> Segments { get; } = [];
    public Uri? InitSection { get; set; }
    public long? InitFrom { get; set; }
    public long? InitTo { get; set; }
    /// <summary>Extension of the joined track: "ts", "mp4" or "webm".</summary>
    public string? Container { get; set; }
}

public sealed class StreamException(string message) : Exception(message);

public static partial class Hls
{
    public sealed record Variant(Uri Url, long Bandwidth, string? AudioGroup);

    public sealed record Master(List<Variant> Variants, Dictionary<string, List<Uri>> Audio)
    {
        /// <summary>The requested quality (matched by bandwidth), else the highest.</summary>
        public Variant Pick(long? bandwidth) =>
            (bandwidth is not null ? Variants.FirstOrDefault(v => v.Bandwidth == bandwidth) : null)
            ?? Variants.MaxBy(v => v.Bandwidth)!;

        public Uri? AudioFor(Variant variant) =>
            variant.AudioGroup is { } group && Audio.TryGetValue(group, out var tracks) ? tracks.FirstOrDefault() : null;
    }

    [GeneratedRegex("""([A-Z0-9-]+)=("[^"]*"|[^,]*)""")]
    private static partial Regex AttributePattern();

    public static Dictionary<string, string> Attributes(string text) =>
        AttributePattern().Matches(text).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim('"'));

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    /// <summary>A master playlist's qualities and separate audio tracks; null for a media playlist.</summary>
    public static Master? ParseMaster(string text, Uri baseUrl)
    {
        var variants = new List<Variant>();
        var audio = new Dictionary<string, List<Uri>>();
        Dictionary<string, string>? pending = null;
        foreach (var line in Lines(text))
        {
            if (line.StartsWith("#EXT-X-STREAM-INF:"))
            {
                pending = Attributes(line["#EXT-X-STREAM-INF:".Length..]);
            }
            else if (line.StartsWith("#EXT-X-MEDIA:"))
            {
                var attrs = Attributes(line["#EXT-X-MEDIA:".Length..]);
                if (attrs.GetValueOrDefault("TYPE") != "AUDIO" || !attrs.TryGetValue("GROUP-ID", out var group)
                    || !attrs.TryGetValue("URI", out var uri)) continue;
                var tracks = audio.TryGetValue(group, out var list) ? list : audio[group] = [];
                var url = new Uri(baseUrl, uri);
                if (attrs.GetValueOrDefault("DEFAULT") == "YES") tracks.Insert(0, url); else tracks.Add(url);
            }
            else if (pending is not null && !line.StartsWith('#'))
            {
                long.TryParse(pending.GetValueOrDefault("BANDWIDTH") ?? pending.GetValueOrDefault("AVERAGE-BANDWIDTH"), out var bandwidth);
                variants.Add(new Variant(new Uri(baseUrl, line), bandwidth, pending.GetValueOrDefault("AUDIO")));
                pending = null;
            }
        }
        return variants.Count > 0 ? new Master(variants, audio) : null;
    }

    public static MediaPlaylist ParseMedia(string text, Uri baseUrl)
    {
        if (!text.TrimStart().StartsWith("#EXTM3U")) throw new StreamException("The address is not an HLS or DASH stream");
        var playlist = new MediaPlaylist();
        long sequence = 0, nextOffset = 0;
        MediaPlaylist.Key? key = null;
        (long From, long To)? pendingRange = null;

        // "length[@offset]"; without an offset the range follows the previous one.
        (long, long)? ByteRange(string spec)
        {
            var parts = spec.Split('@');
            if (!long.TryParse(parts[0], out var length) || length <= 0) return null;
            var start = parts.Length > 1 && long.TryParse(parts[1], out var offset) ? offset : nextOffset;
            nextOffset = start + length;
            return (start, start + length - 1);
        }

        foreach (var line in Lines(text))
        {
            if (line.StartsWith("#EXT-X-BYTERANGE:"))
                pendingRange = ByteRange(line["#EXT-X-BYTERANGE:".Length..]);
            else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:"))
                long.TryParse(line["#EXT-X-MEDIA-SEQUENCE:".Length..], out sequence);
            else if (line.StartsWith("#EXT-X-KEY:"))
            {
                var attrs = Attributes(line["#EXT-X-KEY:".Length..]);
                key = attrs.GetValueOrDefault("METHOD", "NONE") switch
                {
                    "NONE" => null,
                    "AES-128" when attrs.TryGetValue("URI", out var uri) =>
                        new MediaPlaylist.Key(new Uri(baseUrl, uri), attrs.TryGetValue("IV", out var iv) ? Hex(iv) : null),
                    var method => throw new StreamException($"This stream is protected ({method}) and can't be downloaded"),
                };
            }
            else if (line.StartsWith("#EXT-X-MAP:"))
            {
                var attrs = Attributes(line["#EXT-X-MAP:".Length..]);
                if (attrs.TryGetValue("URI", out var uri)) playlist.InitSection = new Uri(baseUrl, uri);
                if (attrs.TryGetValue("BYTERANGE", out var range) && ByteRange(range) is var (from, to))
                {
                    playlist.InitFrom = from;
                    playlist.InitTo = to;
                }
                nextOffset = 0;
            }
            else if (!line.StartsWith('#'))
            {
                playlist.Segments.Add(new MediaPlaylist.Segment(new Uri(baseUrl, line), key, sequence++, pendingRange?.From, pendingRange?.To));
                pendingRange = null;
            }
        }
        if (playlist.Segments.Count == 0) throw new StreamException("The playlist has no media segments");
        return playlist;
    }

    private static byte[]? Hex(string text)
    {
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        try { return Convert.FromHexString(hex); } catch (FormatException) { return null; }
    }
}

/// <summary>
/// Reads a static (on-demand) MPEG-DASH manifest into per-track segment lists: SegmentTemplate with or
/// without SegmentTimeline, SegmentList, and single-file representations. Live and DRM-protected
/// manifests are rejected; only the first Period is used.
/// </summary>
public static class Dash
{
    public static bool LooksLikeManifest(string text) => text.Length > 0 && text[..Math.Min(text.Length, 4000)].Contains("<MPD");

    private sealed record Representation(long Bandwidth, int Height, bool IsVideo, Func<MediaPlaylist> Playlist);

    /// <summary>The chosen video (by bandwidth, else the highest) and the best audio track.</summary>
    public static (MediaPlaylist Video, MediaPlaylist? Audio) Tracks(string xml, Uri baseUrl, long? videoBandwidth)
    {
        var representations = Parse(xml, baseUrl);
        var videos = representations.Where(r => r.IsVideo).ToList();
        var video = (videoBandwidth is not null ? videos.FirstOrDefault(v => v.Bandwidth == videoBandwidth) : null)
            ?? videos.OrderByDescending(v => v.Height).ThenByDescending(v => v.Bandwidth).FirstOrDefault();
        var audio = representations.Where(r => !r.IsVideo).MaxBy(r => r.Bandwidth);
        var main = video ?? audio ?? throw new StreamException("The manifest has no video or audio");
        return (main.Playlist(), video is not null ? audio?.Playlist() : null);
    }

    private static List<Representation> Parse(string xml, Uri baseUrl)
    {
        var mpd = XDocument.Parse(xml).Root;
        if (mpd?.Name.LocalName != "MPD") throw new StreamException("The address is not an HLS or DASH stream");
        if ((string?)mpd.Attribute("type") == "dynamic") throw new StreamException("Live streams can't be downloaded");
        if (mpd.Descendants().Any(e => e.Name.LocalName == "ContentProtection"))
            throw new StreamException("This stream is protected (DRM) and can't be downloaded");
        var period = Children(mpd, "Period").FirstOrDefault() ?? throw new StreamException("The manifest has no media");
        var duration = Seconds((string?)period.Attribute("duration") ?? (string?)mpd.Attribute("mediaPresentationDuration"));
        var periodBase = Resolve(period, Resolve(mpd, baseUrl));

        var result = new List<Representation>();
        foreach (var set in Children(period, "AdaptationSet"))
        {
            var setBase = Resolve(set, periodBase);
            foreach (var rep in Children(set, "Representation"))
            {
                var mime = (string?)rep.Attribute("mimeType") ?? (string?)set.Attribute("mimeType") ?? "";
                var type = (string?)set.Attribute("contentType") ?? mime.Split('/')[0];
                if (type is not ("video" or "audio")) continue;
                var id = (string?)rep.Attribute("id") ?? "";
                long.TryParse((string?)rep.Attribute("bandwidth"), out var bandwidth);
                int.TryParse((string?)rep.Attribute("height") ?? (string?)set.Attribute("height"), out var height);
                var repBase = Resolve(rep, setBase);
                var container = mime.Contains("webm") ? "webm" : "mp4";
                result.Add(new Representation(bandwidth, height, type == "video", () =>
                {
                    var playlist = Segments(rep, set, period, repBase, id, bandwidth, duration);
                    playlist.Container = container;
                    return playlist;
                }));
            }
        }
        return result;
    }

    private static MediaPlaylist Segments(XElement rep, XElement set, XElement period, Uri baseUrl, string id, long bandwidth, double duration)
    {
        // Representation-level settings override AdaptationSet-level ones, which override Period-level.
        XElement[] levels = [rep, set, period];
        var templates = levels.Select(l => Children(l, "SegmentTemplate").FirstOrDefault()).OfType<XElement>().ToList();
        if (templates.Count > 0) return Template(templates, baseUrl, id, bandwidth, duration);
        if (levels.Select(l => Children(l, "SegmentList").FirstOrDefault()).OfType<XElement>().FirstOrDefault() is { } list)
            return SegmentList(list, baseUrl);
        // Single file per representation (SegmentBase or just a BaseURL): the whole file is the track.
        var single = new MediaPlaylist();
        single.Segments.Add(new MediaPlaylist.Segment(baseUrl, null, 0));
        return single;
    }

    /// <summary>`templates` is ordered most specific first; each attribute comes from the first that has it.</summary>
    private static MediaPlaylist Template(List<XElement> templates, Uri baseUrl, string id, long bandwidth, double duration)
    {
        string? Attr(string name) => templates.Select(t => (string?)t.Attribute(name)).FirstOrDefault(v => v is not null);
        var media = Attr("media") ?? throw new StreamException("The manifest has no media");
        var timescale = double.TryParse(Attr("timescale"), CultureInfo.InvariantCulture, out var ts) ? ts : 1;
        var startNumber = long.TryParse(Attr("startNumber"), out var sn) ? sn : 1;
        var playlist = new MediaPlaylist();
        if (Attr("initialization") is { } init) playlist.InitSection = new Uri(baseUrl, Expand(init, id, bandwidth, 0, 0));

        var entries = new List<(long Number, long Time)>();
        var timeline = templates.Select(t => Children(t, "SegmentTimeline").FirstOrDefault()).OfType<XElement>().FirstOrDefault();
        if (timeline is not null)
        {
            long time = 0, number = startNumber, end = (long)(duration * timescale);
            var items = Children(timeline, "S").ToList();
            for (var i = 0; i < items.Count; i++)
            {
                if (long.TryParse((string?)items[i].Attribute("t"), out var t)) time = t;
                if (!long.TryParse((string?)items[i].Attribute("d"), out var d) || d <= 0) continue;
                long.TryParse((string?)items[i].Attribute("r"), out var repeats);
                if (repeats < 0)
                {
                    // Repeat until the next S element's start, or the end of the period.
                    var stop = i + 1 < items.Count && long.TryParse((string?)items[i + 1].Attribute("t"), out var next) ? next : end;
                    repeats = Math.Max(0, (stop - time + d - 1) / d - 1);
                }
                for (var r = 0; r <= repeats; r++, time += d, number++) entries.Add((number, time));
            }
        }
        else if (double.TryParse(Attr("duration"), CultureInfo.InvariantCulture, out var segmentDuration) && segmentDuration > 0 && duration > 0)
        {
            var count = (long)Math.Ceiling(duration * timescale / segmentDuration);
            for (long i = 0; i < count; i++) entries.Add((startNumber + i, (long)(i * segmentDuration)));
        }
        if (entries.Count == 0) throw new StreamException("The manifest has no media segments");
        long sequence = 0;
        foreach (var (number, time) in entries)
            playlist.Segments.Add(new MediaPlaylist.Segment(new Uri(baseUrl, Expand(media, id, bandwidth, number, time)), null, sequence++));
        return playlist;
    }

    private static MediaPlaylist SegmentList(XElement list, Uri baseUrl)
    {
        var playlist = new MediaPlaylist();
        if (Children(list, "Initialization").FirstOrDefault() is { } init)
        {
            playlist.InitSection = (string?)init.Attribute("sourceURL") is { } source ? new Uri(baseUrl, source) : baseUrl;
            if (Range((string?)init.Attribute("range")) is var (from, to)) { playlist.InitFrom = from; playlist.InitTo = to; }
        }
        long sequence = 0;
        foreach (var item in Children(list, "SegmentURL"))
        {
            var url = (string?)item.Attribute("media") is { } media ? new Uri(baseUrl, media) : baseUrl;
            var range = Range((string?)item.Attribute("mediaRange"));
            playlist.Segments.Add(new MediaPlaylist.Segment(url, null, sequence++, range?.From, range?.To));
        }
        return playlist;
    }

    /// <summary>Fills $RepresentationID$, $Bandwidth$, $Number$, $Time$ (with optional %0Nd widths) and $$.</summary>
    public static string Expand(string template, string id, long bandwidth, long number, long time) =>
        Regex.Replace(template, @"\$(\w*)(?:%0(\d+)d)?\$", match =>
        {
            var width = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
            return match.Groups[1].Value switch
            {
                "" => "$",
                "RepresentationID" => id,
                "Bandwidth" => bandwidth.ToString().PadLeft(width, '0'),
                "Number" => number.ToString().PadLeft(width, '0'),
                "Time" => time.ToString().PadLeft(width, '0'),
                _ => match.Value,
            };
        });

    /// <summary>ISO 8601 durations like "PT1H2M3.5S" or "P1DT2H".</summary>
    public static double Seconds(string? text)
    {
        if (text is null || !text.StartsWith('P')) return 0;
        double total = 0;
        var number = "";
        var inTime = false;
        foreach (var c in text[1..])
        {
            double value() => double.TryParse(number, CultureInfo.InvariantCulture, out var v) ? v : 0;
            switch (c)
            {
                case 'T': inTime = true; break;
                case 'D': total += value() * 86400; number = ""; break;
                case 'H': total += value() * 3600; number = ""; break;
                case 'M': total += value() * (inTime ? 60 : 2_592_000); number = ""; break;
                case 'S': total += value(); number = ""; break;
                default: number += c; break;
            }
        }
        return total;
    }

    private static (long From, long To)? Range(string? text)
    {
        var parts = text?.Split('-');
        return parts?.Length == 2 && long.TryParse(parts[0], out var from) && long.TryParse(parts[1], out var to) && from <= to
            ? (from, to) : null;
    }

    private static IEnumerable<XElement> Children(XElement element, string name) =>
        element.Elements().Where(e => e.Name.LocalName == name);

    private static Uri Resolve(XElement element, Uri baseUrl) =>
        Children(element, "BaseURL").FirstOrDefault()?.Value.Trim() is { Length: > 0 } text ? new Uri(baseUrl, text) : baseUrl;
}

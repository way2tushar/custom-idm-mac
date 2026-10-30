using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WinDM.Core;

public enum DuplicateAction { Rename, Overwrite }
public enum WhenQueueDone { Nothing, Exit, Sleep }

/// <summary>All options, saved as JSON in %APPDATA%\WinDM\settings.json.</summary>
public sealed class AppSettings
{
    // General
    public bool RunAtLogin { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool BrowserBridge { get; set; } = true;
    public bool BrowserCapture { get; set; } = true;
    public bool VideoGrabber { get; set; } = true;
    public bool PlaySound { get; set; } = true;
    public bool FFmpegPromptDismissed { get; set; }

    // Save to
    public string DownloadFolder { get; set; } = DefaultDownloads();
    public Dictionary<FileCategory, string> CategoryFolders { get; set; } = [];

    // File types
    public string CaptureTypes { get; set; } = CaptureRules.DefaultTypes;
    public string ExcludedSites { get; set; } = CaptureRules.DefaultSites;
    public List<string> ExcludedAddresses { get; set; } = [];

    // Connection, downloads, proxy
    public int Connections { get; set; } = 8;
    public bool ShowStartDialog { get; set; } = true;
    public bool ShowProgressDialog { get; set; } = true;
    public bool ShowCompleteDialog { get; set; } = true;
    public DuplicateAction Duplicates { get; set; } = DuplicateAction.Rename;
    public bool SpeedLimitEnabled { get; set; }
    public int SpeedLimitKB { get; set; } = 512;
    public bool ProxyEnabled { get; set; }
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; } = 8080;

    // Scheduler (main queue)
    public int QueueMaxConcurrent { get; set; } = 1;
    public bool SchedulerEnabled { get; set; }
    public int SchedulerStartMinute { get; set; } = 2 * 60;
    public bool SchedulerStopEnabled { get; set; }
    public int SchedulerStopMinute { get; set; } = 7 * 60;
    public bool SchedulerDaily { get; set; }
    public List<DayOfWeek> SchedulerDays { get; set; } = [.. Enum.GetValues<DayOfWeek>()];
    public WhenQueueDone SchedulerWhenDone { get; set; } = WhenQueueDone.Nothing;

    public static string DataFolder =>
        Environment.GetEnvironmentVariable("WINDM_DATA") is { Length: > 0 } custom ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinDM");

    private static string FilePath => Path.Combine(DataFolder, "settings.json");

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch { /* a damaged file falls back to defaults */ }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataFolder);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this, Json));
        File.Move(FilePath + ".tmp", FilePath, overwrite: true);
    }

    public string FolderFor(FileCategory category) =>
        CategoryFolders.TryGetValue(category, out var folder) && !string.IsNullOrWhiteSpace(folder) ? folder : DownloadFolder;

    public long? SpeedLimitBytes => SpeedLimitEnabled ? Math.Max(1, SpeedLimitKB) * 1024L : null;

    public string? ProxyAddress => ProxyEnabled && !string.IsNullOrWhiteSpace(ProxyHost) && ProxyPort > 0 ? $"{ProxyHost}:{ProxyPort}" : null;

    private static string DefaultDownloads() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
}

/// <summary>File categories used by the sidebar and by per-category save folders.</summary>
public enum FileCategory { General, Compressed, Documents, Music, Programs, Video }

public static class Categories
{
    private static readonly Dictionary<FileCategory, string[]> Extensions = new()
    {
        [FileCategory.Compressed] = ["zip", "rar", "7z", "gz", "tgz", "bz2", "xz", "tar", "arj", "lzh", "z", "iso", "img", "cab"],
        [FileCategory.Documents] = ["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "rtf", "txt", "epub", "odt", "ods", "csv"],
        [FileCategory.Music] = ["mp3", "wav", "wma", "aac", "aif", "aiff", "m4a", "flac", "ogg", "opus", "mid", "midi"],
        [FileCategory.Programs] = ["exe", "msi", "msix", "appx", "bat", "apk", "dmg", "pkg", "deb", "rpm", "jar"],
        [FileCategory.Video] = ["mp4", "m4v", "mkv", "mov", "avi", "wmv", "flv", "webm", "mpg", "mpeg", "3gp", "ts", "ogv", "asf"],
    };

    public static FileCategory Of(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return Extensions.FirstOrDefault(pair => pair.Value.Contains(ext)).Key;
    }
}

/// <summary>Which browser downloads WinDM takes over automatically (Options → File Types).
/// Links sent explicitly ("Download with MacDM", the video button) are never filtered.</summary>
public sealed class CaptureRules
{
    public const string DefaultTypes =
        "3gp 7z aac aif aiff apk arj asf avi bin bz2 deb dmg epub exe flac flv gz img iso m4a m4b m4v mkv mov " +
        "mp3 mp4 mpeg mpg msi ogg ogv opus pdf pkg rar rpm tar tgz tif tiff wav webm wma wmv xz z zip";
    public const string DefaultSites = "*.windowsupdate.com";

    public IReadOnlyCollection<string> Types { get; }
    public IReadOnlyList<string> Sites { get; }
    public IReadOnlyList<string> Addresses { get; }

    public CaptureRules(AppSettings settings)
    {
        Types = Words(settings.CaptureTypes).Select(t => t.ToLowerInvariant()).ToHashSet();
        Sites = Words(settings.ExcludedSites);
        Addresses = settings.ExcludedAddresses;
    }

    public static List<string> Words(string text) =>
        text.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>False when the address's site or the address itself is on an exclusion list.</summary>
    public bool Allows(Uri url) =>
        !Sites.Any(site => MatchesSite(site.ToLowerInvariant(), url.Host.ToLowerInvariant()))
        && !Addresses.Any(pattern => Wildcard(pattern).IsMatch(url.ToString()));

    /// <summary>"*.example.com" covers example.com and its subdomains; a plain name covers itself and subdomains.</summary>
    public static bool MatchesSite(string site, string host)
    {
        var bare = site.StartsWith("*.") ? site[2..] : site;
        return bare.Contains('*') ? Wildcard(site).IsMatch(host) : host == bare || host.EndsWith("." + bare);
    }

    private static Regex Wildcard(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);
}

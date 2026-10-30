using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinDM;

/// <summary>Windows integration: registry entries, file icons, sleep, opening files.</summary>
internal static class Platform
{
    private static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    /// <summary>Registers the macdm:// link the browser extensions use to start the app (per user, no admin).</summary>
    public static void RegisterLinkProtocol()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\macdm");
            key.SetValue("", "URL:WinDM download link");
            key.SetValue("URL Protocol", "");
            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{ExePath}\" \"%1\"");
        }
        catch { /* registry locked down by policy; links just won't start the app */ }
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool RunAtLogin
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("WinDM") is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("WinDM", $"\"{ExePath}\" --background");
            else key.DeleteValue("WinDM", throwOnMissingValue: false);
        }
    }

    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    public static void ShowInExplorer(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch { }
    }

    public static void Sleep() => Application.SetSuspendState(PowerState.Suspend, force: false, disableWakeEvent: false);

    // ----- File type icons -----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    private static readonly Dictionary<string, Image> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The shell's small icon for a file extension, without the file having to exist.</summary>
    public static Image FileIcon(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (IconCache.TryGetValue(ext, out var cached)) return cached;
        const uint FILE_ATTRIBUTE_NORMAL = 0x80, SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_USEFILEATTRIBUTES = 0x10;
        var info = new SHFILEINFO();
        Image image;
        try
        {
            SHGetFileInfo(string.IsNullOrEmpty(ext) ? "file" : "file" + ext, FILE_ATTRIBUTE_NORMAL, ref info,
                (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);
            using var icon = Icon.FromHandle(info.hIcon);
            image = icon.ToBitmap();
            DestroyIcon(info.hIcon);
        }
        catch
        {
            image = Glyphs.Render(Glyphs.Document, Color.Gray, 16);
        }
        return IconCache[ext] = image;
    }

    public static Icon AppIcon { get; } =
        new(typeof(Platform).Assembly.GetManifestResourceStream("WinDM.app.ico") ?? throw new InvalidOperationException("icon missing"));
}

/// <summary>Toolbar and sidebar pictures drawn from the Windows icon font (Segoe Fluent Icons on Windows 11,
/// Segoe MDL2 Assets on Windows 10), so they stay sharp at any scale.</summary>
internal static class Glyphs
{
    public const string Add = "", Play = "", Stop = "", Cancel = "", Delete = "",
        Completed = "", Settings = "", Calendar = "", List = "", Pause = "",
        Download = "", Recent = "", Package = "", Document = "", Audio = "",
        Video = "", Apps = "", Folder = "";

    private static readonly string FontName =
        new System.Drawing.Text.InstalledFontCollection().Families.Any(f => f.Name == "Segoe Fluent Icons")
            ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    public static Bitmap Render(string glyph, Color color, int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Font(FontName, size * 0.72f, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(glyph, font, brush, new RectangleF(0, 0, size, size), format);
        return bitmap;
    }
}

internal static class Format
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:0.0} {units[unit]}";
    }

    public static string Rate(double bytesPerSecond) => bytesPerSecond > 0 ? Bytes((long)bytesPerSecond) + "/s" : "";

    public static string Duration(double? seconds)
    {
        if (seconds is not double s || double.IsInfinity(s)) return "";
        var t = TimeSpan.FromSeconds(s);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s" : $"{t.Seconds}s";
    }
}

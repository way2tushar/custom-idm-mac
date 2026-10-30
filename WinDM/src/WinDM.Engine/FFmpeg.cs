using System.Diagnostics;

namespace WinDM.Engine;

/// <summary>Finds and runs ffmpeg, used to merge stream audio and repackage .ts as .mp4.</summary>
public static class FFmpeg
{
    /// <summary>Full path of ffmpeg, or null when it isn't installed.</summary>
    public static string? Find()
    {
        if (Environment.GetEnvironmentVariable("WINDM_FFMPEG") is { Length: > 0 } custom && File.Exists(custom)) return custom;
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, name))
            .Concat(
            [
                // winget puts command links here, which apps started before the install don't have on PATH.
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", name),
                "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg",
            ]);
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Runs ffmpeg writing `output`; false when it isn't installed or fails.</summary>
    public static bool Run(IEnumerable<string> arguments, string output)
    {
        if (Find() is not { } ffmpeg) return false;
        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in new[] { "-y", "-loglevel", "error" }.Concat(arguments).Append(output))
            start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start)!;
            process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            if (process.ExitCode == 0) return true;
        }
        catch { }
        try { File.Delete(output); } catch { }
        return false;
    }
}

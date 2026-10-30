using System.Net.Http.Json;
using WinDM.Core;

namespace WinDM;

internal static class Program
{
    /// <summary>
    /// Arguments: "--background" (start in the tray, used at login) and/or a macdm:// link from a browser
    /// extension. A second launch hands its link to the running copy through the local bridge and exits.
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        var link = args.FirstOrDefault(a => a.StartsWith("macdm:", StringComparison.OrdinalIgnoreCase));
        using var mutex = new Mutex(initiallyOwned: true, "WinDM.SingleInstance", out var first);
        if (!first)
        {
            ForwardToRunningCopy(link);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Platform.RegisterLinkProtocol();

        var settings = AppSettings.Load();
        var manager = new DownloadManager(settings);
        using var main = new MainForm(manager, startHidden: args.Contains("--background"));
        if (link is not null && Bridge.FromLink(link) is { } request) main.HandleBrowserRequest(request);
        Application.Run(main);
    }

    private static void ForwardToRunningCopy(string? link)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var root = $"http://127.0.0.1:{Bridge.Port}";
            if (link is not null && Bridge.FromLink(link) is { } request)
            {
                http.PostAsJsonAsync($"{root}/download", new
                {
                    url = request.Url.ToString(),
                    filename = request.FileName,
                    referrer = request.Headers.GetValueOrDefault("Referer"),
                    kind = request.Kind.ToString().ToLowerInvariant(),
                    variant = request.VariantBandwidth,
                }).Wait();
            }
            http.GetAsync($"{root}/show").Wait();
        }
        catch { /* the running copy will be found next time */ }
    }
}

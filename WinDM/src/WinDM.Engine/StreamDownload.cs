using System.Security.Cryptography;
using System.Text;

namespace WinDM.Engine;

/// <summary>
/// Downloads an HLS or DASH stream: every media segment is fetched (several at once), decrypted when an HLS
/// playlist uses AES-128, kept on disk so a paused download resumes, and joined in order at the end. A separate
/// audio track is merged in with ffmpeg; .ts output is repackaged as .mp4 when ffmpeg is installed.
/// </summary>
public sealed class StreamDownload : IDownloadJob
{
    private sealed record Track(string Name, MediaPlaylist Playlist);

    public Guid Id { get; }
    public DownloadRequest Request { get; }
    public string Directory { get; }
    public int MaxConnections { get; set; }
    public bool ReplaceExisting { get; set; }
    public event Action<ProgressSnapshot>? Progress;

    private readonly object _gate = new();
    private readonly SpeedLimiter _limiter = new();
    private readonly Dictionary<Uri, byte[]> _keys = [];
    private DownloadStatus _status = DownloadStatus.Queued;
    private CancellationTokenSource? _cts;
    private Task? _run;
    private int _partsDone, _partsTotal;
    private long _bytesDone, _bytesThisTick;
    private double _speed;
    private string _fileName;
    private string? _finalPath, _error;

    private string PartsDirectory => Path.Combine(Directory, ".windm", $"{Id}-parts");

    public StreamDownload(Guid id, DownloadRequest request, string directory, int maxConnections = 8)
    {
        Id = id;
        Request = request;
        Directory = directory;
        MaxConnections = Math.Max(1, maxConnections);
        // Playlists are often called index/master/playlist; the folder name says more.
        string[] generic = ["index", "master", "playlist", "prog_index", "chunklist", "manifest", "main", "video"];
        var stem = Path.GetFileNameWithoutExtension(request.Url.AbsolutePath);
        if (generic.Contains(stem, StringComparer.OrdinalIgnoreCase))
            stem = Path.GetFileName(Path.GetDirectoryName(request.Url.AbsolutePath) ?? "");
        if (!string.IsNullOrWhiteSpace(request.FileName)) stem = Path.GetFileNameWithoutExtension(request.FileName);
        _fileName = Names.Sanitize(stem) ?? "video";
    }

    public void SetSpeedLimit(long? bytesPerSecond) => _limiter.Limit = bytesPerSecond;

    public void Start()
    {
        lock (_gate)
        {
            if (_status is DownloadStatus.Probing or DownloadStatus.Downloading or DownloadStatus.Completed) return;
            _status = DownloadStatus.Probing;
            _error = null;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _run = Task.Run(() => RunAsync(token));
        }
        Emit();
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_status is not (DownloadStatus.Probing or DownloadStatus.Downloading)) return;
            _status = DownloadStatus.Paused;
            _cts?.Cancel();
        }
        Emit();
    }

    public void Cancel()
    {
        Task? run;
        lock (_gate)
        {
            _status = DownloadStatus.Failed;
            _error = "Cancelled";
            _cts?.Cancel();
            run = _run;
        }
        void Delete() { try { System.IO.Directory.Delete(PartsDirectory, true); } catch { } }
        if (run is null) Delete(); else run.ContinueWith(_ => Delete(), TaskScheduler.Default);
        Emit();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var http = Http.Create();
        using var ticker = new CancellationTokenSource();
        var tick = TickAsync(ticker.Token);
        try
        {
            var tracks = await LoadTracksAsync(http, ct).ConfigureAwait(false);
            System.IO.Directory.CreateDirectory(PartsDirectory);

            var work = new List<(Track Track, int Index)>();
            int resumedParts = 0;
            long resumedBytes = 0;
            foreach (var track in tracks)
                for (var index = 0; index < track.Playlist.Segments.Count; index++)
                {
                    var part = PartPath(track, index);
                    if (File.Exists(part)) { resumedParts++; resumedBytes += new FileInfo(part).Length; }
                    else work.Add((track, index));
                }
            lock (_gate)
            {
                _partsTotal = tracks.Sum(t => t.Playlist.Segments.Count);
                _partsDone = resumedParts;
                _bytesDone = resumedBytes;
                if (_status == DownloadStatus.Probing) _status = DownloadStatus.Downloading;
            }

            foreach (var track in tracks)
                if (track.Playlist.InitSection is { } init && !File.Exists(InitPath(track)))
                    await SaveAsync(http, init, track.Playlist.InitFrom, track.Playlist.InitTo, InitPath(track), ct).ConfigureAwait(false);

            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(MaxConnections, 1, 16), CancellationToken = ct };
            await Parallel.ForEachAsync(work, options, async (item, token) =>
                await DownloadSegmentAsync(http, item.Track, item.Index, token).ConfigureAwait(false)).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            var output = Assemble(tracks);
            lock (_gate)
            {
                _finalPath = output;
                _fileName = Path.GetFileNameWithoutExtension(output);
                _status = DownloadStatus.Completed;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // paused or cancelled; finished segments stay on disk for resuming
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                _status = DownloadStatus.Failed;
                _error = error.Message;
            }
        }
        finally
        {
            ticker.Cancel();
            try { await tick.ConfigureAwait(false); } catch (OperationCanceledException) { }
            Emit();
        }
    }

    /// <summary>A master playlist (HLS) or manifest (DASH) lists qualities; separate audio is its own track.</summary>
    private async Task<List<Track>> LoadTracksAsync(HttpClient http, CancellationToken ct)
    {
        var text = await FetchTextAsync(http, Request.Url, ct).ConfigureAwait(false);
        if (Dash.LooksLikeManifest(text))
        {
            var (video, audio) = Dash.Tracks(text, Request.Url, Request.VariantBandwidth);
            return audio is null ? [new("v", video)] : [new("v", video), new("a", audio)];
        }
        var videoUrl = Request.Url;
        Uri? audioUrl = null;
        if (Hls.ParseMaster(text, Request.Url) is { } master)
        {
            var variant = master.Pick(Request.VariantBandwidth);
            videoUrl = variant.Url;
            audioUrl = master.AudioFor(variant);
            text = await FetchTextAsync(http, videoUrl, ct).ConfigureAwait(false);
        }
        var tracks = new List<Track> { new("v", Hls.ParseMedia(text, videoUrl)) };
        if (audioUrl is not null)
            tracks.Add(new("a", Hls.ParseMedia(await FetchTextAsync(http, audioUrl, ct).ConfigureAwait(false), audioUrl)));
        return tracks;
    }

    private async Task DownloadSegmentAsync(HttpClient http, Track track, int index, CancellationToken ct)
    {
        var segment = track.Playlist.Segments[index];
        for (var attempt = 0; ; )
        {
            try
            {
                long size;
                if (segment.Key is { } key)
                {
                    var data = await FetchBytesAsync(http, segment.Url, segment.From, segment.To, ct).ConfigureAwait(false);
                    var clear = Decrypt(data, await KeyAsync(http, key.Uri, ct).ConfigureAwait(false), key.IV ?? SequenceIV(segment.Sequence));
                    await WriteAtomicAsync(PartPath(track, index), clear, ct).ConfigureAwait(false);
                    size = clear.Length;
                }
                else
                {
                    // Straight to disk: DASH single-file tracks can be hundreds of megabytes.
                    size = await SaveAsync(http, segment.Url, segment.From, segment.To, PartPath(track, index), ct).ConfigureAwait(false);
                }
                lock (_gate)
                {
                    _partsDone++;
                    _bytesDone += size;
                    _bytesThisTick += size;
                }
                return;
            }
            catch (Exception error) when (error is not OperationCanceledException and not StreamException)
            {
                if (++attempt > 6) throw;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << (attempt - 1))), ct).ConfigureAwait(false);
            }
        }
    }

    // ----- HTTP -----

    private async Task<string> FetchTextAsync(HttpClient http, Uri url, CancellationToken ct) =>
        Encoding.UTF8.GetString(await FetchBytesAsync(http, url, null, null, ct).ConfigureAwait(false));

    private async Task<byte[]> FetchBytesAsync(HttpClient http, Uri url, long? from, long? to, CancellationToken ct)
    {
        using var message = Http.Get(url, Request, from, to);
        using var response = await http.SendAsync(message, ct).ConfigureAwait(false);
        Http.EnsureSuccess(response, ranged: from is not null);
        var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        await _limiter.ThrottleAsync(data.Length, ct).ConfigureAwait(false);
        return data;
    }

    /// <summary>Streams a response into `destination` via a temporary file, so a partial file never counts as done.</summary>
    private async Task<long> SaveAsync(HttpClient http, Uri url, long? from, long? to, string destination, CancellationToken ct)
    {
        using var message = Http.Get(url, Request, from, to);
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        Http.EnsureSuccess(response, ranged: from is not null);
        var temporary = destination + ".tmp";
        long size = 0;
        await using (var output = File.Create(temporary))
        await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        {
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                size += read;
                await _limiter.ThrottleAsync(read, ct).ConfigureAwait(false);
            }
        }
        File.Move(temporary, destination, overwrite: true);
        return size;
    }

    private async Task<byte[]> KeyAsync(HttpClient http, Uri url, CancellationToken ct)
    {
        lock (_gate) if (_keys.TryGetValue(url, out var cached)) return cached;
        var key = await FetchBytesAsync(http, url, null, null, ct).ConfigureAwait(false);
        if (key.Length != 16) throw new StreamException($"This stream is protected (key of {key.Length} bytes) and can't be downloaded");
        lock (_gate) _keys[url] = key;
        return key;
    }

    /// <summary>Without an explicit IV, HLS uses the media sequence number as a 128-bit big-endian value.</summary>
    private static byte[] SequenceIV(long sequence)
    {
        var iv = new byte[16];
        for (var i = 0; i < 8; i++) iv[15 - i] = (byte)(sequence >> (8 * i));
        return iv;
    }

    private static byte[] Decrypt(byte[] data, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(data, iv, PaddingMode.PKCS7);
    }

    private static async Task WriteAtomicAsync(string path, byte[] data, CancellationToken ct)
    {
        await File.WriteAllBytesAsync(path + ".tmp", data, ct).ConfigureAwait(false);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    // ----- Files -----

    private string InitPath(Track track) => Path.Combine(PartsDirectory, $"{track.Name}-init.part");
    private string PartPath(Track track, int index) => Path.Combine(PartsDirectory, $"{track.Name}-{index:D6}.part");

    /// <summary>Concatenates one track's segments in order (after its init section, for fMP4).</summary>
    private string Join(Track track)
    {
        var fragmented = track.Playlist.InitSection is not null;
        var container = track.Playlist.Container ?? (fragmented ? "mp4" : "ts");
        var joined = Path.Combine(PartsDirectory, $"{track.Name}-joined.{container}");
        using var output = File.Create(joined);
        if (fragmented) using (var init = File.OpenRead(InitPath(track))) init.CopyTo(output);
        for (var index = 0; index < track.Playlist.Segments.Count; index++)
            using (var part = File.OpenRead(PartPath(track, index))) part.CopyTo(output);
        return joined;
    }

    private string Assemble(List<Track> tracks)
    {
        var video = Join(tracks[0]);
        var result = video;
        string? leftoverAudio = null;
        if (tracks.Count > 1)
        {
            var audio = Join(tracks[1]);
            var webm = video.EndsWith(".webm") && audio.EndsWith(".webm");
            var merged = Path.Combine(PartsDirectory, webm ? "merged.webm" : "merged.mp4");
            List<string> args = ["-i", video, "-i", audio, "-map", "0:v:0", "-map", "1:a:0", "-c", "copy"];
            if (audio.EndsWith(".ts")) args.AddRange(["-bsf:a", "aac_adtstoasc"]);
            if (FFmpeg.Run(args, merged)) result = merged;
            else leftoverAudio = audio; // no ffmpeg: keep the audio next to the video
        }
        else if (video.EndsWith(".ts"))
        {
            var mp4 = Path.ChangeExtension(video, ".mp4");
            if (FFmpeg.Run(["-i", video, "-c", "copy", "-bsf:a", "aac_adtstoasc"], mp4)) result = mp4;
        }

        var destination = Names.UniquePath(Directory, _fileName + Path.GetExtension(result), ReplaceExisting);
        File.Move(result, destination);
        if (leftoverAudio is not null)
        {
            var audioName = Path.GetFileNameWithoutExtension(destination) + " (audio)" + Path.GetExtension(leftoverAudio);
            try { File.Move(leftoverAudio, Names.UniquePath(Directory, audioName, false)); } catch { }
        }
        try { System.IO.Directory.Delete(PartsDirectory, true); } catch { }
        return destination;
    }

    // ----- Progress -----

    private async Task TickAsync(CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            lock (_gate)
            {
                var instant = _bytesThisTick / 0.5;
                _speed = _speed == 0 ? instant : _speed * 0.7 + instant * 0.3;
                _bytesThisTick = 0;
            }
            Emit();
        }
    }

    private void Emit()
    {
        ProgressSnapshot snapshot;
        lock (_gate)
        {
            snapshot = new ProgressSnapshot
            {
                Status = _status,
                FileName = _finalPath is not null ? Path.GetFileName(_finalPath) : _fileName,
                DownloadedBytes = _bytesDone,
                BytesPerSecond = _status == DownloadStatus.Downloading ? _speed : 0,
                PartsDone = _partsDone,
                PartsTotal = _partsTotal,
                Resumable = true,
                FilePath = _finalPath,
                Error = _error,
            };
            if (_status != DownloadStatus.Downloading) _speed = 0;
        }
        Progress?.Invoke(snapshot);
    }
}

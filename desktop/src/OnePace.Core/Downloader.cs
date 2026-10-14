using System.Diagnostics;
using System.IO.Hashing;
using System.Net;
using System.Text.Json;

namespace OnePace.Core;

/// <summary>One album's worth of episodes to fetch into one folder.</summary>
public sealed class DownloadJob
{
    public required string AlbumId { get; init; }
    public required string Subfolder { get; init; }
    /// <summary>The files wanted, keyed by Pixeldrain ID.</summary>
    public Dictionary<string, Source> Files { get; } = new();
}

public readonly record struct FileProgress(
    int FileIndex, int FileCount, string FileName,
    long FileBytes, long FileSize, double BytesPerSecond);

public interface IDownloadReporter
{
    void Log(string message);
    void Status(string message);
    void Progress(FileProgress progress);
}

/// <summary>Downloads One Pace / Muhn Pace episodes from Pixeldrain.
///
/// pixeldrain.com is tried first. It caps free downloads at about 6 GB a day
/// per IP: past that it either refuses (403/429) or slows to a crawl. Both
/// switch the download to the cdn.pixeldrain.eu.cc mirror, which serves the
/// same files without the cap. Downloads resume from a .part file, and every
/// finished file is checked against the CRC32 in its name.</summary>
public sealed class Downloader
{
    public static string ListApi { get; internal set; } = "https://pixeldrain.com/api/list/{0}";
    public static string FileUrl { get; internal set; } = "https://pixeldrain.com/api/file/{0}";
    public static string BypassUrl { get; internal set; } = "https://cdn.pixeldrain.eu.cc/{0}";

    const string PrimaryHost = "pixeldrain.com";
    const string MirrorHost = "cdn.pixeldrain.eu.cc";
    internal static double SlowAfterSeconds = 8;
    const double SlowBytesPerSecond = 500 * 1024;
    static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    const int MaxAttempts = 5;

    readonly string _destRoot;
    readonly IDownloadReporter _report;
    readonly CancellationToken _ct;

    /// <summary>Flips once pixeldrain.com has been slow, so later files skip
    /// straight to the mirror.</summary>
    public bool PreferBypass { get; set; }

    public Downloader(string destRoot, IDownloadReporter report, CancellationToken ct)
    {
        _destRoot = destRoot;
        _report = report;
        _ct = ct;
    }

    public sealed record AlbumFile(string Id, string Name, long Size);

    public static async Task<(string Title, List<AlbumFile> Files)> FetchAlbumAsync(string albumId, CancellationToken ct)
    {
        var bytes = await Http.GetBytesAsync(string.Format(ListApi, albumId), ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        if (!root.TryGetProperty("files", out var files))
            throw new InvalidDataException($"Pixeldrain didn't list album {albumId}");
        var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? albumId : albumId;
        var list = files.EnumerateArray().Select(f => new AlbumFile(
            f.GetProperty("id").GetString() ?? "",
            f.GetProperty("name").GetString() ?? "",
            f.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0)).ToList();
        return (title, list);
    }

    /// <summary>Downloads the job's files and returns where each one landed,
    /// keyed by the Pixeldrain ID it was asked for.</summary>
    public async Task<Dictionary<string, string>> RunAsync(DownloadJob job, int firstIndex = 1, int totalFiles = 0)
    {
        var plan = await PlanAsync(job).ConfigureAwait(false);
        var target = Path.Combine(_destRoot, Files.Sanitize(job.Subfolder));
        Directory.CreateDirectory(target);
        if (totalFiles <= 0) totalFiles = plan.Count;

        var done = new Dictionary<string, string>();
        int i = firstIndex;
        foreach (var (wantedId, file) in plan)
        {
            _ct.ThrowIfCancellationRequested();
            var name = Files.Sanitize(file.Name);
            var output = Path.Combine(target, name);
            _report.Status($"[{i}/{totalFiles}] {name}");
            await DownloadFileAsync(file, output, i, totalFiles).ConfigureAwait(false);
            done[wantedId] = output;
            i++;
        }
        return done;
    }

    /// <summary>Matches the wanted files against what the album holds now.
    /// One Pace re-releases episodes in place: same album, new file ID, new
    /// CRC in the name. Those are found by <see cref="Files.EpisodeKey"/>.
    /// When the album can't be listed at all, the IDs from the catalog are
    /// used as they are.</summary>
    async Task<List<(string WantedId, AlbumFile File)>> PlanAsync(DownloadJob job)
    {
        List<AlbumFile> albumFiles;
        try
        {
            var (title, files) = await FetchAlbumAsync(job.AlbumId, _ct).ConfigureAwait(false);
            albumFiles = files;
            _report.Log($"Album: {title} ({SourceRules.Plural(job.Files.Count, "file")} wanted)");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _report.Log($"  Couldn't list album {job.AlbumId} ({e.Message}). Using the catalog's file IDs.");
            return job.Files.Select(kv => (kv.Key, new AlbumFile(kv.Key, kv.Value.Filename ?? kv.Key, kv.Value.SizeBytes))).ToList();
        }

        var byId = albumFiles.ToDictionary(f => f.Id);
        var byKey = new Dictionary<string, AlbumFile>();
        foreach (var f in albumFiles) byKey.TryAdd(Files.EpisodeKey(f.Name), f);

        var plan = new List<(string, AlbumFile)>();
        int rematched = 0;
        var missing = new List<string>();
        foreach (var (id, src) in job.Files)
        {
            if (byId.TryGetValue(id, out var f)) { plan.Add((id, f)); continue; }
            if (!string.IsNullOrEmpty(src.Filename) && byKey.TryGetValue(Files.EpisodeKey(src.Filename), out f))
            {
                plan.Add((id, f));
                rematched++;
                continue;
            }
            missing.Add(src.Filename ?? id);
        }
        if (rematched > 0)
            _report.Log($"  {SourceRules.Plural(rematched, "episode")} re-released since the catalog was built, matched by name.");
        if (missing.Count > 0)
        {
            // Last chance: the file may still exist on Pixeldrain even though
            // the album dropped it. Try the catalog's ID directly.
            _report.Log($"  {SourceRules.Plural(missing.Count, "episode")} not in the album any more: "
                        + string.Join(", ", missing.Take(3)) + (missing.Count > 3 ? "…" : "")
                        + ". Trying their catalog links directly.");
            foreach (var (id, src) in job.Files)
                if (!plan.Any(p => p.Item1 == id))
                    plan.Add((id, new AlbumFile(id, src.Filename ?? id, src.SizeBytes)));
        }
        return plan;
    }

    async Task DownloadFileAsync(AlbumFile file, string output, int index, int count)
    {
        var partial = output + ".part";
        long size = file.Size;
        if (File.Exists(output) && (size == 0 || new FileInfo(output).Length == size))
        {
            _report.Log($"  Already downloaded: {Path.GetFileName(output)}");
            _report.Progress(new FileProgress(index, count, file.Name, size, size, 0));
            return;
        }

        Exception? last = null;
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                long start = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (size > 0 && start > size)
                {
                    _report.Log($"  Discarding an oversized partial file for {Path.GetFileName(output)}");
                    File.Delete(partial);
                    start = 0;
                }
                // A .part already at full size only needs checking; asking the
                // server for bytes past the end just earns a 416.
                if (!(size > 0 && start == size))
                    await StreamAsync(file, partial, start, index, count).ConfigureAwait(false);

                long got = new FileInfo(partial).Length;
                if (size > 0 && got != size)
                {
                    if (got > size) File.Delete(partial);
                    throw new IOException($"size mismatch: expected {size}, got {got}");
                }
                await VerifyCrcAsync(partial, file.Name).ConfigureAwait(false);
                File.Move(partial, output, overwrite: true);
                return;
            }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                last = e;
                if (attempt == MaxAttempts) break;
                var wait = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 30));
                _report.Log($"  Retry {attempt}/{MaxAttempts - 1} in {wait.TotalSeconds:0}s: {e.Message}");
                await Task.Delay(wait, _ct).ConfigureAwait(false);
            }
        }
        throw new IOException($"{file.Name} failed after {MaxAttempts} attempts: {last?.Message}", last);
    }

    async Task VerifyCrcAsync(string path, string name)
    {
        var expected = Files.ExpectedCrc(name);
        if (expected == null) return;
        var crc = new Crc32();
        await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
            await crc.AppendAsync(fs, _ct).ConfigureAwait(false);
        var actual = crc.GetCurrentHashAsUInt32();
        if (actual != expected)
        {
            File.Delete(path);
            throw new IOException($"checksum mismatch (got {actual:X8}, expected {expected:X8}), the file was damaged and will be fetched again");
        }
    }

    async Task<(HttpResponseMessage Resp, string Host)> OpenAsync(string fileId, long start)
    {
        if (!PreferBypass)
        {
            try
            {
                var resp = await SendAsync(string.Format(FileUrl, fileId), start).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return (resp, PrimaryHost);
                var code = (int)resp.StatusCode;
                resp.Dispose();
                _report.Log(code is 403 or 429
                    ? $"  pixeldrain.com hit its daily limit (HTTP {code}), using the mirror"
                    : $"  pixeldrain.com answered HTTP {code}, using the mirror");
            }
            catch (HttpRequestException e) when (!Http.IsCertError(e))
            {
                _report.Log($"  pixeldrain.com unreachable ({e.Message}), using the mirror");
            }
        }
        var bypass = await SendAsync(string.Format(BypassUrl, fileId), start).ConfigureAwait(false);
        if (!bypass.IsSuccessStatusCode)
        {
            var code = bypass.StatusCode;
            bypass.Dispose();
            throw new HttpRequestException($"the mirror answered HTTP {(int)code}", null, code);
        }
        return (bypass, MirrorHost);
    }

    async Task<HttpResponseMessage> SendAsync(string url, long start)
    {
        var req = Http.Request(HttpMethod.Get, url, browser: true);
        if (start > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, null);
        return await Http.Stream.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, _ct).ConfigureAwait(false);
    }

    async Task StreamAsync(AlbumFile file, string partial, long start, int index, int count)
    {
        var (resp, host) = await OpenAsync(file.Id, start).ConfigureAwait(false);
        try
        {
            if (start > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
            {
                _report.Log($"  {host} can't resume this file, starting it over");
                start = 0;
            }
            if (start == 0)
                _report.Log($"  Source: {host}" + (PreferBypass ? " (pixeldrain.com was slow earlier)" : ""));

            await using var fs = new FileStream(partial, start > 0 ? FileMode.Append : FileMode.Create,
                                                FileAccess.Write, FileShare.Read, 1 << 16, useAsync: true);
            var body = await resp.Content.ReadAsStreamAsync(_ct).ConfigureAwait(false);
            var buffer = new byte[256 * 1024];
            long bytes = start, measureFrom = start;
            var clock = Stopwatch.StartNew();
            double lastReport = 0;

            while (true)
            {
                int n;
                using (var stall = CancellationTokenSource.CreateLinkedTokenSource(_ct))
                {
                    stall.CancelAfter(StallTimeout);
                    try
                    {
                        n = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!_ct.IsCancellationRequested)
                    {
                        throw new IOException($"no data from {host} for {StallTimeout.TotalSeconds:0}s");
                    }
                }
                if (n == 0) break;
                await fs.WriteAsync(buffer.AsMemory(0, n), _ct).ConfigureAwait(false);
                bytes += n;
                double elapsed = clock.Elapsed.TotalSeconds;

                // pixeldrain.com's daily cap mostly throttles rather than
                // erroring, so a slow start also switches to the mirror.
                if (host == PrimaryHost && !PreferBypass && elapsed >= SlowAfterSeconds
                    && (bytes - measureFrom) / elapsed < SlowBytesPerSecond)
                {
                    _report.Log($"  pixeldrain.com is slow ({SourceRules.Bytes((bytes - measureFrom) / elapsed)}/s), switching to the mirror at {SourceRules.Bytes(bytes)}");
                    PreferBypass = true;
                    try
                    {
                        var mirror = await SendAsync(string.Format(BypassUrl, file.Id), bytes).ConfigureAwait(false);
                        if (mirror.StatusCode != HttpStatusCode.PartialContent)
                        {
                            mirror.Dispose();
                            throw new IOException("the mirror ignored the resume offset");
                        }
                        resp.Dispose();
                        resp = mirror;
                        body = await resp.Content.ReadAsStreamAsync(_ct).ConfigureAwait(false);
                        host = MirrorHost;
                        measureFrom = bytes;
                        clock.Restart();
                        lastReport = 0;
                        continue;
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        _report.Log($"  Couldn't switch to the mirror ({e.Message}), staying on pixeldrain.com");
                    }
                }

                if (elapsed - lastReport >= 0.2)
                {
                    lastReport = elapsed;
                    _report.Progress(new FileProgress(index, count, file.Name, bytes, file.Size,
                        (bytes - measureFrom) / Math.Max(elapsed, 0.001)));
                }
            }
            _report.Progress(new FileProgress(index, count, file.Name, bytes, file.Size,
                (bytes - measureFrom) / Math.Max(clock.Elapsed.TotalSeconds, 0.001)));
        }
        finally
        {
            resp.Dispose();
        }
    }
}

using System.Net.Http.Headers;

namespace OnePace.Core;

/// <summary>Loads the episode catalog and keeps it fresh. A GitHub Actions job
/// rebuilds it on the repo's data branch, so every user gets fresh data
/// without hitting Pixeldrain, nyaa.si and the rest themselves.</summary>
public static class IndexStore
{
    public const string RemoteIndexUrl =
        "https://raw.githubusercontent.com/Nicolaslahri/onepacedownloader/data/episode_index.json";

    /// <summary>The cached copy next to the .exe or the one built into the
    /// app, whichever is newer: an upgraded .exe can ship a newer catalog
    /// than the one an older version cached.</summary>
    public static EpisodeIndex Load(Func<Stream?> bundled)
    {
        EpisodeIndex? cached = null, builtIn = null;
        try
        {
            if (File.Exists(AppPaths.IndexFile))
                cached = EpisodeIndex.Parse(File.ReadAllBytes(AppPaths.IndexFile));
        }
        catch (Exception) { /* damaged cache: fall back to the bundled copy */ }
        try
        {
            using var s = bundled();
            if (s != null)
            {
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                builtIn = EpisodeIndex.Parse(ms.ToArray());
            }
        }
        catch (Exception) { }
        if (cached is { Arcs.Count: > 0 } && (builtIn == null
                || string.CompareOrdinal(cached.ScrapedAt ?? "", builtIn.ScrapedAt ?? "") >= 0))
            return cached;
        return builtIn ?? cached ?? new EpisodeIndex();
    }

    /// <summary>Cheap HEAD request: has the published catalog changed since
    /// our last refresh?</summary>
    public static async Task<bool> HasUpdateAsync(string? cachedEtag, CancellationToken ct = default)
    {
        using var req = Http.Request(HttpMethod.Head, RemoteIndexUrl);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var resp = await Http.Api.SendAsync(req, cts.Token).ConfigureAwait(false);
        var etag = resp.Headers.ETag?.ToString();
        return resp.IsSuccessStatusCode && !string.IsNullOrEmpty(etag) && etag != cachedEtag;
    }

    /// <summary>Where else the same file can be fetched when raw.githubusercontent.com
    /// is blocked or down: the GitHub API (another host and network) and the
    /// jsDelivr CDN (can lag the branch by a few hours).</summary>
    public static readonly string[] MirrorUrls =
    {
        "https://api.github.com/repos/Nicolaslahri/onepacedownloader/contents/episode_index.json?ref=data",
        "https://cdn.jsdelivr.net/gh/Nicolaslahri/onepacedownloader@data/episode_index.json",
    };

    public sealed record RefreshResult(EpisodeIndex Index, bool Changed, string? Etag, string Host);

    /// <summary>Downloads the published catalog and caches it beside the .exe.
    /// Tries GitHub first, then the mirrors; throws the first source's error
    /// if none of them works.</summary>
    public static async Task<RefreshResult> RefreshAsync(string? cachedEtag, CancellationToken ct = default,
                                                         Action<string>? log = null)
    {
        Exception? first = null;
        foreach (var url in MirrorUrls.Prepend(RemoteIndexUrl))
        {
            var host = new Uri(url).Host;
            try
            {
                var (bytes, etag) = await FetchAsync(url, ct).ConfigureAwait(false);
                var index = EpisodeIndex.Parse(bytes);   // throws on a malformed payload before we overwrite anything
                if (index.Arcs.Count == 0) throw new InvalidDataException("The published catalog has no arcs");
                // Mirrors have their own ETags, so only GitHub's is remembered.
                if (url != RemoteIndexUrl) etag = null;
                bool changed = etag == null || etag != cachedEtag || !File.Exists(AppPaths.IndexFile);
                if (changed)
                {
                    try { AppPaths.AtomicWrite(AppPaths.IndexFile, bytes); }
                    catch (Exception) { /* read-only folder: use it for this session only */ }
                }
                return new RefreshResult(index, changed, etag, host);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                first ??= e;
                log?.Invoke($"{host}: {Http.Describe(e)}");
            }
        }
        throw first!;
    }

    static async Task<(byte[] Bytes, string? Etag)> FetchAsync(string url, CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0) await Task.Delay(1500, ct).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                using var req = Http.Request(HttpMethod.Get, url);
                if (url.Contains("api.github.com"))
                    req.Headers.Accept.ParseAdd("application/vnd.github.raw");
                using var resp = await Http.Api.SendAsync(req, cts.Token).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                return (bytes, resp.Headers.ETag?.ToString());
            }
            catch (HttpRequestException e) when (e.StatusCode is { } code && (int)code < 500 && (int)code != 429)
            {
                throw;   // 403/404 won't change by asking again
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or IOException)
            {
                last = e is TaskCanceledException ? new TimeoutException($"No answer from {new Uri(url).Host} in 30 seconds", e) : e;
            }
        }
        throw last!;
    }
}

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

    public sealed record RefreshResult(EpisodeIndex Index, bool Changed, string? Etag);

    /// <summary>Downloads the published catalog and caches it beside the .exe.</summary>
    public static async Task<RefreshResult> RefreshAsync(string? cachedEtag, CancellationToken ct = default)
    {
        using var req = Http.Request(HttpMethod.Get, RemoteIndexUrl);
        using var resp = await Http.Api.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var etag = resp.Headers.ETag?.ToString();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var index = EpisodeIndex.Parse(bytes);   // throws on a malformed payload before we overwrite anything
        if (index.Arcs.Count == 0) throw new InvalidDataException("The published catalog has no arcs");
        bool changed = string.IsNullOrEmpty(etag) || etag != cachedEtag || !File.Exists(AppPaths.IndexFile);
        if (changed)
        {
            try { AppPaths.AtomicWrite(AppPaths.IndexFile, bytes); }
            catch (Exception) { /* read-only folder: use it for this session only */ }
        }
        return new RefreshResult(index, changed, etag);
    }
}

using System.Net;
using System.Text;

namespace OnePace.Core;

/// <summary>Fetches NZB files from the user's own Newznab indexer. The bundled
/// release IDs are NZBGeek's. The API key only ever lives in config.json.</summary>
public static class Usenet
{
    public const string DefaultIndexerUrl = "https://api.nzbgeek.info";
    public const string SetupGuideUrl = "https://github.com/Nicolaslahri/onepacedownloader#usenet-setup";
    public const string ApiKeyPageUrl = "https://nzbgeek.info/dashboard.php?myaccount";

    public static string BuildUrl(string indexer, string query) => indexer.TrimEnd('/') + "/api?" + query;

    /// <summary>Saves the release's .nzb into <paramref name="folder"/> and returns its path.</summary>
    public static async Task<string> FetchNzbAsync(string indexer, string apiKey, Source source, string folder,
                                                   CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(source.Guid))
            throw new InvalidOperationException("This release has no ID, so its NZB can't be fetched.");
        var url = BuildUrl(indexer, $"t=get&id={Uri.EscapeDataString(source.Guid)}&apikey={Uri.EscapeDataString(apiKey)}");
        using var req = Http.Request(HttpMethod.Get, url);
        using var resp = await Http.Api.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("The indexer refused your API key.");
        resp.EnsureSuccessStatusCode();
        var blob = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var head = Encoding.UTF8.GetString(blob, 0, Math.Min(blob.Length, 2000));
        if (blob.Length < 200 || !head.Contains("<nzb"))
            throw new InvalidOperationException("The indexer didn't return an NZB. Check your API key and that the indexer is up.");

        Directory.CreateDirectory(folder);
        var name = Files.NzbFileName(source.ReleaseTitle, source.Guid);
        var path = Path.Combine(folder, name);
        if (path.Length > 240)
        {
            int budget = 240 - folder.Length - 2 - 4;
            path = Path.Combine(folder, budget > 20
                ? Path.GetFileNameWithoutExtension(name)[..Math.Min(budget, Path.GetFileNameWithoutExtension(name).Length)] + "_" + source.Guid[..Math.Min(8, source.Guid.Length)] + ".nzb"
                : source.Guid + ".nzb");
        }
        await File.WriteAllBytesAsync(path, blob, ct).ConfigureAwait(false);
        return path;
    }

    public static async Task<(bool Ok, string Message)> TestAsync(string indexer, string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return (false, "The API key is empty.");
        try
        {
            var url = BuildUrl(indexer, $"t=search&apikey={Uri.EscapeDataString(apiKey)}&limit=1");
            using var req = Http.Request(HttpMethod.Get, url);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = await Http.Api.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (false, "The indexer refused that API key.");
            if (!resp.IsSuccessStatusCode) return (false, $"The indexer answered HTTP {(int)resp.StatusCode}.");
            var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (body.Contains("error code=\"100\"") || body.Contains("Incorrect credentials"))
                return (false, "The indexer refused that API key.");
            return (true, "Connected. Your API key works.");
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (false, $"Couldn't reach the indexer ({e.GetType().Name}).");
        }
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;

namespace OnePace.Core;

public static partial class AppUpdate
{
    public const string ReleasesApi = "https://api.github.com/repos/Nicolaslahri/onepacedownloader/releases/latest";
    public const string RepoUrl = "https://github.com/Nicolaslahri/onepacedownloader";

    /// <summary>"v2.1.0" becomes [2, 1, 0]. A non-numeric part ends it.</summary>
    public static int[] VersionParts(string? v)
    {
        var parts = new List<int>();
        foreach (var part in (v ?? "").Trim().TrimStart('v', 'V').Split('.'))
        {
            var m = LeadingDigits().Match(part);
            if (!m.Success) break;
            parts.Add(int.Parse(m.Value));
        }
        return parts.ToArray();
    }

    [GeneratedRegex(@"^\d+")]
    private static partial Regex LeadingDigits();

    public static bool IsNewer(string candidate, string current)
    {
        var a = VersionParts(candidate);
        var b = VersionParts(current);
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    /// <summary>The latest release if it's newer than this build, else null.</summary>
    public static async Task<(string Version, string Url)?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        using var req = Http.Request(HttpMethod.Get, ReleasesApi);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var resp = await Http.Api.SendAsync(req, cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false));
        var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null;
        return IsNewer(tag, currentVersion) ? (tag.TrimStart('v', 'V'), url ?? RepoUrl + "/releases/latest") : null;
    }
}

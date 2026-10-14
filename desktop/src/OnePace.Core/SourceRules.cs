using System.Globalization;
using System.Text.RegularExpressions;

namespace OnePace.Core;

public static partial class SourceRules
{
    public const string OnePace = "onepace";
    public const string Muhn = "muhn";
    public const string Nyaa = "nyaa";
    public const string Usenet = "usenet";
    public static readonly string[] Kinds = { OnePace, Muhn, Nyaa, Usenet };

    public const string Sub = "English Subtitles";
    public const string Dub = "English Dub";
    public const string DubCc = "English Dub with Closed Captions";
    public static readonly string[] Versions = { Sub, Dub, DubCc };
    public static readonly string[] Qualities = { "1080p", "720p", "480p" };

    public const string NyaaOfficialUploader = "Galaxy9000";

    public static string Label(string kind) => kind switch
    {
        OnePace => "One Pace",
        Muhn => "Muhn Pace",
        Nyaa => "Nyaa",
        Usenet => "Usenet",
        _ => kind,
    };

    public static string Blurb(string kind) => kind switch
    {
        OnePace => "Fan re-cut. Sub for every arc, Dub for the newer ones.",
        Muhn => "Fan-made English dub for the arcs One Pace hasn't dubbed yet (Enies Lobby to Wano).",
        Nyaa => "Torrents from nyaa.si. Official One Pace uploads are marked Official.",
        Usenet => "NZB files from NZBGeek. Needs your own Usenet provider and indexer API key (set it up in Settings).",
        _ => "",
    };

    public static string VersionShort(string? version, string kind = OnePace) => version switch
    {
        Sub => "Sub",
        Dub => "Dub",
        DubCc => "Dub-CC",
        _ => kind == Muhn ? "Dub" : "?",
    };

    public static string VersionLong(string? version) => version switch
    {
        Sub => "English subtitles",
        Dub => "English dub",
        DubCc => "English dub + closed captions",
        null or "" => "English dub",
        _ => version,
    };

    public static bool IsDub(string? version) => !string.IsNullOrEmpty(version) && version.Contains("Dub");

    // Closest substitute first. The two dubs share an audio track, so they
    // stand in for each other before anything falls back to Japanese audio.
    static readonly Dictionary<string, string[]> Fallback = new()
    {
        [Sub] = new[] { Sub, Dub, DubCc },
        [Dub] = new[] { Dub, DubCc, Sub },
        [DubCc] = new[] { DubCc, Dub, Sub },
    };

    public static int QualityRank(string? quality)
    {
        var m = QualityRe().Match(quality ?? "");
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    [GeneratedRegex(@"^(\d+)p")]
    private static partial Regex QualityRe();

    /// <summary>Best One Pace / Muhn Pace file for an episode. Versions are
    /// tried closest-first; within a version the preferred quality wins, else
    /// the best one there is. With <paramref name="allowSubs"/> off, a dub
    /// preference never falls back to the subtitled release.</summary>
    public static Source? BestSource(Episode ep, string kind, string versionPref,
                                     string qualityPref, bool allowSubs = true)
    {
        var candidates = ep.Sources.Where(s => s.Kind == kind).ToList();
        if (candidates.Count == 0) return null;

        IEnumerable<string?> order;
        if (!candidates.Any(s => !string.IsNullOrEmpty(s.Version)))
            order = new string?[] { null };   // Muhn Pace: dub-only, no version field
        else
        {
            var list = Fallback.TryGetValue(versionPref, out var f) ? f : new[] { versionPref };
            if (!allowSubs && IsDub(versionPref)) list = list.Where(IsDub).ToArray();
            order = list;
        }

        foreach (var ver in order)
        {
            var same = candidates.Where(s => (string.IsNullOrEmpty(s.Version) ? null : s.Version) == ver).ToList();
            var exact = same.FirstOrDefault(s => s.Quality == qualityPref);
            if (exact != null) return exact;
            if (same.Count > 0) return same.MaxBy(s => QualityRank(s.Quality));
        }
        // A version label we don't know: anything beats nothing.
        return allowSubs ? candidates.MaxBy(s => QualityRank(s.Quality)) : null;
    }

    /// <summary>Official Galaxy9000 upload first, otherwise the most seeded.</summary>
    public static Source? BestTorrent(Episode ep)
    {
        var all = ep.SourcesOf(Nyaa).ToList();
        if (all.Count == 0) return null;
        var official = all.Where(s => s.IsOfficial).ToList();
        return (official.Count > 0 ? official : all).MaxBy(s => s.Seeders);
    }

    public static Source? BestNzb(Episode ep, string qualityPref)
    {
        var all = ep.SourcesOf(Usenet).ToList();
        return all.FirstOrDefault(s => s.Quality == qualityPref) ?? all.MaxBy(s => QualityRank(s.Quality));
    }

    /// <summary>Nyaa packs predate the kind field, so untagged packs count as Nyaa.</summary>
    public static IEnumerable<Source> PacksOf(Arc arc, string kind) =>
        arc.ArcPacks.Where(p => kind == Nyaa ? string.IsNullOrEmpty(p.Kind) || p.Kind == Nyaa : p.Kind == kind);

    public static bool ArcHas(Arc arc, string kind) =>
        arc.Episodes.Any(e => e.Has(kind)) || PacksOf(arc, kind).Any();

    /// <summary>"171 MB" or "1.23 GB", like the episode tiles.</summary>
    public static string Size(long bytes)
    {
        double mb = bytes / 1024d / 1024d;
        return mb >= 1024
            ? (mb / 1024).ToString("0.00", CultureInfo.InvariantCulture) + " GB"
            : mb.ToString("0", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>"209.8 MB" for live progress.</summary>
    public static string Bytes(double n)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        foreach (var u in units)
        {
            if (n < 1024) return n.ToString("0.0", CultureInfo.InvariantCulture) + " " + u;
            n /= 1024;
        }
        return n.ToString("0.0", CultureInfo.InvariantCulture) + " PB";
    }

    public static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";
}

/// <summary>How well Nyaa covers an arc's canonical One Pace episodes.</summary>
public readonly record struct NyaaCoverage(int PerEpisode, int Total, int Packs)
{
    public static NyaaCoverage Of(Arc arc)
    {
        var canonical = arc.Episodes.Where(e => e.Has(SourceRules.OnePace)).ToList();
        return new(canonical.Count(e => e.Has(SourceRules.Nyaa)), canonical.Count,
                   SourceRules.PacksOf(arc, SourceRules.Nyaa).Count());
    }
}

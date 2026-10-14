using System.Text.RegularExpressions;

namespace OnePace.Core;

/// <summary>Plex / Jellyfin season numbers, pinned per arc. Deriving them from
/// the arc's position would renumber every later season (and orphan existing
/// library folders) the day One Pace adds an arc in the middle. Arcs not
/// listed are numbered after the highest pinned season, in index order.</summary>
public static partial class Seasons
{
    static readonly Dictionary<string, int> Pinned = new()
    {
        ["romancedawn"] = 1, ["orangetown"] = 2, ["syrupvillage"] = 3, ["gaimon"] = 4,
        ["baratie"] = 5, ["arlongpark"] = 6, ["theadventuresofbuggyscrew"] = 7,
        ["loguetown"] = 8, ["reversemountain"] = 9, ["whiskypeak"] = 10,
        ["thetrialsofkobymeppo"] = 11, ["littlegarden"] = 12, ["drumisland"] = 13,
        ["alabasta"] = 14, ["jaya"] = 15, ["skypiea"] = 16, ["longringlongland"] = 17,
        ["waterseven"] = 18, ["enieslobby"] = 19, ["postenieslobby"] = 20,
        ["thrillerbark"] = 21, ["sabaodyarchipelago"] = 22, ["amazonlily"] = 23,
        ["impeldown"] = 24, ["ifyoucouldgoanywheretheadventuresofthestrawhats"] = 25,
        ["marineford"] = 26, ["postwar"] = 27, ["returntosabaody"] = 28,
        ["fishmanisland"] = 29, ["punkhazard"] = 30, ["dressrosa"] = 31, ["zou"] = 32,
        ["wholecakeisland"] = 33, ["reverie"] = 34, ["wano"] = 35, ["egghead"] = 36,
    };

    public static string Slug(string? title) => NonAlnum().Replace((title ?? "").ToLowerInvariant(), "");

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAlnum();

    public static int? ForArc(EpisodeIndex index, string title)
    {
        if (!index.Arcs.Any(a => a.Title == title)) return null;
        if (Pinned.TryGetValue(Slug(title), out var pinned)) return pinned;
        int next = Pinned.Values.Max();
        foreach (var a in index.Arcs)
        {
            if (Pinned.ContainsKey(Slug(a.Title))) continue;
            next++;
            if (a.Title == title) return next;
        }
        return null;
    }
}

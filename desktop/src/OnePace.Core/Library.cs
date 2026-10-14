using System.Text.RegularExpressions;

namespace OnePace.Core;

/// <summary>What's already in the save folder, for the Saved badges and for
/// skipping episodes you have. Plex-renamed files are recognised by their
/// sNNeMM tag.</summary>
public sealed partial class Library
{
    readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _plexKeys = new(StringComparer.OrdinalIgnoreCase);

    public static Library Scan(string folder)
    {
        var lib = new Library();
        if (!Directory.Exists(folder)) return lib;
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var path in Directory.EnumerateFiles(folder, "*", opts))
            {
                var name = Path.GetFileName(path);
                if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
                lib._names.Add(name);
                var m = PlexTag().Match(name);
                if (m.Success) lib._plexKeys.Add($"s{m.Groups[1].Value}e{m.Groups[2].Value}");
            }
        }
        catch (Exception) { }
        return lib;
    }

    [GeneratedRegex(@"\bs(\d{2})e(\d{2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex PlexTag();

    public bool IsSaved(Episode ep, string kind, int? season)
    {
        foreach (var s in ep.Sources)
        {
            if (s.Kind != kind || string.IsNullOrEmpty(s.Filename)) continue;
            if (_names.Contains(Files.Sanitize(s.Filename)) || _names.Contains(s.Filename)) return true;
        }
        return season != null && _plexKeys.Contains($"s{season:00}e{ep.Num:00}");
    }
}

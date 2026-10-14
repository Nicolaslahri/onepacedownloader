using System.Security;
using System.Text;

namespace OnePace.Core;

/// <summary>Plex / Jellyfin layout:
/// &lt;save folder&gt;/One Pace/Season N/One Pace - sNNeMM - Title.ext,
/// with an episodedetails .nfo beside each file (same schema as
/// SpykerNZ/one-pace-for-plex, so existing scrapers pick it up).</summary>
public static class PlexOrganizer
{
    public static string NfoXml(int season, int episode, string? title, string? plot)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<episodedetails>\n");
        sb.Append($"  <title>{SecurityElement.Escape(string.IsNullOrEmpty(title) ? $"Episode {episode:00}" : title)}</title>\n");
        sb.Append("  <showtitle>One Pace</showtitle>\n");
        sb.Append($"  <season>{season}</season>\n");
        sb.Append($"  <episode>{episode}</episode>\n");
        sb.Append($"  <plot>{SecurityElement.Escape(plot ?? "")}</plot>\n");
        sb.Append("</episodedetails>\n");
        return sb.ToString();
    }

    public static string TargetName(int season, Episode ep, string extension)
    {
        var title = string.IsNullOrWhiteSpace(ep.CanonicalTitle) ? $"Episode {ep.Num:00}" : Files.Sanitize(ep.CanonicalTitle);
        return $"One Pace - s{season:00}e{ep.Num:00} - {title}{extension}";
    }

    /// <summary>Moves just-downloaded files into the library layout.
    /// Best effort: a file that can't be moved is logged and left where it is.</summary>
    public static int Organize(string saveRoot, Arc arc, EpisodeIndex index,
                               IEnumerable<(Episode Episode, string Path)> files, Action<string> log)
    {
        var season = Seasons.ForArc(index, arc.Title);
        if (season == null)
        {
            log($"  Plex organize: {arc.Title} isn't in the catalog, files left as they are");
            return 0;
        }
        var seasonDir = Path.Combine(saveRoot, "One Pace", $"Season {season}");
        try { Directory.CreateDirectory(seasonDir); }
        catch (Exception e)
        {
            log($"  Couldn't create {seasonDir}: {e.Message}");
            return 0;
        }

        int moved = 0;
        var folders = new HashSet<string>();
        foreach (var (ep, path) in files)
        {
            if (!File.Exists(path)) continue;
            var target = Path.Combine(seasonDir, TargetName(season.Value, ep, Path.GetExtension(path)));
            if (!MoveWithRetry(path, target, log)) continue;
            moved++;
            folders.Add(Path.GetDirectoryName(path)!);
            WriteNfo(target, season.Value, ep, log);
        }
        if (moved > 0)
            log($"  Organized {SourceRules.Plural(moved, "file")} into One Pace/Season {season}/");
        foreach (var dir in folders) TryRemoveEmpty(dir);
        return moved;
    }

    /// <summary>Organizes files downloaded before the setting was turned on.</summary>
    public static int OrganizeExisting(string saveRoot, EpisodeIndex index, Action<string> log)
    {
        if (!Directory.Exists(saveRoot)) return 0;
        var byName = new Dictionary<string, (Arc Arc, Episode Ep, int Season)>(StringComparer.OrdinalIgnoreCase);
        foreach (var arc in index.Arcs)
        {
            var season = Seasons.ForArc(index, arc.Title);
            if (season == null) continue;
            foreach (var ep in arc.Episodes)
                foreach (var s in ep.Sources)
                    if (!string.IsNullOrEmpty(s.Filename))
                        byName.TryAdd(Files.Sanitize(s.Filename), (arc, ep, season.Value));
        }

        var libraryRoot = Path.Combine(saveRoot, "One Pace");
        int moved = 0;
        var folders = new HashSet<string>();
        IEnumerable<string> all;
        try { all = Directory.EnumerateFiles(saveRoot, "*", SearchOption.AllDirectories).ToList(); }
        catch (Exception) { return 0; }
        foreach (var path in all)
        {
            if (path.StartsWith(libraryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileName(path);
            if (name.EndsWith(".part") || name.EndsWith(".nfo")) continue;
            if (!byName.TryGetValue(name, out var info)) continue;
            var seasonDir = Path.Combine(libraryRoot, $"Season {info.Season}");
            try { Directory.CreateDirectory(seasonDir); } catch (Exception) { continue; }
            var target = Path.Combine(seasonDir, TargetName(info.Season, info.Ep, Path.GetExtension(path)));
            if (!MoveWithRetry(path, target, log)) continue;
            moved++;
            folders.Add(Path.GetDirectoryName(path)!);
            WriteNfo(target, info.Season, info.Ep, log);
        }
        foreach (var dir in folders)
            if (!string.Equals(dir.TrimEnd('\\', '/'), saveRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                TryRemoveEmpty(dir);
        if (moved > 0) log($"Organized {SourceRules.Plural(moved, "existing file")} into the Plex / Jellyfin layout.");
        return moved;
    }

    static bool MoveWithRetry(string from, string to, Action<string> log)
    {
        // Defender or the search indexer can hold a fresh file for a moment.
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                File.Move(from, to, overwrite: true);
                return true;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(500);
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                Thread.Sleep(500);
            }
            catch (Exception e)
            {
                log($"  Couldn't move {Path.GetFileName(from)}: {e.Message}");
                return false;
            }
        }
        return false;
    }

    static void WriteNfo(string target, int season, Episode ep, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(ep.CanonicalTitle) && string.IsNullOrWhiteSpace(ep.Plot)) return;
        try
        {
            File.WriteAllText(Path.ChangeExtension(target, ".nfo"),
                NfoXml(season, ep.Num, ep.CanonicalTitle, ep.Plot), new UTF8Encoding(false));
        }
        catch (Exception e)
        {
            log($"  Couldn't write the .nfo for {Path.GetFileName(target)}: {e.Message}");
        }
    }

    static void TryRemoveEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch (Exception) { }
    }
}

using System.Text.RegularExpressions;

namespace OnePace.Core;

public static partial class Files
{
    public static string Sanitize(string? name)
    {
        var s = IllegalChars().Replace(name ?? "", "_").Trim(' ', '.');
        return s.Length == 0 ? "untitled" : s;
    }

    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1f]")]
    private static partial Regex IllegalChars();

    /// <summary>One Pace puts each file's CRC32 in its name, e.g. [DEC3026A].</summary>
    [GeneratedRegex(@"\[[0-9A-Fa-f]{8}\]")]
    public static partial Regex CrcTag();

    [GeneratedRegex(@"\[\d+(?:[-,]\d+)*\]")]
    private static partial Regex RangeTag();

    [GeneratedRegex(@"(?i)\[one pace\]")]
    private static partial Regex OnePaceTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>Identity of an episode file that survives a re-release (new
    /// Pixeldrain ID, new CRC, sometimes a widened chapter range):
    /// "[One Pace][115-117] Little Garden 01 [1080p][En Sub][DEC3026A].mp4"
    /// becomes "little garden 01 [1080p][en sub].mp4".</summary>
    public static string EpisodeKey(string filename)
    {
        var k = CrcTag().Replace(filename, "");
        k = RangeTag().Replace(k, "");
        k = OnePaceTag().Replace(k, "");
        return Spaces().Replace(k, " ").Trim().ToLowerInvariant();
    }

    public static uint? ExpectedCrc(string filename)
    {
        var m = CrcTag().Match(filename);
        return m.Success ? Convert.ToUInt32(m.Value[1..^1], 16) : null;
    }

    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    /// <summary>Windows-safe "title.nzb". Falls back to the GUID when nothing
    /// usable is left.</summary>
    public static string NzbFileName(string? title, string guid)
    {
        var s = Regex.Replace(title ?? "", @"[<>:""/\\|?*\x00-\x1f]+", "_");
        s = Regex.Replace(s, "_{2,}", "_").TrimEnd('.', ' ');
        s = s.Length > 120 ? s[..120] : s;
        s = s.TrimEnd('.', ' ');
        if (s.Length == 0) s = guid;
        var stem = Regex.Replace(s, @"\.nzb$", "", RegexOptions.IgnoreCase);
        if (Reserved.Contains(stem)) s = "_" + s;
        if (!s.EndsWith(".nzb", StringComparison.OrdinalIgnoreCase)) s += ".nzb";
        return s;
    }

    /// <summary>Creates the folder if needed; null when it is usable,
    /// otherwise a message to show.</summary>
    public static string? CheckWritable(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception e)
        {
            return $"Couldn't create the save folder:\n{folder}\n\n{e.Message}\n\nPick a different folder.";
        }
        try
        {
            var probe = Path.Combine(folder, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (Exception)
        {
            return $"This folder isn't writable:\n{folder}\n\nPick a folder you can write to, such as Downloads or Videos.";
        }
    }
}

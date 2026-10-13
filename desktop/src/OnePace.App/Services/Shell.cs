using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace OnePace.App.Services;

/// <summary>Hands URLs, magnets and files to whatever Windows has registered.</summary>
public static class Shell
{
    public const string DiscordUrl = "https://discord.gg/KHn6AbevZ2";
    public const string RedditUrl = "https://www.reddit.com/user/nicolasenjah/";
    public const string GitHubUrl = "https://github.com/Nicolaslahri/onepacedownloader";
    public const string MuhnMirrorUrl = "https://drive.google.com/drive/folders/1OiT81U_kJulO9ptcBJA0wdZTSQAq83Sl";
    public const string QBittorrentUrl = "https://www.qbittorrent.org/";
    public const string DnsGuideUrl = "https://github.com/Nicolaslahri/onepacedownloader#downloads-not-starting";

    public static bool Open(string target)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return false;
        }
    }

    public static void OpenFolder(string folder)
    {
        try { Directory.CreateDirectory(folder); } catch (Exception) { }
        Open(folder);
    }
}

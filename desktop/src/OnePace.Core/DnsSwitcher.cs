using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace OnePace.Core;

/// <summary>Switches the active network adapter's DNS to Cloudflare or Google
/// (or back to automatic) through netsh, behind a UAC prompt. Some ISPs block
/// the download CDN at the DNS level; a public resolver gets around that.</summary>
public static partial class DnsSwitcher
{
    public const string CloudflarePrimary = "1.1.1.1";
    public const string CloudflareSecondary = "1.0.0.1";
    public const string GooglePrimary = "8.8.8.8";
    public const string GoogleSecondary = "8.8.4.4";

    static (int Code, string Output) Run(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return (p.ExitCode, output);
        }
        catch (Exception e)
        {
            return (-1, e.Message);
        }
    }

    /// <summary>The first connected, dedicated adapter, as netsh names it.</summary>
    public static string? ActiveInterface()
    {
        var (code, output) = Run("interface", "show", "interface");
        if (code != 0) return null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("Admin State") || line.StartsWith("---")) continue;
            var parts = Columns().Split(line, 4);
            if (parts.Length < 4) continue;
            if (parts[1].Equals("connected", StringComparison.OrdinalIgnoreCase)
                && parts[2].Equals("dedicated", StringComparison.OrdinalIgnoreCase))
                return parts[3].Trim();
        }
        return null;
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Columns();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex Ipv4();

    public static (List<string> Servers, bool Dhcp) CurrentDns(string iface)
    {
        var (code, output) = Run("interface", "ipv4", "show", "dnsservers", $"name={iface}");
        if (code != 0) return (new(), false);
        var ips = Ipv4().Matches(output).Select(m => m.Value).Distinct().ToList();
        return (ips, output.Contains("dhcp", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Runs the command elevated. True if the user accepted the UAC
    /// prompt (not whether DNS actually changed).</summary>
    static bool RunElevated(string commandLine)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{commandLine}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            p?.WaitForExit(20000);
            return p != null;
        }
        catch (Win32Exception)
        {
            return false;   // UAC declined
        }
    }

    public static bool UseStatic(string iface, string primary, string secondary) =>
        RunElevated($"netsh interface ipv4 set dnsservers name=\"{iface}\" static {primary} primary && " +
                    $"netsh interface ipv4 add dnsservers name=\"{iface}\" {secondary} index=2");

    public static bool UseAutomatic(string iface) =>
        RunElevated($"netsh interface ipv4 set dnsservers name=\"{iface}\" dhcp");

    public static List<string> WaitForChange(string iface, List<string> before, double seconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(400);
            var now = CurrentDns(iface).Servers;
            if (!now.SequenceEqual(before)) return now;
        }
        return before;
    }
}

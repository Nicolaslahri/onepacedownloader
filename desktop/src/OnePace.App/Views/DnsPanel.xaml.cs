using System.Windows;
using System.Windows.Controls;
using OnePace.App.Services;
using OnePace.Core;

namespace OnePace.App;

/// <summary>One-click DNS switcher. netsh runs elevated behind a UAC prompt.</summary>
public partial class DnsPanel : UserControl
{
    readonly Settings _settings;
    readonly Action<string> _log;
    string? _iface;

    public DnsPanel(Settings settings, Action<string> log)
    {
        InitializeComponent();
        _settings = settings;
        _log = log;
        Loaded += async (_, _) => await RefreshStateAsync();
    }

    async Task RefreshStateAsync()
    {
        var (iface, ips, dhcp) = await Task.Run(() =>
        {
            var i = DnsSwitcher.ActiveInterface();
            if (i == null) return (null, new List<string>(), false);
            var (servers, auto) = DnsSwitcher.CurrentDns(i);
            return ((string?)i, servers, auto);
        });
        _iface = iface;
        bool enabled = iface != null;
        CloudflareButton.IsEnabled = GoogleButton.IsEnabled = RevertButton.IsEnabled = enabled;
        if (iface == null)
        {
            StateText.Text = "No active network found";
            IfaceText.Text = "Connect to Wi-Fi or Ethernet, then open this again.";
            DnsText.Text = "";
            StateDot.Visibility = Visibility.Collapsed;
            return;
        }
        bool cloudflare = ips.Contains(DnsSwitcher.CloudflarePrimary);
        bool google = ips.Contains(DnsSwitcher.GooglePrimary);
        StateDot.Visibility = cloudflare || google ? Visibility.Visible : Visibility.Collapsed;
        StateText.Text = cloudflare ? "Using Cloudflare DNS"
            : google ? "Using Google DNS"
            : dhcp ? "Using your provider's DNS (automatic)"
            : "Using a custom DNS server";
        IfaceText.Text = $"Network:  {iface}";
        DnsText.Text = "DNS:  " + (dhcp
            ? "automatic" + (ips.Count > 0 ? $"  ({string.Join(", ", ips)})" : "")
            : ips.Count > 0 ? string.Join(", ", ips) : "none set");
    }

    async void OnCloudflare(object sender, RoutedEventArgs e) =>
        await SwitchAsync(DnsSwitcher.CloudflarePrimary, DnsSwitcher.CloudflareSecondary, "Cloudflare");

    async void OnGoogle(object sender, RoutedEventArgs e) =>
        await SwitchAsync(DnsSwitcher.GooglePrimary, DnsSwitcher.GoogleSecondary, "Google");

    async Task SwitchAsync(string primary, string secondary, string name)
    {
        if (_iface is not { } iface) return;
        SetBusy(true, "Waiting for Windows to ask for permission…");
        var (accepted, after, before) = await Task.Run(() =>
        {
            var b = DnsSwitcher.CurrentDns(iface).Servers;
            if (!DnsSwitcher.UseStatic(iface, primary, secondary)) return (false, b, b);
            return (true, DnsSwitcher.WaitForChange(iface, b), b);
        });
        if (!accepted)
        {
            SetBusy(false, "Permission declined. Your DNS didn't change.");
            return;
        }
        if (!_settings.HasDnsBackup)
        {
            _settings.SetDnsBackup(iface, before);
            _settings.Save();
        }
        bool ok = after.Contains(primary);
        _log(ok ? $"DNS switched to {name} on {iface}." : $"DNS switch to {name} ran but didn't take effect on {iface}.");
        SetBusy(false, ok ? $"Switched to {name} at {DateTime.Now:HH:mm}."
                          : "The command ran but DNS didn't change. Check your Windows network settings.");
        await RefreshStateAsync();
    }

    async void OnRevert(object sender, RoutedEventArgs e)
    {
        if (_iface is not { } iface) return;
        SetBusy(true, "Waiting for Windows to ask for permission…");
        bool accepted = await Task.Run(() =>
        {
            var b = DnsSwitcher.CurrentDns(iface).Servers;
            if (!DnsSwitcher.UseAutomatic(iface)) return false;
            DnsSwitcher.WaitForChange(iface, b);
            return true;
        });
        if (accepted) _log($"DNS set back to automatic on {iface}.");
        SetBusy(false, accepted ? $"Back to automatic at {DateTime.Now:HH:mm}." : "Permission declined. Your DNS didn't change.");
        await RefreshStateAsync();
    }

    void SetBusy(bool busy, string message)
    {
        CloudflareButton.IsEnabled = GoogleButton.IsEnabled = RevertButton.IsEnabled = !busy && _iface != null;
        StatusText.Text = message;
    }

    void OnGuide(object sender, RoutedEventArgs e) => Shell.Open(Shell.DnsGuideUrl);
}

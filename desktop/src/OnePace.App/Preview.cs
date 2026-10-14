using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OnePace.App.ViewModels;
using OnePace.Core;

namespace OnePace.App;

/// <summary>Screenshot mode for CI: opens the app in a given state with a
/// throwaway config and no network calls, saves a PNG of the window, exits.
///
///   OnePaceDownloader.exe --preview main-dark --out shots --theme Dark
///       --source onepace --arc Alabasta --ep 3 --ticks 1,2,3 --extra dl,saved
/// </summary>
public sealed class PreviewSpec
{
    public string Name { get; private init; } = "preview";
    public string Out { get; private init; } = ".";
    public string Theme { get; private init; } = "Dark";
    public string Source { get; private init; } = SourceRules.OnePace;
    public string Arc { get; private init; } = "";
    public int Episode { get; private init; }
    public int[] Ticks { get; private init; } = Array.Empty<int>();
    public HashSet<string> Extra { get; private init; } = new();
    public double Width { get; private init; } = 1360;
    public double Height { get; private init; } = 860;
    public string WorkDir { get; } = Path.Combine(Path.GetTempPath(), "onepace-preview-" + Guid.NewGuid().ToString("N")[..8]);

    public static PreviewSpec? Parse(string[] args)
    {
        var map = new Dictionary<string, string>();
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i].StartsWith("--")) map[args[i][2..]] = args[++i];
        if (!map.TryGetValue("preview", out var name)) return null;
        var size = map.GetValueOrDefault("size", "1360x860").Split('x');
        var spec = new PreviewSpec
        {
            Name = name,
            Out = map.GetValueOrDefault("out", "."),
            Theme = map.GetValueOrDefault("theme", "Dark"),
            Source = map.GetValueOrDefault("source", SourceRules.OnePace),
            Arc = map.GetValueOrDefault("arc", ""),
            Episode = int.TryParse(map.GetValueOrDefault("ep"), out var ep) ? ep : 0,
            Ticks = map.GetValueOrDefault("ticks", "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray(),
            Extra = map.GetValueOrDefault("extra", "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(),
            Width = double.Parse(size[0]),
            Height = double.Parse(size[1]),
        };
        Directory.CreateDirectory(spec.WorkDir);
        return spec;
    }

    public void Fail(Exception e)
    {
        try
        {
            Directory.CreateDirectory(Out);
            File.WriteAllText(Path.Combine(Out, Name + ".error.txt"), e.ToString());
        }
        catch (Exception) { }
    }

    public void ApplyTo(Settings s)
    {
        s.Appearance = Theme;
        s.Source = Source;
        s.SaveFolder = Path.Combine(WorkDir, "One Pace");
        if (Extra.Contains("usenet")) s.UsenetApiKey = "0123456789abcdef";
    }

    public async Task RunAsync(MainWindow window, MainViewModel vm)
    {
        if (Extra.Contains("saved")) await FakeLibraryAsync(vm);
        var arc = vm.Arcs.FirstOrDefault(a => a.Title == Arc);
        if (arc != null) vm.SelectedArc = arc;
        if (Extra.Contains("packs")) vm.ShowPacks = true;
        foreach (var t in Ticks)
            if (vm.Episodes.FirstOrDefault(e => e.Num == t) is { } item) item.IsChecked = true;
        if (Episode > 0) vm.SelectedEpisode = vm.Episodes.FirstOrDefault(e => e.Num == Episode);
        if (Extra.Contains("update"))
        {
            vm.AppUpdateVersion = "3.1.0";
            vm.IndexUpdateAvailable = true;
        }
        if (Extra.Contains("log"))
        {
            vm.Log("Album: [One Pace][1-7] Romance Dawn [1080p] (3 files wanted)");
            vm.Log("  Already downloaded: [One Pace][1] Romance Dawn 01 [1080p][En Sub][A2F4E1C5].mp4");
            vm.LogVisible = true;
        }
        if (Extra.Contains("dl")) FakeDownload(vm);
        if (Extra.Contains("settings")) vm.SettingsOpen = true;

        // Dialogs stay open while the shot is taken, so they're not awaited.
        if (Extra.Contains("batch")) _ = vm.DownloadSelectedCommand.ExecuteAsync(null);
        if (Extra.Contains("dns")) _ = vm.OpenDnsCommand.ExecuteAsync(null);
        if (Extra.Contains("toast")) window.Toast("Download complete", "3 episodes saved to your library.", Services.ToastKind.Success);

        await Task.Delay(Extra.Contains("dns") ? 4000 : 1800);
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        try
        {
            Directory.CreateDirectory(Out);
            Capture(window, Path.Combine(Out, Name + ".png"));
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }

    /// <summary>Empty files named like the first episodes of the arc, so the
    /// Saved badges show.</summary>
    async Task FakeLibraryAsync(MainViewModel vm)
    {
        var arc = vm.Index.Arcs.FirstOrDefault(a => a.Title == Arc);
        if (arc == null) return;
        var folder = Path.Combine(vm.Settings.SaveFolder, $"{arc.Title} - Sub 1080p");
        Directory.CreateDirectory(folder);
        foreach (var ep in arc.Episodes.Take(2))
            if (ep.SourcesOf(vm.Kind).FirstOrDefault()?.Filename is { } name)
                File.WriteAllBytes(Path.Combine(folder, Files.Sanitize(name)), Array.Empty<byte>());
        await vm.RescanLibraryAsync();
    }

    static void FakeDownload(MainViewModel vm)
    {
        var d = new DownloadDetail { Title = $"{vm.SelectedArc?.Title ?? "Alabasta"}  ·  3 episodes (Sub 1080p)", Folder = vm.Settings.SaveFolder };
        d.Fraction = 0.42;
        d.Percent = "42%";
        d.Speed = "8.4 MB/s  ·  1m 52s left";
        d.Bytes = "612 MB of 1.43 GB";
        d.Files = "File 2 of 3";
        vm.Download = d;
        vm.ShowDownloadCommand.Execute(null);
        vm.Status = "[2/3]  612 MB  ·  8.4 MB/s";
    }

    // ------------------------------------------------- screen capture --

    static void Capture(Window window, string path)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out RECT r, Marshal.SizeOf<RECT>()) != 0)
            GetWindowRect(hwnd, out r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        BitBlt(mem, 0, 0, w, h, screen, r.Left, r.Top, 0x00CC0020 | 0x40000000 /* SRCCOPY | CAPTUREBLT */);
        SelectObject(mem, old);
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(source));
            using var fs = File.Create(path);
            enc.Save(fs);
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
}

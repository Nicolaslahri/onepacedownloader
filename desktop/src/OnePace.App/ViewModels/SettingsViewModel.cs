using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnePace.App.Services;
using OnePace.Core;

namespace OnePace.App.ViewModels;

/// <summary>The Settings page. Like Windows Settings, changes apply and save
/// as soon as you make them.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    readonly MainViewModel _main;
    readonly IUiHost _ui;
    readonly Settings _s;

    public SettingsViewModel(MainViewModel main, IUiHost ui)
    {
        _main = main;
        _ui = ui;
        _s = main.Settings;
        _usenetUrl = _s.UsenetIndexerUrl;
        _usenetKey = _s.UsenetApiKey;
    }

    public static IReadOnlyList<string> Versions => SourceRules.Versions;
    public static IReadOnlyList<string> Qualities => SourceRules.Qualities;
    public static IReadOnlyList<string> Themes { get; } = new[] { "System", "Light", "Dark" };
    public string AppVersion => AppInfo.Version;

    [ObservableProperty] private string _section = "";
    [ObservableProperty] private string _usenetUrl;
    [ObservableProperty] private string _usenetKey;
    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private bool _testOk;
    [ObservableProperty] private bool _testing;

    /// <summary>Raised when the theme setting changes, for the window to apply.</summary>
    public event Action<string>? ThemeChanged;

    public string DefaultVersion
    {
        get => _s.DefaultVersion;
        set
        {
            if (value == _s.DefaultVersion) return;
            _s.DefaultVersion = value;
            _main.TrySave();
            OnPropertyChanged();
            _main.OnDefaultsChanged();
        }
    }

    public string DefaultQuality
    {
        get => _s.DefaultQuality;
        set
        {
            if (value == _s.DefaultQuality) return;
            _s.DefaultQuality = value;
            _main.TrySave();
            OnPropertyChanged();
            _main.OnDefaultsChanged();
        }
    }

    public bool AllowSubFallback
    {
        get => _s.AllowSubFallback;
        set
        {
            _s.AllowSubFallback = value;
            _main.TrySave();
            OnPropertyChanged();
        }
    }

    public string Theme
    {
        get => _s.Appearance;
        set
        {
            if (value == _s.Appearance) return;
            _s.Appearance = value;
            _main.TrySave();
            OnPropertyChanged();
            ThemeChanged?.Invoke(value);
        }
    }

    public string SaveFolder => _s.SaveFolder;

    public bool Organize
    {
        get => _s.OrganizeForMediaServer;
        set
        {
            if (value == _s.OrganizeForMediaServer) return;
            _s.OrganizeForMediaServer = value;
            _main.TrySave();
            OnPropertyChanged();
            if (value) _ = OrganizeExistingAsync();
        }
    }

    public string LastRefresh => string.IsNullOrEmpty(_s.LastRefresh)
        ? "Not refreshed yet. The app uses the catalog it shipped with."
        : $"Last refreshed {_s.LastRefresh.Replace('T', ' ')}";

    public string CatalogBuilt => string.IsNullOrEmpty(_main.Index.ScrapedAt)
        ? $"{_main.Index.Arcs.Count} arcs"
        : $"{_main.Index.Arcs.Count} arcs, built {_main.Index.ScrapedAt}";

    partial void OnUsenetUrlChanged(string value)
    {
        _s.UsenetIndexerUrl = value.Trim();
        _main.TrySave();
        TestResult = "";
    }

    partial void OnUsenetKeyChanged(string value)
    {
        _s.UsenetApiKey = value.Trim();
        _main.TrySave();
        TestResult = "";
    }

    public void OnFolderChanged() => OnPropertyChanged(nameof(SaveFolder));

    public void OnCatalogRefreshed()
    {
        OnPropertyChanged(nameof(LastRefresh));
        OnPropertyChanged(nameof(CatalogBuilt));
    }

    [RelayCommand]
    Task PickFolder() => _main.PickFolderCommand.ExecuteAsync(null);

    [RelayCommand]
    async Task TestUsenet()
    {
        Testing = true;
        TestResult = "Testing…";
        TestOk = false;
        var (ok, message) = await Usenet.TestAsync(_s.UsenetIndexerUrl, _s.UsenetApiKey);
        TestOk = ok;
        TestResult = message;
        Testing = false;
    }

    [RelayCommand]
    async Task RedownloadCatalog()
    {
        _s.RemoteEtag = null;
        _main.TrySave();
        await _main.RefreshCommand.ExecuteAsync(null);
    }

    async Task OrganizeExistingAsync()
    {
        if (!await _ui.ConfirmAsync("Organize your library now?",
                "Episodes you download from now on go into One Pace / Season N folders with Plex-style names.\n\n"
                + "Move the episodes already in your save folder into that layout too?", "Organize now", "Not now"))
            return;
        var folder = _s.SaveFolder;
        var index = _main.Index;
        var lines = new List<string>();
        int moved = await Task.Run(() => PlexOrganizer.OrganizeExisting(folder, index, m => { lock (lines) lines.Add(m); }));
        foreach (var l in lines) _main.Log(l);
        await _main.RescanLibraryAsync();
        _ui.Toast("Library organized", moved == 0 ? "Nothing needed moving." : $"Moved {SourceRules.Plural(moved, "episode")}.",
                  ToastKind.Success);
    }
}

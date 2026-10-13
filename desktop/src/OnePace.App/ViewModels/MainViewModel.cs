using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnePace.App.Services;
using OnePace.Core;

namespace OnePace.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    readonly IUiHost _ui;
    Library _library = new();
    CancellationTokenSource? _downloadCts;
    bool _bulkChecking;
    bool _slowNoted;
    readonly HashSet<string> _nzbInFlight = new();
    readonly StringBuilder _log = new();

    public MainViewModel(IUiHost ui, Settings settings, EpisodeIndex index)
    {
        _ui = ui;
        Settings = settings;
        Index = index;
        Sources = new ObservableCollection<SourceItem>(SourceRules.Kinds.Select(k => new SourceItem { Kind = k }));
        ArcsView = CollectionViewSource.GetDefaultView(Arcs);
        ArcsView.Filter = o => o is ArcItem a && (SearchText.Length == 0
            || a.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        SettingsPage = new SettingsViewModel(this, ui);

        UpdateSourceCounts();
        _selectedSource = Sources.FirstOrDefault(s => s.Kind == settings.Source) ?? Sources[0];
        BuildArcs();
        ShowEpisodes();
        RefreshDetail();
        if (Index.Arcs.Count == 0)
            Log("No episode catalog found. Use Refresh to download it.");
    }

    // ------------------------------------------------------------ state --

    public Settings Settings { get; }
    public EpisodeIndex Index { get; private set; }
    public string AppVersion => AppInfo.Version;
    public ObservableCollection<SourceItem> Sources { get; }
    public ObservableCollection<ArcItem> Arcs { get; } = new();
    public ICollectionView ArcsView { get; }
    public ObservableCollection<EpisodeItem> Episodes { get; } = new();
    public ObservableCollection<PackItem> Packs { get; } = new();
    public SettingsViewModel SettingsPage { get; }

    public string Kind => SelectedSource?.Kind ?? SourceRules.OnePace;
    public bool IsNyaa => Kind == SourceRules.Nyaa;
    public string SourceBlurb => SourceRules.Blurb(Kind);
    public string SaveFolder => Settings.SaveFolder;
    public string ArcCount => Arcs.Count.ToString();

    [ObservableProperty] private SourceItem? _selectedSource;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private ArcItem? _selectedArc;
    [ObservableProperty] private EpisodeItem? _selectedEpisode;
    [ObservableProperty] private bool _showPacks;
    [ObservableProperty] private object? _detail;
    [ObservableProperty] private string _detailHeader = "";
    [ObservableProperty] private string _status = "Pick an arc on the left to get started.";
    [ObservableProperty] private string _episodesTitle = "Episodes";
    [ObservableProperty] private string _episodesSubtitle = "";
    [ObservableProperty] private CoverageBanner? _coverage;
    [ObservableProperty] private string _emptyTitle = "";
    [ObservableProperty] private string _emptyBody = "";
    [ObservableProperty] private string _noArcsText = "";
    [ObservableProperty] private string _selectionText = "";
    [ObservableProperty] private string _selectAllText = "Select all";
    [ObservableProperty] private string _downloadText = "Download selected";
    [ObservableProperty] private bool _hasChecked;
    [ObservableProperty] private DownloadDetail? _download;
    [ObservableProperty] private bool _settingsOpen;
    [ObservableProperty] private bool _logVisible;
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private bool _indexUpdateAvailable;
    [ObservableProperty] private bool _refreshing;
    [ObservableProperty] private string? _appUpdateVersion;
    [ObservableProperty] private string? _appUpdateUrl;

    public bool IsDownloading => Download != null;
    public bool HasEpisodes => Episodes.Count > 0 && !ShowPacks;
    public bool ShowEmpty => EmptyTitle.Length > 0;
    public string RefreshLabel => Refreshing ? "Checking…" : IndexUpdateAvailable ? "New episodes" : "Refresh";

    partial void OnDownloadChanged(DownloadDetail? value) => OnPropertyChanged(nameof(IsDownloading));
    partial void OnEmptyTitleChanged(string value) => OnPropertyChanged(nameof(ShowEmpty));
    partial void OnIndexUpdateAvailableChanged(bool value) => OnPropertyChanged(nameof(RefreshLabel));
    partial void OnRefreshingChanged(bool value) => OnPropertyChanged(nameof(RefreshLabel));
    partial void OnSearchTextChanged(string value) { ArcsView.Refresh(); UpdateNoArcs(); }

    partial void OnSelectedSourceChanged(SourceItem? value)
    {
        if (value == null) return;
        SettingsOpen = false;
        Settings.Source = value.Kind;
        TrySave();
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(IsNyaa));
        OnPropertyChanged(nameof(SourceBlurb));
        SelectedArc = null;
        BuildArcs();
        ShowEpisodes();
        RefreshDetail();
    }

    partial void OnSelectedArcChanged(ArcItem? value)
    {
        if (value != null && IsNyaa)
        {
            var cov = NyaaCoverage.Of(value.Arc);
            ShowPacks = cov.PerEpisode == 0 && cov.Packs > 0;
        }
        else if (!IsNyaa) ShowPacks = false;
        ShowEpisodes();
        if (Download == null || Detail != Download) RefreshDetail();
        if (value != null && Download == null && Status.StartsWith("Pick an arc"))
            Status = "Tick episodes, then hit Download.";
    }

    partial void OnShowPacksChanged(bool value)
    {
        ShowEpisodes();
        RefreshDetail();
    }

    partial void OnSelectedEpisodeChanged(EpisodeItem? value)
    {
        if (_quietEpisode) return;
        if (value != null || Download == null) RefreshDetail();
    }

    bool _quietEpisode;

    /// <summary>Drops the episode selection without redrawing the details
    /// pane; the caller decides what it shows next.</summary>
    void ClearEpisodeQuietly()
    {
        _quietEpisode = true;
        try { SelectedEpisode = null; }
        finally { _quietEpisode = false; }
    }

    // ------------------------------------------------------- arcs list --

    void UpdateSourceCounts()
    {
        foreach (var s in Sources)
        {
            int n = Index.Arcs.Count(a => SourceRules.ArcHas(a, s.Kind));
            s.Count = n > 0 ? n.ToString() : "";
        }
    }

    void BuildArcs()
    {
        Arcs.Clear();
        foreach (var arc in Index.Arcs.Where(a => SourceRules.ArcHas(a, Kind)))
        {
            var item = new ArcItem(arc, Seasons.ForArc(Index, arc.Title));
            UpdateArcMeta(item);
            Arcs.Add(item);
        }
        OnPropertyChanged(nameof(ArcCount));
        UpdateNoArcs();
    }

    void UpdateNoArcs()
    {
        bool any = ArcsView.Cast<object>().Any();
        NoArcsText = any ? ""
            : Index.Arcs.Count == 0 ? "No catalog yet. Use Refresh to download it."
            : SearchText.Length > 0 ? "No arcs match."
            : $"No arcs available from {SourceRules.Label(Kind)}.";
    }

    (int Saved, int Total) SavedCount(Arc arc)
    {
        if (Kind is SourceRules.Nyaa or SourceRules.Usenet) return (0, 0);
        var eps = arc.Episodes.Where(e => e.Has(Kind)).ToList();
        int? season = Seasons.ForArc(Index, arc.Title);
        return (eps.Count(e => _library.IsSaved(e, Kind, season)), eps.Count);
    }

    void UpdateArcMeta(ArcItem item)
    {
        var arc = item.Arc;
        if (Kind == SourceRules.Nyaa)
        {
            var cov = NyaaCoverage.Of(arc);
            string packs = cov.Packs > 0 ? SourceRules.Plural(cov.Packs, "pack") : "";
            item.Meta = cov.Total == 0 ? (packs.Length > 0 ? packs : "—")
                : cov.Packs == 0 ? $"{cov.PerEpisode}/{cov.Total} eps"
                : $"{cov.PerEpisode}/{cov.Total} · {packs}";
            item.Tone = MetaTone.Dim;
            return;
        }
        int eps = arc.Episodes.Count(e => e.Has(Kind));
        int packCount = SourceRules.PacksOf(arc, Kind).Count();
        var (saved, total) = SavedCount(arc);
        if (total > 0 && saved == total) { item.Meta = "✓ saved"; item.Tone = MetaTone.Complete; return; }
        if (saved > 0) { item.Meta = $"{saved}/{total}"; item.Tone = MetaTone.Partial; return; }
        var bits = new List<string>();
        if (eps > 0) bits.Add(SourceRules.Plural(eps, "ep"));
        if (packCount > 0) bits.Add(SourceRules.Plural(packCount, "pack"));
        item.Meta = string.Join(" · ", bits);
        item.Tone = MetaTone.Dim;
    }

    // --------------------------------------------------- episodes list --

    void ShowEpisodes()
    {
        foreach (var e in Episodes) e.PropertyChanged -= OnEpisodeChanged;
        Episodes.Clear();
        Packs.Clear();
        Coverage = null;
        EmptyTitle = EmptyBody = "";
        ClearEpisodeQuietly();

        var arcItem = SelectedArc;
        if (arcItem == null)
        {
            EpisodesTitle = "Episodes";
            EpisodesSubtitle = "Pick an arc on the left to see its episodes.";
            EmptyTitle = "No arc selected";
            EmptyBody = "Choose an arc from the list. Its episodes show up here. Tick the ones you want and hit Download.";
            AfterEpisodesChanged();
            return;
        }
        var arc = arcItem.Arc;
        EpisodesTitle = arc.Title;
        if (IsNyaa) Coverage = BuildCoverage(arc);

        if (IsNyaa && ShowPacks)
        {
            foreach (var p in SourceRules.PacksOf(arc, SourceRules.Nyaa)) Packs.Add(new PackItem(p));
            EpisodesSubtitle = $"{SourceRules.Plural(Packs.Count, "full-arc pack")} on Nyaa";
            if (Packs.Count == 0)
            {
                EmptyTitle = "No full-arc packs";
                EmptyBody = "Switch to Individual for per-episode torrents.";
            }
            AfterEpisodesChanged();
            return;
        }

        var version = Kind == SourceRules.OnePace ? Settings.DefaultVersion : SourceRules.Dub;
        var quality = Settings.DefaultQuality;
        foreach (var ep in arc.Episodes.Where(e => e.Has(Kind)))
        {
            var item = BuildEpisodeItem(ep, version, quality, arcItem.Season);
            item.PropertyChanged += OnEpisodeChanged;
            Episodes.Add(item);
        }

        var bits = new List<string>();
        if (arcItem.Season != null) bits.Add($"Season {arcItem.Season}");
        bits.Add(SourceRules.Plural(Episodes.Count, "episode"));
        bits.Add(SourceRules.Label(Kind));
        int saved = Episodes.Count(e => e.IsSaved);
        if (saved > 0 && Kind is SourceRules.OnePace or SourceRules.Muhn) bits.Add($"{saved} saved");
        EpisodesSubtitle = string.Join("  ·  ", bits);

        if (Episodes.Count == 0)
        {
            EmptyTitle = "Nothing here";
            EmptyBody = IsNyaa ? "No per-episode torrents. Switch to Packs above."
                               : $"No {SourceRules.Label(Kind)} episodes for this arc.";
        }
        AfterEpisodesChanged();
    }

    void AfterEpisodesChanged()
    {
        OnPropertyChanged(nameof(HasEpisodes));
        UpdateSelection();
    }

    EpisodeItem BuildEpisodeItem(Episode ep, string version, string quality, int? season)
    {
        var sources = ep.SourcesOf(Kind).ToList();
        string chip = "", detail;
        var chipKind = ChipKind.None;
        bool alsoDub = false;
        Source? best = null;
        switch (Kind)
        {
            case SourceRules.OnePace or SourceRules.Muhn:
                best = SourceRules.BestSource(ep, Kind, version, quality);
                if (best != null)
                {
                    chip = $"{SourceRules.VersionShort(best.Version, Kind)} {QualityLabel(best.Quality)}".Trim();
                    bool dub = Kind == SourceRules.Muhn || SourceRules.IsDub(best.Version);
                    chipKind = dub ? ChipKind.Dub : ChipKind.Sub;
                    detail = SourceRules.Size(best.SizeBytes);
                    alsoDub = !dub && sources.Any(s => SourceRules.IsDub(s.Version));
                }
                else detail = "no source";
                break;
            case SourceRules.Nyaa:
                best = SourceRules.BestTorrent(ep);
                if (best != null)
                {
                    if (best.IsOfficial) { chip = "Official"; chipKind = ChipKind.Ok; }
                    detail = $"{SourceRules.Size(best.SizeBytes)}  ·  ↑{best.Seeders}";
                }
                else detail = "no torrents";
                break;
            default:
                best = sources.MaxBy(s => SourceRules.QualityRank(s.Quality));
                if (best != null)
                {
                    chip = best.Quality ?? "";
                    chipKind = chip.Length > 0 ? ChipKind.Neutral : ChipKind.None;
                    detail = SourceRules.Size(best.SizeBytes) + (sources.Count > 1 ? $"  ·  +{sources.Count - 1}" : "");
                }
                else detail = "no NZB";
                break;
        }
        return new EpisodeItem(ep)
        {
            ChipText = chip, ChipKind = chipKind, AlsoDub = alsoDub, Detail = detail, Best = best,
            IsSaved = _library.IsSaved(ep, Kind, season),
        };
    }

    static CoverageBanner BuildCoverage(Arc arc)
    {
        var (perEp, total, packs) = NyaaCoverage.Of(arc);
        string p = SourceRules.Plural(packs, "full-arc pack");
        if (total == 0) return new("No data", "This arc has no canonical episode list.", false);
        if (perEp == total) return new("Full coverage", $"{total}/{total} episodes have per-episode torrents.", true);
        if (packs > 0 && perEp == 0)
            return new("Full coverage via pack", $"No per-episode torrents, but {p} cover{(packs == 1 ? "s" : "")} all {total} episodes.", true);
        if (packs > 0) return new("Full coverage", $"{perEp}/{total} per-episode, plus {p} to fill the gap.", true);
        return new("Partial coverage", $"{perEp}/{total} per-episode, {total - perEp} not available on Nyaa.", false);
    }

    void OnEpisodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EpisodeItem.IsChecked) && !_bulkChecking) UpdateSelection();
    }

    void UpdateSelection()
    {
        int total = Episodes.Count;
        var ticked = Episodes.Where(e => e.IsChecked).ToList();
        HasChecked = ticked.Count > 0;
        long bytes = 0;
        if (Kind is SourceRules.OnePace or SourceRules.Muhn)
        {
            var version = Kind == SourceRules.OnePace ? Settings.DefaultVersion : SourceRules.Dub;
            bytes = ticked.Sum(e => SourceRules.BestSource(e.Episode, Kind, version, Settings.DefaultQuality)?.SizeBytes ?? 0);
        }
        else bytes = ticked.Sum(e => e.Best?.SizeBytes ?? 0);
        SelectionText = ticked.Count == 0 ? "" : $"{ticked.Count} selected ({SourceRules.Size(bytes)})";

        string verb = Kind switch { SourceRules.Nyaa => "Open", SourceRules.Usenet => "Get", _ => "Download" };
        SelectAllText = total == 0 ? "Select all" : ticked.Count == total ? "Deselect all" : $"Select all ({total})";
        DownloadText = ticked.Count == 0 ? (Kind == SourceRules.Nyaa ? "Open selected" : Kind == SourceRules.Usenet ? "Get NZBs" : "Download selected")
            : ticked.Count == total ? $"{verb} {SourceRules.Plural(ticked.Count, "episode")}"
            : $"{verb} {ticked.Count} of {total}";
    }

    [RelayCommand]
    void ToggleSelectAll()
    {
        if (Episodes.Count == 0) return;
        bool value = !Episodes.Any(e => e.IsChecked);
        SetChecked(Episodes, value);
    }

    /// <summary>Shift+click on a checkbox ticks everything between it and the last one clicked.</summary>
    public void CheckRange(EpisodeItem from, EpisodeItem to, bool value)
    {
        int a = Episodes.IndexOf(from), b = Episodes.IndexOf(to);
        if (a < 0 || b < 0) return;
        SetChecked(Episodes.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1), value);
    }

    void SetChecked(IEnumerable<EpisodeItem> items, bool value)
    {
        _bulkChecking = true;
        try { foreach (var e in items) e.IsChecked = value; }
        finally { _bulkChecking = false; }
        UpdateSelection();
    }

    // ---------------------------------------------------- details pane --

    [RelayCommand]
    void ShowDownload()
    {
        if (Download == null) return;
        ClearEpisodeQuietly();
        DetailHeader = "Downloading";
        Detail = Download;
    }

    public void RefreshDetail()
    {
        var arcItem = SelectedArc;
        var epItem = SelectedEpisode;
        if (arcItem == null)
        {
            if (Download != null) { DetailHeader = "Downloading"; Detail = Download; return; }
            DetailHeader = "Welcome aboard";
            Detail = BuildWelcome();
            return;
        }
        if (epItem == null || (IsNyaa && ShowPacks))
        {
            if (Download != null) { DetailHeader = "Downloading"; Detail = Download; return; }
            DetailHeader = arcItem.Season != null ? $"Season {arcItem.Season}" : "Arc";
            Detail = BuildArcDetail(arcItem);
            return;
        }
        DetailHeader = $"{arcItem.Title}  ›  Episode {epItem.Num}";
        Detail = BuildEpisodeDetail(arcItem, epItem);
    }

    WelcomeDetail BuildWelcome()
    {
        var arcs = Index.Arcs.Where(a => SourceRules.ArcHas(a, Kind)).ToList();
        int eps = arcs.Sum(a => a.Episodes.Count(e => e.Has(Kind)));
        var stats = new List<StatTile> { new(arcs.Count.ToString(), "Arcs"), new(eps.ToString(), "Episodes") };
        if (Kind is SourceRules.OnePace or SourceRules.Muhn)
            stats.Add(new(arcs.Sum(a => SavedCount(a).Saved).ToString(), "Saved", true));
        return new WelcomeDetail { Blurb = SourceRules.Blurb(Kind), Stats = stats };
    }

    ArcDetail BuildArcDetail(ArcItem item)
    {
        var arc = item.Arc;
        int eps = arc.Episodes.Count(e => e.Has(Kind));
        int packs = SourceRules.PacksOf(arc, Kind).Count();
        var summary = SourceRules.Plural(eps, "episode")
                      + (packs > 0 ? $"  ·  {SourceRules.Plural(packs, "pack")}" : "")
                      + $"  ·  {SourceRules.Label(Kind)}";
        var facts = new List<StatTile>();
        bool library = Kind is SourceRules.OnePace or SourceRules.Muhn;
        var (saved, total) = SavedCount(arc);
        if (Kind == SourceRules.OnePace)
        {
            int sub = 0, dub = 0;
            long size = 0;
            foreach (var ep in arc.Episodes)
            {
                var vers = ep.SourcesOf(SourceRules.OnePace).Select(s => s.Version).ToList();
                if (vers.Any(SourceRules.IsDub)) dub++;
                if (vers.Contains(SourceRules.Sub)) sub++;
                size += SourceRules.BestSource(ep, SourceRules.OnePace, Settings.DefaultVersion, Settings.DefaultQuality)?.SizeBytes ?? 0;
            }
            facts.Add(new($"{sub}/{eps}", "Subbed"));
            facts.Add(new($"{dub}/{eps}", "Dubbed"));
            facts.Add(new(SourceRules.Size(size), "Whole arc"));
        }
        return new ArcDetail
        {
            Eyebrow = item.Season != null ? $"Season {item.Season}" : "Arc",
            Title = arc.Title,
            Summary = summary,
            ShowLibrary = library,
            LibraryText = $"{saved} / {total}",
            LibraryFraction = total > 0 ? (double)saved / total : 0,
            LibraryComplete = total > 0 && saved == total,
            Facts = facts,
            Tip = IsNyaa
                ? "Click an episode for its torrents, or switch to Packs for whole-arc downloads."
                : "Click an episode to see every version and quality, or tick several and use Download.",
        };
    }

    EpisodeDetail BuildEpisodeDetail(ArcItem arcItem, EpisodeItem item)
    {
        var ep = item.Episode;
        var sources = ep.SourcesOf(Kind).ToList();
        var plot = (ep.Plot ?? "").Trim();
        if (plot.Length > 600) plot = plot[..plot.LastIndexOf(' ', 599)] + "…";
        var versions = sources.Select(s => s.Version).ToList();
        var detail = new EpisodeDetail
        {
            Breadcrumb = $"{arcItem.Title}  ›  Episode {ep.Num}",
            Title = ep.Name,
            Plot = plot,
            IsSaved = item.IsSaved,
            DubAvailable = Kind == SourceRules.OnePace && versions.Any(SourceRules.IsDub),
            SubAvailable = Kind == SourceRules.OnePace && versions.Contains(SourceRules.Sub),
            Heading = Kind switch
            {
                SourceRules.OnePace => "Download",
                SourceRules.Muhn => "Download  ·  Muhn Pace dub",
                SourceRules.Nyaa => "Torrents",
                _ => "NZB releases",
            },
            Versions = Kind is SourceRules.OnePace or SourceRules.Muhn ? BuildVersionCards(ep, sources) : Array.Empty<VersionCard>(),
            Torrents = Kind == SourceRules.Nyaa
                ? sources.OrderByDescending(s => s.IsOfficial).ThenByDescending(s => s.Seeders).Select(s => new TorrentRow(ep, s)).ToList()
                : Array.Empty<TorrentRow>(),
            Nzbs = Kind == SourceRules.Usenet
                ? sources.OrderByDescending(s => SourceRules.QualityRank(s.Quality)).Select(s => new NzbRow(ep, s)).ToList()
                : Array.Empty<NzbRow>(),
            ShowMirror = Kind == SourceRules.Muhn,
            Empty = sources.Count == 0 ? $"No {SourceRules.Label(Kind)} options for this episode." : "",
        };
        return detail;
    }

    List<VersionCard> BuildVersionCards(Episode ep, List<Source> sources)
    {
        var best = SourceRules.BestSource(ep, Kind, Kind == SourceRules.OnePace ? Settings.DefaultVersion : SourceRules.Dub,
                                          Settings.DefaultQuality);
        var byVersion = sources
            .GroupBy(s => string.IsNullOrEmpty(s.Version) ? SourceRules.Dub : s.Version!)
            .OrderBy(g => Array.IndexOf(SourceRules.Versions, g.Key) is var i && i >= 0 ? i : 99);
        var cards = new List<VersionCard>();
        foreach (var g in byVersion)
        {
            var options = g.OrderByDescending(s => SourceRules.QualityRank(s.Quality))
                .Select(s => new QualityOption(ep, s,
                                               $"{(QualityLabel(s.Quality) is { Length: > 0 } q ? q : "Download")}  ·  {SourceRules.Size(s.SizeBytes)}",
                                               ReferenceEquals(s, best),
                                               $"Download {SourceRules.VersionLong(g.Key)} {s.Quality}"))
                .ToList();
            bool dub = SourceRules.IsDub(g.Key);
            cards.Add(new VersionCard(SourceRules.VersionLong(g.Key), SourceRules.VersionShort(g.Key, Kind),
                                      dub ? ChipKind.Dub : ChipKind.Sub, options));
        }
        return cards;
    }

    // ------------------------------------------------------- downloads --

    [RelayCommand]
    async Task DownloadOption(QualityOption? option)
    {
        if (option == null || SelectedArc == null) return;
        var s = option.Source;
        var title = $"{option.Episode.FullTitle}  ·  {SourceRules.VersionShort(s.Version, s.Kind ?? Kind)} {QualityLabel(s.Quality)}".TrimEnd();
        await RunDownloadsAsync(new[] { new PlannedEpisode(SelectedArc.Arc, option.Episode, s) }, title);
    }

    [RelayCommand]
    async Task DownloadSelected()
    {
        var arcItem = SelectedArc;
        if (arcItem == null) return;
        var chosen = Episodes.Where(e => e.IsChecked).Select(e => e.Episode).ToList();
        if (chosen.Count == 0)
        {
            await _ui.AlertAsync("Nothing selected", "Tick at least one episode in the list first.");
            return;
        }
        if (Kind == SourceRules.Nyaa) { await OpenMagnetsAsync(chosen); return; }
        if (Kind == SourceRules.Usenet) { await FetchNzbsAsync(chosen); return; }

        if (IsDownloading)
        {
            _ui.Toast("Already downloading", "Wait for the current download to finish, or cancel it.", ToastKind.Caution);
            return;
        }
        var saved = chosen.Where(e => _library.IsSaved(e, Kind, arcItem.Season)).Select(e => e.Num).ToHashSet();
        var choice = await _ui.AskBatchAsync(new BatchRequest(Kind, arcItem.Arc, chosen, Settings.DefaultVersion,
                                                              Settings.DefaultQuality, Settings.AllowSubFallback, saved));
        if (choice == null) return;
        if (choice.AllowSubs != Settings.AllowSubFallback)
        {
            Settings.AllowSubFallback = choice.AllowSubs;
            TrySave();
        }
        if (choice.SkipSaved && saved.Count > 0)
        {
            chosen = chosen.Where(e => !saved.Contains(e.Num)).ToList();
            Log($"Skipping {SourceRules.Plural(saved.Count, "episode")} already in your library.");
            if (chosen.Count == 0)
            {
                Status = "Everything you picked is already saved.";
                return;
            }
        }

        var version = Kind == SourceRules.OnePace ? choice.Version : SourceRules.Dub;
        var plan = new List<PlannedEpisode>();
        var skipped = new List<string>();
        foreach (var ep in chosen)
        {
            var src = SourceRules.BestSource(ep, Kind, version, choice.Quality, choice.AllowSubs);
            if (src == null) { skipped.Add(ep.FullTitle); continue; }
            if (!string.IsNullOrEmpty(src.Version) && src.Version != version)
                Log($"{ep.FullTitle}: no {SourceRules.VersionLong(version)}, getting {SourceRules.VersionLong(src.Version)}.");
            plan.Add(new PlannedEpisode(arcItem.Arc, ep, src));
        }
        string wanted = Kind == SourceRules.OnePace && !choice.AllowSubs ? SourceRules.VersionLong(version) : "download";
        if (plan.Count == 0)
        {
            await _ui.AlertAsync("Nothing to download", $"None of the selected episodes have a matching {wanted}.");
            return;
        }
        if (skipped.Count > 0)
            Log($"Skipping {SourceRules.Plural(skipped.Count, "episode")} with no {wanted}: "
                + string.Join(", ", skipped.Take(5)) + (skipped.Count > 5 ? "…" : ""));

        var label = Kind == SourceRules.OnePace ? SourceRules.VersionShort(version) : "Dub";
        var title = $"{arcItem.Title}  ·  {SourceRules.Plural(plan.Count, "episode")} ({label} {choice.Quality})";
        await RunDownloadsAsync(plan, title);
    }

    async Task RunDownloadsAsync(IReadOnlyList<PlannedEpisode> plan, string title)
    {
        if (IsDownloading)
        {
            _ui.Toast("Already downloading", "Wait for the current download to finish, or cancel it.", ToastKind.Caution);
            return;
        }
        var dest = Settings.SaveFolder;
        var problem = Files.CheckWritable(dest);
        if (problem != null)
        {
            await _ui.AlertAsync("Folder problem", problem);
            return;
        }

        var cts = new CancellationTokenSource();
        _downloadCts = cts;
        var detail = new DownloadDetail { Title = title, Folder = dest };
        Download = detail;
        ShowDownload();
        Status = "Starting…";
        Log($"Starting: {title}");
        var started = DateTime.UtcNow;
        var dispatcher = Application.Current.Dispatcher;
        var session = new DownloadSession(plan, dest, Index, Settings.OrganizeForMediaServer,
            log: m => dispatcher.InvokeAsync(() => Log(m)),
            status: s => dispatcher.InvokeAsync(() => Status = s),
            progress: p => dispatcher.InvokeAsync(() => ShowProgress(detail, p, started)));
        try
        {
            var result = await Task.Run(() => session.RunAsync(cts.Token));
            if (result.Cancelled)
            {
                Status = "Cancelled.";
                Log("Cancelled.");
            }
            else if (result.Failed.Count > 0)
            {
                Status = $"Finished with {SourceRules.Plural(result.Failed.Count, "problem")}.";
                LogVisible = true;
                await _ui.AlertAsync("Some downloads failed",
                    $"{SourceRules.Plural(result.Downloaded, "episode")} downloaded, {result.Failed.Count} failed:\n\n"
                    + string.Join("\n", result.Failed.Take(8)) + (result.Failed.Count > 8 ? "\n…" : "")
                    + "\n\nRun the download again to retry; finished files are kept. The log has the details.");
            }
            else
            {
                Status = plan.Count == 1 ? $"Done: {plan[0].Episode.FullTitle}" : "All done.";
                _ui.Toast("Download complete", plan.Count == 1 ? plan[0].Episode.FullTitle
                    : $"{SourceRules.Plural(result.Downloaded, "episode")} saved to your library.", ToastKind.Success);
            }
        }
        catch (Exception e)
        {
            Log("Error: " + e.Message);
            LogVisible = true;
            Status = "Download failed.";
            await _ui.AlertAsync("Download failed", e.Message);
        }
        finally
        {
            _downloadCts = null;
            cts.Dispose();
            bool showing = Detail == detail;
            Download = null;
            await RescanLibraryAsync();
            if (showing) RefreshDetail();
        }
    }

    void ShowProgress(DownloadDetail d, OverallProgress p, DateTime started)
    {
        if (Download != d) return;
        d.Fraction = p.Fraction;
        d.Percent = $"{(int)(p.Fraction * 100)}%";
        d.Bytes = p.BytesTotal > 0 ? $"{SourceRules.Bytes(p.BytesDone)} of {SourceRules.Bytes(p.BytesTotal)}" : SourceRules.Bytes(p.BytesDone);
        d.Files = p.FileCount > 1 ? $"File {p.FileIndex} of {p.FileCount}" : "";
        string speed = p.BytesPerSecond > 0 ? SourceRules.Bytes(p.BytesPerSecond) + "/s" : "…";
        if (p.BytesPerSecond > 0 && p.BytesTotal > p.BytesDone)
            speed += "  ·  " + Eta((p.BytesTotal - p.BytesDone) / p.BytesPerSecond);
        d.Speed = speed;
        if (!_slowNoted && (DateTime.UtcNow - started).TotalSeconds > 15 && p.BytesPerSecond is > 0 and < 300 * 1024)
            _slowNoted = true;
        d.Slow = _slowNoted;
        Status = $"[{p.FileIndex}/{p.FileCount}]  {SourceRules.Bytes(p.BytesDone)}  ·  {(p.BytesPerSecond > 0 ? SourceRules.Bytes(p.BytesPerSecond) + "/s" : "…")}";
    }

    /// <summary>"1080p", or nothing for labels like Muhn Pace's "varies".</summary>
    static string QualityLabel(string? quality) => SourceRules.QualityRank(quality) > 0 ? quality! : "";

    static string Eta(double seconds)
    {
        var s = (int)seconds;
        return s < 60 ? $"{s}s left" : s < 3600 ? $"{s / 60}m {s % 60}s left" : $"{s / 3600}h {s % 3600 / 60}m left";
    }

    [RelayCommand]
    void CancelDownload()
    {
        if (_downloadCts == null || Download == null) return;
        Download.Cancelling = true;
        Status = "Cancelling…";
        Log("Cancel requested…");
        _downloadCts.Cancel();
    }

    public void CancelForExit() => _downloadCts?.Cancel();

    // --------------------------------------------------------- torrents --

    [RelayCommand]
    async Task OpenTorrent(object? row)
    {
        var src = row switch { TorrentRow t => t.Source, PackItem p => p.Source, _ => null };
        if (src?.Magnet == null) return;
        if (Shell.Open(src.Magnet))
        {
            Log($"Sent to torrent client: {src.TorrentTitle ?? "magnet"}");
            Status = "Magnet sent to your torrent client.";
            _ui.Toast("Sent to your torrent app", src.TorrentTitle ?? "", ToastKind.Success);
        }
        else
        {
            CopyToClipboard(src.Magnet);
            await _ui.ShowNoTorrentClientAsync();
        }
    }

    async Task OpenMagnetsAsync(List<Episode> chosen)
    {
        var magnets = new List<(string Title, string Magnet)>();
        var skipped = new List<string>();
        foreach (var ep in chosen)
        {
            var best = SourceRules.BestTorrent(ep);
            if (best?.Magnet == null) skipped.Add(ep.FullTitle);
            else magnets.Add((ep.FullTitle, best.Magnet));
        }
        if (magnets.Count == 0)
        {
            await _ui.AlertAsync("No torrents", "None of the selected episodes have a Nyaa torrent.");
            return;
        }
        if (!await _ui.ConfirmAsync("Send to your torrent app",
                $"Open {SourceRules.Plural(magnets.Count, "magnet link")} in your torrent app (qBittorrent, uTorrent…)?\n\nMake sure it's running.",
                "Open magnets"))
            return;
        if (skipped.Count > 0)
            Log($"Skipped {SourceRules.Plural(skipped.Count, "episode")} without a Nyaa torrent: "
                + string.Join(", ", skipped.Take(5)) + (skipped.Count > 5 ? "…" : ""));
        for (int i = 0; i < magnets.Count; i++)
        {
            var (title, magnet) = magnets[i];
            if (!Shell.Open(magnet))
            {
                CopyToClipboard(magnet);
                Log("Stopped: no torrent app is registered for magnet links.");
                await _ui.ShowNoTorrentClientAsync();
                return;
            }
            Status = $"Sent {i + 1}/{magnets.Count}: {title}";
            await Task.Delay(250);
        }
        Status = $"Sent {SourceRules.Plural(magnets.Count, "magnet")}.";
        Log("All magnets sent.");
        _ui.Toast("Sent to your torrent app", SourceRules.Plural(magnets.Count, "magnet link"), ToastKind.Success);
    }

    static void CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); } catch (Exception) { /* clipboard busy */ }
    }

    // ------------------------------------------------------------ usenet --

    [RelayCommand]
    async Task GetNzb(NzbRow? row)
    {
        if (row == null) return;
        if (!Settings.UsenetConfigured) { await AskUsenetSetupAsync(); return; }
        await FetchNzbAsync(row.Source, single: true);
    }

    async Task AskUsenetSetupAsync()
    {
        if (await _ui.AskUsenetSetupAsync())
        {
            SettingsOpen = true;
            SettingsPage.Section = "usenet";
        }
    }

    async Task<bool> FetchNzbAsync(Source src, bool single)
    {
        var guid = src.Guid ?? "";
        if (!_nzbInFlight.Add(guid))
        {
            Status = "That NZB is already being fetched…";
            return true;
        }
        var title = src.ReleaseTitle ?? "release";
        Status = $"Fetching NZB: {(title.Length > 60 ? title[..60] + "…" : title)}";
        Log($"Usenet: {title}");
        try
        {
            var path = await Usenet.FetchNzbAsync(Settings.UsenetIndexerUrl, Settings.UsenetApiKey, src, Settings.SaveFolder);
            if (Shell.Open(path))
            {
                Log($"NZB handed to your Usenet app: {Path.GetFileName(path)}");
                Status = "NZB saved and opened in your Usenet app.";
                if (single) _ui.Toast("NZB sent to your Usenet app", Path.GetFileName(path), ToastKind.Success);
            }
            else
            {
                Shell.OpenFolder(Path.GetDirectoryName(path)!);
                Log("No app is registered for .nzb files, opened the folder instead.");
                Status = "NZB saved. Open it in SABnzbd or NZBGet.";
            }
            return true;
        }
        catch (Exception e)
        {
            Log($"NZB fetch failed: {e.Message}");
            Status = "Couldn't fetch the NZB.";
            await _ui.AlertAsync("Usenet", $"Couldn't fetch the NZB.\n\n{e.Message}");
            return false;
        }
        finally
        {
            _nzbInFlight.Remove(guid);
        }
    }

    async Task FetchNzbsAsync(List<Episode> chosen)
    {
        if (!Settings.UsenetConfigured) { await AskUsenetSetupAsync(); return; }
        var plan = new List<(string Title, Source Src)>();
        var skipped = new List<string>();
        foreach (var ep in chosen)
        {
            var best = SourceRules.BestNzb(ep, Settings.DefaultQuality);
            if (best == null) skipped.Add(ep.FullTitle);
            else plan.Add((ep.FullTitle, best));
        }
        if (plan.Count == 0)
        {
            await _ui.AlertAsync("No NZBs", "None of the selected episodes have a Usenet release.");
            return;
        }
        if (!await _ui.ConfirmAsync("Get NZBs",
                $"Download {SourceRules.Plural(plan.Count, ".nzb file")} and hand them to your Usenet app (SABnzbd, NZBGet)?\n\n"
                + "They're fetched one after another so your indexer doesn't rate-limit you.", "Get NZBs"))
            return;
        if (skipped.Count > 0)
            Log($"Skipped {SourceRules.Plural(skipped.Count, "episode")} without a Usenet release: "
                + string.Join(", ", skipped.Take(5)) + (skipped.Count > 5 ? "…" : ""));
        for (int i = 0; i < plan.Count; i++)
        {
            Status = $"NZB {i + 1}/{plan.Count}: {plan[i].Title}";
            if (!await FetchNzbAsync(plan[i].Src, single: false)) return;
            await Task.Delay(800);
        }
        Status = $"Queued {SourceRules.Plural(plan.Count, "NZB")}.";
        _ui.Toast("NZBs sent to your Usenet app", SourceRules.Plural(plan.Count, "release"), ToastKind.Success);
    }

    // ------------------------------------------------- catalog refresh --

    [RelayCommand]
    async Task Refresh()
    {
        if (Refreshing) return;
        if (IsDownloading)
        {
            _ui.Toast("Download in progress", "Refresh once it's finished.", ToastKind.Caution);
            return;
        }
        Refreshing = true;
        IndexUpdateAvailable = false;
        Status = "Checking for new episodes…";
        Log("Fetching the latest catalog from GitHub…");
        try
        {
            var r = await IndexStore.RefreshAsync(Settings.RemoteEtag);
            if (!string.IsNullOrEmpty(r.Etag)) Settings.RemoteEtag = r.Etag;
            Settings.LastRefresh = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            TrySave();
            SettingsPage.OnCatalogRefreshed();
            if (r.Changed)
            {
                int before = Index.Arcs.Sum(a => a.Episodes.Count);
                ApplyIndex(r.Index);
                int after = Index.Arcs.Sum(a => a.Episodes.Count);
                Log($"Catalog updated (built {r.Index.ScrapedAt ?? "?"}, {r.Index.Arcs.Count} arcs).");
                Status = "Catalog updated.";
                _ui.Toast("Catalog updated", after > before ? $"{SourceRules.Plural(after - before, "new episode")} added."
                                                              : "You have the latest episodes.", ToastKind.Success);
            }
            else
            {
                Log("Already up to date.");
                Status = "You're up to date.";
                _ui.Toast("You're up to date", "No new episodes since your last refresh.");
            }
        }
        catch (Exception e)
        {
            Log($"Refresh failed: {e.Message}");
            Status = "Couldn't reach GitHub. Check your connection and try again.";
            _ui.Toast("Couldn't refresh", "GitHub didn't answer. Check your connection.", ToastKind.Danger);
        }
        finally
        {
            Refreshing = false;
        }
    }

    void ApplyIndex(EpisodeIndex index)
    {
        var arcTitle = SelectedArc?.Title;
        var epNum = SelectedEpisode?.Num;
        Index = index;
        UpdateSourceCounts();
        BuildArcs();
        SelectedArc = Arcs.FirstOrDefault(a => a.Title == arcTitle);
        if (epNum != null) SelectedEpisode = Episodes.FirstOrDefault(e => e.Num == epNum);
        if (SelectedArc == null) { ShowEpisodes(); RefreshDetail(); }
    }

    public async Task CheckForUpdatesAsync()
    {
        try { IndexUpdateAvailable = await IndexStore.HasUpdateAsync(Settings.RemoteEtag); }
        catch (Exception) { /* offline: no badge */ }
        try
        {
            var rel = await AppUpdate.CheckAsync(AppInfo.Version);
            if (rel is { } r)
            {
                AppUpdateUrl = r.Url;
                AppUpdateVersion = r.Version;
                Log($"Version {r.Version} is available: {r.Url}");
            }
        }
        catch (Exception) { }
    }

    [RelayCommand]
    void OpenAppUpdate()
    {
        if (AppUpdateUrl != null) Shell.Open(AppUpdateUrl);
    }

    // ------------------------------------------------- library & folder --

    public async Task RescanLibraryAsync()
    {
        var folder = Settings.SaveFolder;
        _library = await Task.Run(() => Library.Scan(folder));
        foreach (var a in Arcs) UpdateArcMeta(a);
        var season = SelectedArc?.Season;
        foreach (var e in Episodes) e.IsSaved = _library.IsSaved(e.Episode, Kind, season);
        if (SelectedArc != null && !(IsNyaa && ShowPacks))
        {
            var bits = EpisodesSubtitle.Split("  ·  ").Where(b => !b.EndsWith(" saved")).ToList();
            int saved = Episodes.Count(e => e.IsSaved);
            if (saved > 0 && Kind is SourceRules.OnePace or SourceRules.Muhn) bits.Add($"{saved} saved");
            EpisodesSubtitle = string.Join("  ·  ", bits);
        }
        if (Detail is not DownloadDetail) RefreshDetail();
    }

    [RelayCommand]
    async Task PickFolder()
    {
        var chosen = _ui.PickFolder(Settings.SaveFolder);
        if (chosen == null) return;
        await SetSaveFolderAsync(chosen);
    }

    public async Task SetSaveFolderAsync(string folder)
    {
        Settings.SaveFolder = folder;
        TrySave();
        OnPropertyChanged(nameof(SaveFolder));
        SettingsPage.OnFolderChanged();
        var problem = Files.CheckWritable(folder);
        if (problem != null) await _ui.AlertAsync("Folder problem", problem);
        await RescanLibraryAsync();
    }

    [RelayCommand]
    void OpenFolder() => Shell.OpenFolder(Settings.SaveFolder);

    /// <summary>Default version or quality changed in Settings.</summary>
    public void OnDefaultsChanged()
    {
        var checkedNums = Episodes.Where(e => e.IsChecked).Select(e => e.Num).ToHashSet();
        var selected = SelectedEpisode?.Num;
        ShowEpisodes();
        SetChecked(Episodes.Where(e => checkedNums.Contains(e.Num)), true);
        if (selected != null) SelectedEpisode = Episodes.FirstOrDefault(e => e.Num == selected);
        if (Detail is not DownloadDetail) RefreshDetail();
    }

    // ---------------------------------------------------------- misc ui --

    [RelayCommand]
    void OpenSettings() => SettingsOpen = true;

    [RelayCommand]
    void CloseSettings() => SettingsOpen = false;

    [RelayCommand]
    Task OpenDns() => _ui.ShowDnsAsync();

    [RelayCommand]
    void ToggleLog() => LogVisible = !LogVisible;

    [RelayCommand]
    static void OpenUrl(string? url)
    {
        if (!string.IsNullOrEmpty(url)) Shell.Open(url);
    }

    public void Log(string message)
    {
        _log.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] ").AppendLine(message);
        if (_log.Length > 200_000) _log.Remove(0, _log.Length - 150_000);
        LogText = _log.ToString();
    }

    public void TrySave()
    {
        try { Settings.Save(); }
        catch (Exception e) { Log($"Couldn't save settings: {e.Message}"); }
    }
}

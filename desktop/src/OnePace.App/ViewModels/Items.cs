using CommunityToolkit.Mvvm.ComponentModel;
using OnePace.Core;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace OnePace.App.ViewModels;

/// <summary>A source in the navigation pane.</summary>
public sealed partial class SourceItem : ObservableObject
{
    public required string Kind { get; init; }
    public string Label => SourceRules.Label(Kind);

    public SymbolRegular Icon => Kind switch
    {
        SourceRules.OnePace => SymbolRegular.MoviesAndTv24,
        SourceRules.Muhn => SymbolRegular.PersonVoice24,
        SourceRules.Nyaa => SymbolRegular.ArrowDownload24,
        _ => SymbolRegular.Server24,
    };

    [ObservableProperty] private string _count = "";
}

public enum MetaTone { Dim, Partial, Complete }

/// <summary>A row in the arcs list.</summary>
public sealed partial class ArcItem : ObservableObject
{
    public ArcItem(Arc arc, int? season)
    {
        Arc = arc;
        Season = season;
    }

    public Arc Arc { get; }
    public int? Season { get; }
    public string Title => Arc.Title;
    public string Number => Season?.ToString() ?? "";

    [ObservableProperty] private string _meta = "";
    [ObservableProperty] private MetaTone _tone;
}

/// <summary>A row in the episodes list.</summary>
public sealed partial class EpisodeItem : ObservableObject
{
    public EpisodeItem(Episode episode) => Episode = episode;

    public Episode Episode { get; }
    public int Num => Episode.Num;
    public string NumText => Episode.Num.ToString("00");
    public string Title => Episode.Name;
    public string FullTitle => Episode.FullTitle;

    public string ChipText { get; init; } = "";
    public ChipKind ChipKind { get; init; } = ChipKind.None;
    public bool AlsoDub { get; init; }
    public string Detail { get; init; } = "";
    public Source? Best { get; init; }

    [ObservableProperty] private bool _isSaved;
    [ObservableProperty] private bool _isChecked;
}

/// <summary>A full-arc torrent pack (Nyaa, Packs view).</summary>
public sealed class PackItem
{
    public PackItem(Source source) => Source = source;

    public Source Source { get; }
    public string Title => Source.TorrentTitle ?? "(pack)";
    public string Size => SourceRules.Size(Source.SizeBytes);
    public string Seeders => $"↑ {Source.Seeders} seeders";
    public bool IsOfficial => Source.IsOfficial;
}

/// <summary>Nyaa coverage banner above the episode list.</summary>
public sealed record CoverageBanner(string Heading, string Body, bool Good);

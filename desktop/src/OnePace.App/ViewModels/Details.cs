using CommunityToolkit.Mvvm.ComponentModel;
using OnePace.Core;

namespace OnePace.App.ViewModels;

// What the details pane on the right shows. Each has a DataTemplate in
// MainWindow.xaml.

public sealed record StatTile(string Value, string Label, bool Highlight = false);

/// <summary>Nothing picked yet: a welcome with library stats.</summary>
public sealed class WelcomeDetail
{
    public required string Blurb { get; init; }
    public required IReadOnlyList<StatTile> Stats { get; init; }

    public IReadOnlyList<StatTile> Steps { get; } = new StatTile[]
    {
        new("1", "Pick a source in the side pane."),
        new("2", "Choose an arc from the list."),
        new("3", "Tick the episodes you want."),
        new("4", "Hit Download, pick a version and quality, done."),
    };
}

/// <summary>An arc is picked but no episode.</summary>
public sealed class ArcDetail
{
    public required string Eyebrow { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public bool ShowLibrary { get; init; }
    public string LibraryText { get; init; } = "";
    public double LibraryFraction { get; init; }
    public bool LibraryComplete { get; init; }
    public IReadOnlyList<StatTile> Facts { get; init; } = Array.Empty<StatTile>();
    public bool HasFacts => Facts.Count > 0;
    public required string Tip { get; init; }
}

/// <summary>One button per quality inside a version card.</summary>
public sealed record QualityOption(Episode Episode, Source Source, string Text, bool IsBest, string ToolTip);

public sealed record VersionCard(string Title, string Chip, ChipKind ChipKind, IReadOnlyList<QualityOption> Options);

public sealed record TorrentRow(Episode Episode, Source Source)
{
    public string Quality => Source.Quality ?? "—";
    public string Size => SourceRules.Size(Source.SizeBytes);
    public string Seeders => $"↑ {Source.Seeders} seeders";
    public bool IsOfficial => Source.IsOfficial;
}

public sealed record NzbRow(Episode Episode, Source Source)
{
    public string Quality => Source.Quality ?? "—";
    public string Size => SourceRules.Size(Source.SizeBytes);

    public string Release
    {
        get
        {
            var t = Source.ReleaseTitle ?? "";
            var tail = t.Contains(']') ? t[(t.LastIndexOf(']') + 1)..].Trim() : t;
            return string.IsNullOrEmpty(tail) ? "NZB" : tail;
        }
    }
}

/// <summary>An episode is picked: its plot and every way to get it.</summary>
public sealed class EpisodeDetail
{
    public required string Breadcrumb { get; init; }
    public required string Title { get; init; }
    public string Plot { get; init; } = "";
    public bool IsSaved { get; init; }
    public bool DubAvailable { get; init; }
    public bool SubAvailable { get; init; }
    public string Heading { get; init; } = "";
    public IReadOnlyList<VersionCard> Versions { get; init; } = Array.Empty<VersionCard>();
    public IReadOnlyList<TorrentRow> Torrents { get; init; } = Array.Empty<TorrentRow>();
    public IReadOnlyList<NzbRow> Nzbs { get; init; } = Array.Empty<NzbRow>();
    public bool ShowMirror { get; init; }
    public string Empty { get; init; } = "";
    public bool HasTorrents => Torrents.Count > 0;
    public bool HasNzbs => Nzbs.Count > 0;
    public bool HasTags => IsSaved || DubAvailable || SubAvailable;
}

/// <summary>A download is running.</summary>
public sealed partial class DownloadDetail : ObservableObject
{
    public required string Title { get; init; }
    public required string Folder { get; init; }

    [ObservableProperty] private double _fraction;
    [ObservableProperty] private string _percent = "0%";
    [ObservableProperty] private string _speed = "Starting…";
    [ObservableProperty] private string _bytes = "";
    [ObservableProperty] private string _files = "";
    [ObservableProperty] private bool _slow;
    [ObservableProperty] private bool _cancelling;
}

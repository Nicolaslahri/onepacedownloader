using System.Windows;
using System.Windows.Controls;
using OnePace.App.Services;
using OnePace.Core;

namespace OnePace.App;

/// <summary>"Download N episodes": version and quality for the batch, with
/// the total size and what will be skipped.</summary>
public partial class BatchDialog : UserControl
{
    readonly BatchRequest _req;
    readonly bool _ready;

    public BatchDialog(BatchRequest request)
    {
        InitializeComponent();
        _req = request;
        FromText.Text = $"{request.Arc.Title}  ·  from {SourceRules.Label(request.Kind)}";
        VersionPanel.Visibility = request.Kind == SourceRules.OnePace ? Visibility.Visible : Visibility.Collapsed;
        VersionBox.ItemsSource = SourceRules.Versions.Select(SourceRules.VersionLong).ToList();
        VersionBox.SelectedIndex = Math.Max(0, Array.IndexOf(SourceRules.Versions, request.Version));
        QualityBox.ItemsSource = SourceRules.Qualities;
        QualityBox.SelectedItem = request.Quality;
        AllowSubsBox.IsChecked = request.AllowSubs;
        int saved = request.SavedNums.Count;
        if (saved > 0)
        {
            SkipSavedBox.Content = $"Skip the {SourceRules.Plural(saved, "episode")} already in your library";
            SkipSavedBox.IsChecked = true;
            SkipSavedBox.Visibility = Visibility.Visible;
        }
        _ready = true;
        Update();
    }

    public string Version => _req.Kind == SourceRules.OnePace
        ? SourceRules.Versions[Math.Max(0, VersionBox.SelectedIndex)] : SourceRules.Dub;

    public BatchChoice Choice => new(Version, QualityBox.SelectedItem as string ?? "1080p",
                                     AllowSubsBox.IsChecked == true, SkipSavedBox.IsChecked == true);

    void OnChoiceChanged(object sender, RoutedEventArgs e)
    {
        if (_ready) Update();
    }

    void Update()
    {
        var c = Choice;
        long bytes = 0;
        int noSource = 0, subOnly = 0;
        foreach (var ep in _req.Episodes)
        {
            if (c.SkipSaved && _req.SavedNums.Contains(ep.Num)) continue;
            var best = SourceRules.BestSource(ep, _req.Kind, c.Version, c.Quality);
            if (best == null) { noSource++; continue; }
            if (SourceRules.IsDub(c.Version) && !string.IsNullOrEmpty(best.Version) && !SourceRules.IsDub(best.Version))
            {
                subOnly++;
                if (!c.AllowSubs) continue;
            }
            bytes += best.SizeBytes;
        }
        SizeText.Text = bytes > 0 ? SourceRules.Size(bytes) : "—";
        var notes = new List<string>();
        if (subOnly > 0)
            notes.Add($"{SourceRules.Plural(subOnly, "episode")} {(subOnly == 1 ? "has" : "have")} no English dub yet. "
                      + (c.AllowSubs ? "They'll download with Japanese audio and English subtitles." : "They'll be skipped."));
        if (noSource > 0)
            notes.Add($"{SourceRules.Plural(noSource, "episode")} {(noSource == 1 ? "has" : "have")} no matching download and will be skipped.");
        NotesText.Text = string.Join("\n", notes);
        NotesText.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AllowSubsBox.Visibility = subOnly > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

using OnePace.Core;
using Xunit;

namespace OnePace.Core.Tests;

/// <summary>Checks the bundled catalog: every One Pace and Muhn Pace episode
/// has to carry what the downloader needs.</summary>
public class CatalogTests
{
    static readonly EpisodeIndex Index =
        EpisodeIndex.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "episode_index.json")));

    [Fact]
    public void HasArcsAndEpisodes()
    {
        Assert.True(Index.Arcs.Count >= 30);
        Assert.True(Index.Arcs.Sum(a => a.Episodes.Count) > 300);
    }

    [Fact]
    public void EveryArcHasASeason()
    {
        foreach (var arc in Index.Arcs)
            Assert.NotNull(Seasons.ForArc(Index, arc.Title));
        var seasons = Index.Arcs.Select(a => Seasons.ForArc(Index, a.Title)).ToList();
        Assert.Equal(seasons.Count, seasons.Distinct().Count());
    }

    [Fact]
    public void EveryPixeldrainSourceIsDownloadable()
    {
        var problems = new List<string>();
        foreach (var arc in Index.Arcs)
            foreach (var ep in arc.Episodes)
                foreach (var s in ep.Sources.Where(s => s.Kind is "onepace" or "muhn"))
                {
                    if (string.IsNullOrEmpty(s.AlbumId) || string.IsNullOrEmpty(s.FileId) || string.IsNullOrEmpty(s.Filename))
                        problems.Add($"{arc.Title} {ep.Num}: missing album/file/name");
                    if (s.SizeBytes <= 0) problems.Add($"{arc.Title} {ep.Num}: no size");
                }
        Assert.Empty(problems);
    }

    [Fact]
    public void EveryOnePaceEpisodeHasADownloadForEveryDefault()
    {
        foreach (var arc in Index.Arcs)
            foreach (var ep in arc.Episodes.Where(e => e.Has("onepace")))
                foreach (var v in SourceRules.Versions)
                    foreach (var q in SourceRules.Qualities)
                        Assert.NotNull(SourceRules.BestSource(ep, "onepace", v, q));
    }

    [Fact]
    public void NoDuplicateFilesAfterNormalizing()
    {
        foreach (var ep in Index.Arcs.SelectMany(a => a.Episodes))
        {
            var ids = ep.Sources.Where(s => !string.IsNullOrEmpty(s.FileId)).Select(s => (s.Kind, s.FileId)).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
        }
    }
}

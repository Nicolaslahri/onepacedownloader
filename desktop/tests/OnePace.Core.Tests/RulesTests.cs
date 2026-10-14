using OnePace.Core;
using Xunit;

namespace OnePace.Core.Tests;

public class RulesTests
{
    static Source Px(string version, string quality, long size = 100) => new()
    {
        Kind = SourceRules.OnePace, Version = version, Quality = quality, SizeBytes = size,
        AlbumId = "a", FileId = version + quality, Filename = $"{version} {quality}.mp4",
    };

    static Episode Ep(params Source[] sources) => new() { Num = 1, Sources = sources.ToList() };

    [Fact]
    public void PreferredVersionAndQualityWin()
    {
        var ep = Ep(Px(SourceRules.Sub, "1080p"), Px(SourceRules.Sub, "720p"), Px(SourceRules.Dub, "720p"));
        Assert.Equal("720p", SourceRules.BestSource(ep, "onepace", SourceRules.Sub, "720p")!.Quality);
        Assert.Equal(SourceRules.Dub, SourceRules.BestSource(ep, "onepace", SourceRules.Dub, "1080p")!.Version);
    }

    [Fact]
    public void FallsBackToBestQualityOfSameVersion()
    {
        var ep = Ep(Px(SourceRules.Sub, "720p"), Px(SourceRules.Sub, "480p"));
        Assert.Equal("720p", SourceRules.BestSource(ep, "onepace", SourceRules.Sub, "1080p")!.Quality);
    }

    [Fact]
    public void DubsStandInForEachOtherBeforeSubs()
    {
        var ep = Ep(Px(SourceRules.Sub, "1080p"), Px(SourceRules.DubCc, "720p"));
        Assert.Equal(SourceRules.DubCc, SourceRules.BestSource(ep, "onepace", SourceRules.Dub, "1080p")!.Version);
    }

    [Fact]
    public void DubNeverFallsBackToSubWhenNotAllowed()
    {
        var ep = Ep(Px(SourceRules.Sub, "1080p"));
        Assert.Null(SourceRules.BestSource(ep, "onepace", SourceRules.Dub, "1080p", allowSubs: false));
        Assert.Equal(SourceRules.Sub, SourceRules.BestSource(ep, "onepace", SourceRules.Dub, "1080p")!.Version);
    }

    [Fact]
    public void MuhnHasNoVersionField()
    {
        var ep = Ep(new Source { Kind = "muhn", Quality = "720p", FileId = "m1" },
                    new Source { Kind = "muhn", Quality = "1080p", FileId = "m2" });
        Assert.Equal("1080p", SourceRules.BestSource(ep, "muhn", SourceRules.Dub, "1080p", allowSubs: false)!.Quality);
    }

    [Fact]
    public void OfficialTorrentBeatsMoreSeeds()
    {
        var ep = Ep(new Source { Kind = "nyaa", Seeders = 500, Magnet = "a" },
                    new Source { Kind = "nyaa", Seeders = 20, Magnet = "b", Uploader = "Galaxy9000" });
        Assert.Equal("b", SourceRules.BestTorrent(ep)!.Magnet);
    }

    [Theory]
    [InlineData("[One Pace][115-117] Little Garden 01 [1080p][En Sub][DEC3026A].mp4", "little garden 01 [1080p][en sub].mp4")]
    [InlineData("[One Pace][115-118] Little Garden 01 [1080p][En Sub][0BADF00D].mp4", "little garden 01 [1080p][en sub].mp4")]
    [InlineData("[One Pace][42,22] Gaimon [480p][EFF6059A].mkv", "gaimon [480p].mkv")]
    public void EpisodeKeySurvivesReRelease(string name, string key) => Assert.Equal(key, Files.EpisodeKey(name));

    [Fact]
    public void ReadsCrcFromName()
    {
        Assert.Equal(0xDEC3026Au, Files.ExpectedCrc("Little Garden 01 [1080p][En Sub][DEC3026A].mp4"));
        Assert.Null(Files.ExpectedCrc("no tag.mp4"));
    }

    [Theory]
    [InlineData("2.1.0", "2.0.5", true)]
    [InlineData("v3.0.0", "2.1.1", true)]
    [InlineData("2.1.1", "2.1.1", false)]
    [InlineData("2.1", "2.1.0", false)]
    [InlineData("2.0.10", "2.0.9", true)]
    public void ComparesVersions(string a, string b, bool newer) => Assert.Equal(newer, AppUpdate.IsNewer(a, b));

    [Fact]
    public void NzbNamesAreWindowsSafe()
    {
        Assert.Equal("a_b.nzb", Files.NzbFileName("a<>b", "g"));
        Assert.Equal("_CON.nzb", Files.NzbFileName("CON", "g"));
        Assert.Equal("guid1.nzb", Files.NzbFileName("...", "guid1"));
    }

    [Fact]
    public void SizesFormatLikeTheTiles()
    {
        Assert.Equal("171 MB", SourceRules.Size(171L * 1024 * 1024));
        Assert.Equal("1.50 GB", SourceRules.Size(1536L * 1024 * 1024));
    }

    [Fact]
    public void SeasonsArePinnedAndNewArcsGoAfter()
    {
        var index = new EpisodeIndex
        {
            Arcs = { new Arc { Title = "Romance Dawn" }, new Arc { Title = "Brand New Arc" }, new Arc { Title = "Egghead" } },
        };
        Assert.Equal(1, Seasons.ForArc(index, "Romance Dawn"));
        Assert.Equal(36, Seasons.ForArc(index, "Egghead"));
        Assert.Equal(37, Seasons.ForArc(index, "Brand New Arc"));
        Assert.Null(Seasons.ForArc(index, "Not here"));
    }
}

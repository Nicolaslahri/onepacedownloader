using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnePace.Core;

/// <summary>The per-episode catalog published on the repo's data branch.</summary>
public sealed class EpisodeIndex
{
    public string? ScrapedAt { get; set; }
    public List<Arc> Arcs { get; set; } = new();
    public List<Source> Specials { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static EpisodeIndex Parse(ReadOnlySpan<byte> utf8)
    {
        var index = JsonSerializer.Deserialize<EpisodeIndex>(utf8, Json)
                    ?? throw new InvalidDataException("Empty episode index");
        index.Normalize();
        return index;
    }

    /// <summary>One Pace albums sometimes list one file under several quality
    /// albums. Drop the repeats so each file shows up once.</summary>
    public void Normalize()
    {
        foreach (var arc in Arcs)
        {
            arc.Episodes ??= new();
            arc.ArcPacks ??= new();
            foreach (var ep in arc.Episodes)
            {
                ep.Sources ??= new();
                var seen = new HashSet<(string?, string)>();
                ep.Sources = ep.Sources
                    .Where(s => string.IsNullOrEmpty(s.FileId) || seen.Add((s.Kind, s.FileId!)))
                    .ToList();
            }
        }
    }
}

public sealed class Arc
{
    public string Title { get; set; } = "";
    public List<Episode> Episodes { get; set; } = new();
    public List<Source> ArcPacks { get; set; } = new();

    public override string ToString() => Title;
}

public sealed class Episode
{
    public int Num { get; set; }
    public string? DisplayTitle { get; set; }
    public string? CanonicalTitle { get; set; }
    public string? Plot { get; set; }
    public List<Source> Sources { get; set; } = new();

    /// <summary>"Romance Dawn, the Dawn of an Adventure", else "Romance Dawn 01".</summary>
    public string Name => !string.IsNullOrWhiteSpace(CanonicalTitle)
        ? CanonicalTitle!
        : DisplayTitle ?? $"Episode {Num:00}";

    /// <summary>"01  Romance Dawn, the Dawn of an Adventure" for logs and dialogs.</summary>
    public string FullTitle => !string.IsNullOrWhiteSpace(CanonicalTitle)
        ? $"{Num:00}  {CanonicalTitle}"
        : DisplayTitle ?? $"Episode {Num:00}";

    public IEnumerable<Source> SourcesOf(string kind) => Sources.Where(s => s.Kind == kind);
    public bool Has(string kind) => Sources.Any(s => s.Kind == kind);
}

/// <summary>A downloadable thing: a Pixeldrain file, a torrent or an NZB.
/// Which fields are set depends on <see cref="Kind"/>.</summary>
public sealed class Source
{
    public string? Kind { get; set; }
    public string? Version { get; set; }
    public string? Quality { get; set; }
    public long SizeBytes { get; set; }
    public string? SizeStr { get; set; }

    // One Pace / Muhn Pace (Pixeldrain)
    public string? AlbumId { get; set; }
    public string? FileId { get; set; }
    public string? Filename { get; set; }

    // Nyaa
    public string? Magnet { get; set; }
    public int Seeders { get; set; }
    public string? TorrentTitle { get; set; }
    public string? Uploader { get; set; }

    // Usenet
    public string? Guid { get; set; }
    public string? ReleaseTitle { get; set; }
    public string? PubDate { get; set; }

    [JsonIgnore]
    public bool IsOfficial => Uploader == SourceRules.NyaaOfficialUploader;
}

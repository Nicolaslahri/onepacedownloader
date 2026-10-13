namespace OnePace.Core;

public readonly record struct OverallProgress(
    double Fraction, long BytesDone, long BytesTotal,
    int FileIndex, int FileCount, double BytesPerSecond);

public sealed record PlannedEpisode(Arc Arc, Episode Episode, Source Source)
{
    /// <summary>"Alabasta - Sub 1080p", the folder the earlier versions used.</summary>
    public string Subfolder =>
        $"{Arc.Title} - {SourceRules.VersionShort(Source.Version, Source.Kind ?? SourceRules.OnePace)} {Source.Quality}".Trim();
}

public sealed class SessionResult
{
    public int Downloaded { get; set; }
    public List<string> Failed { get; } = new();
    public bool Cancelled { get; set; }
}

/// <summary>Runs a batch of episode downloads as one job: one progress
/// figure for everything queued, Plex/Jellyfin organizing after each album,
/// and a list of anything that failed instead of stopping at the first error.</summary>
public sealed class DownloadSession : IDownloadReporter
{
    readonly IReadOnlyList<PlannedEpisode> _plan;
    readonly string _dest;
    readonly EpisodeIndex _index;
    readonly bool _organize;
    readonly Action<string> _log;
    readonly Action<string> _status;
    readonly Action<OverallProgress> _progress;

    long _total, _finishedBytes, _currentBytes;
    int _finishedFiles, _currentIndex;

    public DownloadSession(IReadOnlyList<PlannedEpisode> plan, string dest, EpisodeIndex index, bool organize,
                           Action<string> log, Action<string> status, Action<OverallProgress> progress)
    {
        _plan = plan;
        _dest = dest;
        _index = index;
        _organize = organize;
        _log = log;
        _status = status;
        _progress = progress;
        _total = plan.Sum(p => p.Source.SizeBytes);
    }

    public long TotalBytes => _total;

    public async Task<SessionResult> RunAsync(CancellationToken ct)
    {
        var result = new SessionResult();
        var downloader = new Downloader(_dest, this, ct);
        // One request per album, files grouped by the folder they land in.
        var groups = _plan
            .Where(p => !string.IsNullOrEmpty(p.Source.AlbumId) && !string.IsNullOrEmpty(p.Source.FileId))
            .GroupBy(p => (p.Source.AlbumId!, p.Subfolder))
            .ToList();
        int fileCount = groups.Sum(g => g.Count());
        int nextIndex = 1;

        foreach (var g in groups)
        {
            if (ct.IsCancellationRequested) break;
            var job = new DownloadJob { AlbumId = g.Key.Item1, Subfolder = g.Key.Subfolder };
            foreach (var p in g) job.Files[p.Source.FileId!] = p.Source;
            try
            {
                var landed = await downloader.RunAsync(job, nextIndex, fileCount).ConfigureAwait(false);
                FlushFile();
                result.Downloaded += landed.Count;
                foreach (var p in g)
                    if (!landed.ContainsKey(p.Source.FileId!))
                        result.Failed.Add(p.Episode.FullTitle);
                if (_organize)
                    PlexOrganizer.Organize(_dest, g.First().Arc, _index,
                        g.Where(p => landed.ContainsKey(p.Source.FileId!))
                         .Select(p => (p.Episode, landed[p.Source.FileId!])), _log);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                result.Cancelled = true;
                break;
            }
            catch (Exception e)
            {
                FlushFile();
                _log($"  Error in album {job.AlbumId}: {e.Message}");
                foreach (var p in g) result.Failed.Add(p.Episode.FullTitle);
            }
            nextIndex += g.Count();
        }
        result.Cancelled |= ct.IsCancellationRequested;
        return result;
    }

    void FlushFile()
    {
        if (_currentIndex == 0) return;
        _finishedFiles++;
        _finishedBytes += _currentBytes;
        _currentIndex = 0;
        _currentBytes = 0;
    }

    void IDownloadReporter.Log(string message) => _log(message);
    void IDownloadReporter.Status(string message) => _status(message);

    void IDownloadReporter.Progress(FileProgress p)
    {
        if (_currentIndex != 0 && p.FileIndex != _currentIndex) FlushFile();
        _currentIndex = p.FileIndex;
        _currentBytes = p.FileBytes;
        long done = _finishedBytes + p.FileBytes;
        double frac = _total > 0
            ? (double)done / _total
            : (_finishedFiles + (p.FileSize > 0 ? (double)p.FileBytes / p.FileSize : 0)) / Math.Max(p.FileCount, 1);
        _progress(new OverallProgress(Math.Clamp(frac, 0, 1), done, _total,
            Math.Min(_finishedFiles + 1, p.FileCount), p.FileCount, p.BytesPerSecond));
    }
}

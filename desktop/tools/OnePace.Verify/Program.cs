// Checks that every One Pace and Muhn Pace episode in the catalog can still
// be downloaded, using the app's own downloader code.
//
//   dotnet run --project desktop/tools/OnePace.Verify -- [--index FILE] [--full N] [--summary FILE]
//
// 1. Lists every Pixeldrain album and matches each catalog file against it,
//    by ID or, for re-released episodes, by name (as the app does).
// 2. Reads the first 64 KB of one file per album from pixeldrain.com and
//    from the mirror, to show both hosts serve it.
// 3. Fully downloads the N smallest episodes through the real Downloader,
//    which checks size and CRC32.
// Exits 1 if any episode can't be resolved or a download fails.

using System.Diagnostics;
using System.Net;
using System.Text;
using OnePace.Core;

var argMap = new Dictionary<string, string>();
for (int i = 0; i + 1 < args.Length; i++)
    if (args[i].StartsWith("--")) argMap[args[i][2..]] = args[++i];
int fullCount = int.TryParse(argMap.GetValueOrDefault("full"), out var fc) ? fc : 3;

EpisodeIndex index;
if (argMap.TryGetValue("index", out var indexPath))
    index = EpisodeIndex.Parse(File.ReadAllBytes(indexPath));
else
{
    Console.WriteLine($"Fetching {IndexStore.RemoteIndexUrl}");
    index = EpisodeIndex.Parse(await Http.GetBytesAsync(IndexStore.RemoteIndexUrl, browser: false));
}
Console.WriteLine($"Catalog built {index.ScrapedAt}: {index.Arcs.Count} arcs");

var entries = (from arc in index.Arcs
               from ep in arc.Episodes
               from s in ep.Sources
               where s.Kind is SourceRules.OnePace or SourceRules.Muhn
               select (Arc: arc, Ep: ep, Src: s)).ToList();
var byAlbum = entries.Where(e => !string.IsNullOrEmpty(e.Src.AlbumId)).GroupBy(e => e.Src.AlbumId!).ToList();
Console.WriteLine($"{entries.Count} files in {byAlbum.Count} albums");

var problems = new List<string>();
var notes = new List<string>();
var status = new Dictionary<Source, string>();   // "ok", "renamed", "missing", "album"
var sampleByAlbum = new Dictionary<string, Downloader.AlbumFile>();
var sw = Stopwatch.StartNew();

foreach (var e in entries.Where(e => string.IsNullOrEmpty(e.Src.AlbumId) || string.IsNullOrEmpty(e.Src.FileId)))
{
    status[e.Src] = "missing";
    problems.Add($"{e.Arc.Title} {e.Ep.Num:00} ({Label(e.Src)}): no Pixeldrain album or file ID in the catalog");
}

int n = 0;
foreach (var album in byAlbum)
{
    n++;
    List<Downloader.AlbumFile> files;
    try
    {
        (_, files) = await Downloader.FetchAlbumAsync(album.Key, CancellationToken.None);
    }
    catch (Exception ex)
    {
        foreach (var e in album) status[e.Src] = "album";
        problems.Add($"Album {album.Key} ({album.First().Arc.Title}, {album.Count()} files) couldn't be listed: {ex.Message}");
        continue;
    }
    var byId = files.ToDictionary(f => f.Id);
    var byKey = new Dictionary<string, Downloader.AlbumFile>();
    foreach (var f in files) byKey.TryAdd(Files.EpisodeKey(f.Name), f);
    foreach (var e in album)
    {
        if (byId.TryGetValue(e.Src.FileId!, out var f))
        {
            status[e.Src] = "ok";
            if (e.Src.SizeBytes > 0 && f.Size > 0 && f.Size != e.Src.SizeBytes)
                notes.Add($"{e.Arc.Title} {e.Ep.Num:00} ({Label(e.Src)}): size changed {e.Src.SizeBytes} → {f.Size}");
            sampleByAlbum.TryAdd(album.Key, f);
        }
        else if (!string.IsNullOrEmpty(e.Src.Filename) && byKey.TryGetValue(Files.EpisodeKey(e.Src.Filename), out f))
        {
            status[e.Src] = "renamed";
            notes.Add($"{e.Arc.Title} {e.Ep.Num:00} ({Label(e.Src)}): re-released as {f.Name}; the app finds it by name");
            sampleByAlbum.TryAdd(album.Key, f);
        }
        else
        {
            status[e.Src] = "missing";
            problems.Add($"{e.Arc.Title} {e.Ep.Num:00} ({Label(e.Src)}): {e.Src.Filename} is gone from album {album.Key}");
        }
    }
    if (n % 20 == 0) Console.WriteLine($"  {n}/{byAlbum.Count} albums checked");
    await Task.Delay(250);
}
Console.WriteLine($"Albums checked in {sw.Elapsed.TotalSeconds:0}s");

// Both hosts serve the files: first 64 KB of one file per album.
int primaryOk = 0, mirrorOk = 0, rangeTotal = 0;
var hostFailures = new List<string>();
foreach (var (albumId, f) in sampleByAlbum)
{
    rangeTotal++;
    var p = await RangeCheck(string.Format(Downloader.FileUrl, f.Id), f.Size);
    var m = await RangeCheck(string.Format(Downloader.BypassUrl, f.Id), f.Size);
    if (p == null) primaryOk++;
    if (m == null) mirrorOk++;
    if (p != null && m != null)
        hostFailures.Add($"{f.Name}: pixeldrain.com {p}; mirror {m}");
    await Task.Delay(250);
}
foreach (var h in hostFailures) problems.Add("Neither host served " + h);
Console.WriteLine($"Range checks: pixeldrain.com {primaryOk}/{rangeTotal}, mirror {mirrorOk}/{rangeTotal}");

// Full downloads through the app's own downloader (size + CRC32 check).
var full = new List<(string Name, bool Ok, string Detail)>();
var picks = entries.Where(e => status.GetValueOrDefault(e.Src) is "ok" && e.Src.SizeBytes > 0)
    .GroupBy(e => e.Src.Kind == SourceRules.Muhn ? "muhn" : SourceRules.IsDub(e.Src.Version) ? "dub" : "sub")
    .SelectMany(g => g.OrderBy(e => e.Src.SizeBytes).Take(1))
    .Concat(entries.Where(e => status.GetValueOrDefault(e.Src) is "ok" && e.Src.SizeBytes > 0).OrderBy(e => e.Src.SizeBytes))
    .DistinctBy(e => e.Src.FileId)
    .Take(fullCount)
    .ToList();
var temp = Path.Combine(Path.GetTempPath(), "onepace-verify-" + Guid.NewGuid().ToString("N")[..8]);
foreach (var e in picks)
{
    var reporter = new ConsoleReporter();
    var dl = new Downloader(temp, reporter, CancellationToken.None);
    var job = new DownloadJob { AlbumId = e.Src.AlbumId!, Subfolder = "check" };
    job.Files[e.Src.FileId!] = e.Src;
    var t = Stopwatch.StartNew();
    try
    {
        var landed = await dl.RunAsync(job);
        var path = landed.GetValueOrDefault(e.Src.FileId!);
        bool ok = path != null && File.Exists(path);
        full.Add((e.Src.Filename ?? e.Src.FileId!, ok,
                  ok ? $"{SourceRules.Size(new FileInfo(path!).Length)} in {t.Elapsed.TotalSeconds:0}s, CRC32 matches" : "not saved"));
        if (!ok) problems.Add($"Full download of {e.Src.Filename} didn't produce a file");
    }
    catch (Exception ex)
    {
        full.Add((e.Src.Filename ?? e.Src.FileId!, false, ex.Message));
        problems.Add($"Full download of {e.Src.Filename} failed: {ex.Message}");
    }
}
try { Directory.Delete(temp, true); } catch (Exception) { }

// Report
var md = new StringBuilder();
md.AppendLine($"## Download check: {(problems.Count == 0 ? "every episode is downloadable ✅" : $"{problems.Count} problem(s) ❌")}");
md.AppendLine();
md.AppendLine($"Catalog built {index.ScrapedAt}. {entries.Count} files across {byAlbum.Count} Pixeldrain albums.");
md.AppendLine();
md.AppendLine("| # | Arc | Episodes | Files | Found | Re-released | Missing |");
md.AppendLine("|---|-----|---------:|------:|------:|------------:|--------:|");
int row = 0;
foreach (var arc in index.Arcs)
{
    var srcs = entries.Where(e => e.Arc == arc).ToList();
    if (srcs.Count == 0) continue;
    row++;
    int eps = srcs.Select(e => e.Ep.Num).Distinct().Count();
    int ok = srcs.Count(e => status.GetValueOrDefault(e.Src) == "ok");
    int ren = srcs.Count(e => status.GetValueOrDefault(e.Src) == "renamed");
    int bad = srcs.Count - ok - ren;
    md.AppendLine($"| {Seasons.ForArc(index, arc.Title)} | {arc.Title} | {eps} | {srcs.Count} | {ok} | {ren} | {(bad > 0 ? $"**{bad}**" : "0")} |");
}
md.AppendLine();
md.AppendLine($"Both hosts: pixeldrain.com served {primaryOk}/{rangeTotal} sample files, the mirror {mirrorOk}/{rangeTotal}.");
md.AppendLine();
md.AppendLine("Full downloads through the app's downloader:");
foreach (var f in full) md.AppendLine($"- {(f.Ok ? "✅" : "❌")} `{f.Name}`: {f.Detail}");
if (problems.Count > 0)
{
    md.AppendLine();
    md.AppendLine("### Problems");
    foreach (var p in problems.Take(200)) md.AppendLine($"- {p}");
}
if (notes.Count > 0)
{
    md.AppendLine();
    md.AppendLine($"<details><summary>{notes.Count} note(s)</summary>");
    md.AppendLine();
    foreach (var note in notes.Take(300)) md.AppendLine($"- {note}");
    md.AppendLine();
    md.AppendLine("</details>");
}
var report = md.ToString();
Console.WriteLine();
Console.WriteLine(report);
if (argMap.TryGetValue("summary", out var summary)) File.AppendAllText(summary, report);
return problems.Count == 0 ? 0 : 1;

static string Label(Source s) => s.Kind == SourceRules.Muhn
    ? $"Muhn {s.Quality}" : $"{SourceRules.VersionShort(s.Version)} {s.Quality}";

static async Task<string?> RangeCheck(string url, long size)
{
    long want = size > 0 ? Math.Min(size, 65536) : 65536;
    for (int attempt = 1; attempt <= 3; attempt++)
    {
        try
        {
            using var req = Http.Request(HttpMethod.Get, url, browser: true);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, want - 1);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var resp = await Http.Stream.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests) { await Task.Delay(3000 * attempt); continue; }
            if (resp.StatusCode != HttpStatusCode.PartialContent && resp.StatusCode != HttpStatusCode.OK)
                return $"HTTP {(int)resp.StatusCode}";
            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
            if (bytes.Length < want) return $"short read ({bytes.Length} of {want} bytes)";
            return null;
        }
        catch (Exception ex) when (attempt < 3)
        {
            _ = ex;
            await Task.Delay(2000 * attempt);
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }
    return "rate limited";
}

sealed class ConsoleReporter : IDownloadReporter
{
    DateTime _last;
    public void Log(string message) => Console.WriteLine(message);
    public void Status(string message) => Console.WriteLine(message);

    public void Progress(FileProgress p)
    {
        if ((DateTime.UtcNow - _last).TotalSeconds < 5 && p.FileBytes < p.FileSize) return;
        _last = DateTime.UtcNow;
        Console.WriteLine($"  {SourceRules.Bytes(p.FileBytes)} / {SourceRules.Bytes(p.FileSize)}  {SourceRules.Bytes(p.BytesPerSecond)}/s");
    }
}

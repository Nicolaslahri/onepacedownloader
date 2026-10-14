using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OnePace.Core;
using Xunit;

namespace OnePace.Core.Tests;

/// <summary>A stand-in for pixeldrain.com and its mirror on localhost.</summary>
sealed class FakePixeldrain : IDisposable
{
    readonly HttpListener _listener = new();
    public string Root { get; }
    public ConcurrentDictionary<string, byte[]> Files { get; } = new();
    public ConcurrentDictionary<string, string> Names { get; } = new();
    public Dictionary<string, List<string>> Albums { get; } = new();
    public bool ListFails { get; set; }
    public int PrimaryStatus { get; set; } = 200;
    public int CorruptNext { get; set; }
    public bool IgnoreRange { get; set; }
    public ConcurrentQueue<string> Hits { get; } = new();

    public FakePixeldrain()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        Root = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(Root);
        _listener.Start();
        _ = Task.Run(Loop);
        Downloader.ListApi = Root + "api/list/{0}";
        Downloader.FileUrl = Root + "api/file/{0}";
        Downloader.BypassUrl = Root + "mirror/{0}";
    }

    public static string CrcName(string stem, byte[] data)
    {
        var crc = Crc32.HashToUInt32(data);
        return $"[One Pace][1-2] {stem} [1080p][En Sub][{crc:X8}].mp4";
    }

    public (string Id, string Name) Add(string album, string id, string stem, int size)
    {
        var data = new byte[size];
        new Random(id.GetHashCode()).NextBytes(data);
        Files[id] = data;
        Names[id] = CrcName(stem, data);
        if (!Albums.TryGetValue(album, out var list)) Albums[album] = list = new();
        list.Add(id);
        return (id, Names[id]);
    }

    async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        Hits.Enqueue(path);
        var resp = ctx.Response;
        try
        {
            if (path.StartsWith("/api/list/"))
            {
                var album = path["/api/list/".Length..];
                if (ListFails || !Albums.TryGetValue(album, out var ids)) { resp.StatusCode = 500; return; }
                var json = JsonSerializer.Serialize(new
                {
                    success = true,
                    title = "Album " + album,
                    files = ids.Select(i => new { id = i, name = Names[i], size = Files[i].Length }),
                });
                var bytes = Encoding.UTF8.GetBytes(json);
                resp.ContentType = "application/json";
                resp.OutputStream.Write(bytes);
                return;
            }
            bool primary = path.StartsWith("/api/file/");
            var id = path[(primary ? "/api/file/".Length : "/mirror/".Length)..];
            if (primary && PrimaryStatus != 200) { resp.StatusCode = PrimaryStatus; return; }
            if (!Files.TryGetValue(id, out var data)) { resp.StatusCode = 404; return; }
            if (CorruptNext > 0)
            {
                CorruptNext--;
                data = (byte[])data.Clone();
                data[^1] ^= 0xFF;
            }
            long start = 0;
            var range = ctx.Request.Headers["Range"];
            if (range != null && !IgnoreRange)
            {
                start = long.Parse(range.Replace("bytes=", "").TrimEnd('-'));
                resp.StatusCode = 206;
            }
            resp.ContentLength64 = data.Length - start;
            resp.OutputStream.Write(data, (int)start, (int)(data.Length - start));
        }
        catch { }
        finally
        {
            try { resp.Close(); } catch { }
        }
    }

    public void Dispose() => _listener.Close();
}

sealed class Recorder : IDownloadReporter
{
    public List<string> Logs { get; } = new();
    public void Log(string message) { lock (Logs) Logs.Add(message); }
    public void Status(string message) { }
    public void Progress(FileProgress progress) { }
}

[Collection("network")]
public class DownloaderTests : IDisposable
{
    readonly FakePixeldrain _px = new();
    readonly string _dir = Path.Combine(Path.GetTempPath(), "op-dl-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _px.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    DownloadJob Job(string album, params (string Id, string Name)[] files)
    {
        var job = new DownloadJob { AlbumId = album, Subfolder = "Arc - Sub 1080p" };
        foreach (var (id, name) in files)
            job.Files[id] = new Source { Kind = "onepace", AlbumId = album, FileId = id, Filename = name, SizeBytes = _px.Files[id].Length };
        return job;
    }

    string Out(string name) => Path.Combine(_dir, "Arc - Sub 1080p", name);

    [Fact]
    public async Task DownloadsAndVerifiesFiles()
    {
        var a = _px.Add("A1", "f1", "Arc 01", 300_000);
        var b = _px.Add("A1", "f2", "Arc 02", 200_000);
        var rec = new Recorder();
        var landed = await new Downloader(_dir, rec, default).RunAsync(Job("A1", a, b));
        Assert.Equal(2, landed.Count);
        Assert.Equal(_px.Files["f1"], File.ReadAllBytes(Out(a.Name)));
        Assert.Equal(_px.Files["f2"], File.ReadAllBytes(Out(b.Name)));
    }

    [Fact]
    public async Task ResumesFromPartialFile()
    {
        var a = _px.Add("A1", "f1", "Arc 01", 400_000);
        Directory.CreateDirectory(Path.GetDirectoryName(Out(a.Name))!);
        File.WriteAllBytes(Out(a.Name) + ".part", _px.Files["f1"][..150_000]);
        await new Downloader(_dir, new Recorder(), default).RunAsync(Job("A1", a));
        Assert.Equal(_px.Files["f1"], File.ReadAllBytes(Out(a.Name)));
        Assert.False(File.Exists(Out(a.Name) + ".part"));
    }

    [Fact]
    public async Task RestartsWhenServerIgnoresRange()
    {
        var a = _px.Add("A1", "f1", "Arc 01", 250_000);
        _px.IgnoreRange = true;
        Directory.CreateDirectory(Path.GetDirectoryName(Out(a.Name))!);
        File.WriteAllBytes(Out(a.Name) + ".part", _px.Files["f1"][..100_000]);
        await new Downloader(_dir, new Recorder(), default).RunAsync(Job("A1", a));
        Assert.Equal(_px.Files["f1"], File.ReadAllBytes(Out(a.Name)));
    }

    [Fact]
    public async Task FallsBackToMirrorWhenRateLimited()
    {
        var a = _px.Add("A1", "f1", "Arc 01", 120_000);
        _px.PrimaryStatus = 429;
        var rec = new Recorder();
        await new Downloader(_dir, rec, default).RunAsync(Job("A1", a));
        Assert.Equal(_px.Files["f1"], File.ReadAllBytes(Out(a.Name)));
        Assert.Contains(_px.Hits, h => h.StartsWith("/mirror/"));
    }

    [Fact]
    public async Task RefetchesADamagedFile()
    {
        var a = _px.Add("A1", "f1", "Arc 01", 100_000);
        _px.CorruptNext = 1;
        var rec = new Recorder();
        await new Downloader(_dir, rec, default).RunAsync(Job("A1", a));
        Assert.Equal(_px.Files["f1"], File.ReadAllBytes(Out(a.Name)));
        Assert.Contains(rec.Logs, l => l.Contains("checksum mismatch"));
    }

    [Fact]
    public async Task FindsReReleasedEpisodeByName()
    {
        // The catalog knows "old1"; the album now holds "new1" under a new CRC.
        var current = _px.Add("A1", "new1", "Arc 01", 90_000);
        var catalogName = "[One Pace][1-2] Arc 01 [1080p][En Sub][00000000].mp4";
        var job = new DownloadJob { AlbumId = "A1", Subfolder = "Arc - Sub 1080p" };
        job.Files["old1"] = new Source { Kind = "onepace", AlbumId = "A1", FileId = "old1", Filename = catalogName, SizeBytes = 1 };
        var landed = await new Downloader(_dir, new Recorder(), default).RunAsync(job);
        Assert.Equal(Out(current.Name), landed["old1"]);
        Assert.Equal(_px.Files["new1"], File.ReadAllBytes(landed["old1"]));
    }

    [Fact]
    public async Task UsesCatalogIdsWhenAlbumCantBeListed()
    {
        var a = _px.Add("A1", "f1", "Arc 01", 80_000);
        _px.ListFails = true;
        var landed = await new Downloader(_dir, new Recorder(), default).RunAsync(Job("A1", a));
        Assert.Equal(_px.Files["f1"], File.ReadAllBytes(landed["f1"]));
    }

    [Fact]
    public async Task SkipsFilesAlreadyComplete()
    {
        var a = _px.Add("A1", "f1", "Arc 01", 50_000);
        Directory.CreateDirectory(Path.GetDirectoryName(Out(a.Name))!);
        File.WriteAllBytes(Out(a.Name), _px.Files["f1"]);
        var rec = new Recorder();
        await new Downloader(_dir, rec, default).RunAsync(Job("A1", a));
        Assert.DoesNotContain(_px.Hits, h => h.StartsWith("/api/file/") || h.StartsWith("/mirror/"));
    }

    [Fact]
    public async Task SessionReportsOverallProgressAndOrganizes()
    {
        var a = _px.Add("A1", "f1", "Arc 01", 60_000);
        var b = _px.Add("A2", "f2", "Arc 02", 40_000);
        var arc = new Arc { Title = "Romance Dawn" };
        Episode Ep(int n, string id, string name, string album) => new()
        {
            Num = n, CanonicalTitle = $"Title {n}", Plot = "plot",
            Sources = { new Source { Kind = "onepace", Version = SourceRules.Sub, Quality = "1080p", AlbumId = album, FileId = id, Filename = name, SizeBytes = _px.Files[id].Length } },
        };
        arc.Episodes.Add(Ep(1, "f1", a.Name, "A1"));
        arc.Episodes.Add(Ep(2, "f2", b.Name, "A2"));
        var index = new EpisodeIndex { Arcs = { arc } };
        var plan = arc.Episodes.Select(e => new PlannedEpisode(arc, e, e.Sources[0])).ToList();
        double last = 0;
        var session = new DownloadSession(plan, _dir, index, organize: true, _ => { }, _ => { }, p => last = p.Fraction);
        var result = await session.RunAsync(default);
        Assert.Equal(2, result.Downloaded);
        Assert.Empty(result.Failed);
        Assert.Equal(1.0, last, 3);
        var season = Path.Combine(_dir, "One Pace", "Season 1");
        Assert.True(File.Exists(Path.Combine(season, "One Pace - s01e01 - Title 1.mp4")));
        Assert.True(File.Exists(Path.Combine(season, "One Pace - s01e02 - Title 2.nfo")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "Romance Dawn - Sub 1080p")));

        var lib = Library.Scan(_dir);
        Assert.True(lib.IsSaved(arc.Episodes[0], "onepace", 1));
    }

    [Fact]
    public async Task CancelStopsTheSession()
    {
        var a = _px.Add("A1", "f1", "Arc 01", 50_000);
        var arc = new Arc { Title = "Romance Dawn" };
        var ep = new Episode { Num = 1, Sources = { new Source { Kind = "onepace", AlbumId = "A1", FileId = "f1", Filename = a.Name, SizeBytes = 50_000 } } };
        arc.Episodes.Add(ep);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var session = new DownloadSession(new[] { new PlannedEpisode(arc, ep, ep.Sources[0]) }, _dir,
            new EpisodeIndex { Arcs = { arc } }, false, _ => { }, _ => { }, _ => { });
        var result = await session.RunAsync(cts.Token);
        Assert.True(result.Cancelled);
    }
}

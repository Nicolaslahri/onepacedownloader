using System.Text;
using OnePace.Core;
using Xunit;

namespace OnePace.Core.Tests;

public class IndexStoreTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("onepace-index-").FullName;
    readonly string _oldAppDir = AppPaths.AppDir;

    public IndexStoreTests() => AppPaths.AppDir = _dir;

    public void Dispose()
    {
        AppPaths.AppDir = _oldAppDir;
        Directory.Delete(_dir, true);
    }

    static string Catalog(string scraped, string arc) =>
        $$"""{"scraped_at":"{{scraped}}","arcs":[{"title":"{{arc}}","episodes":[]}]}""";

    static Func<Stream?> Bundled(string json) => () => new MemoryStream(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void NewerBundledCatalogBeatsAnOldCache()
    {
        File.WriteAllText(AppPaths.IndexFile, Catalog("2026-01-01T00:00:00", "Old"));
        Assert.Equal("New", IndexStore.Load(Bundled(Catalog("2026-05-01T00:00:00", "New"))).Arcs[0].Title);
    }

    [Fact]
    public void RefreshedCacheBeatsTheBundledCatalog()
    {
        File.WriteAllText(AppPaths.IndexFile, Catalog("2026-09-01T00:00:00", "Fresh"));
        Assert.Equal("Fresh", IndexStore.Load(Bundled(Catalog("2026-05-01T00:00:00", "Bundled"))).Arcs[0].Title);
    }

    [Fact]
    public void DamagedCacheFallsBackToBundled()
    {
        File.WriteAllText(AppPaths.IndexFile, "{not json");
        Assert.Equal("Bundled", IndexStore.Load(Bundled(Catalog("2026-05-01T00:00:00", "Bundled"))).Arcs[0].Title);
    }
}

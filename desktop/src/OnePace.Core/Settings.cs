using System.Text.Json;
using System.Text.Json.Nodes;

namespace OnePace.Core;

public static class AppPaths
{
    /// <summary>Folder next to the .exe. The app is portable: settings and the
    /// cached catalog live beside it, same as the earlier versions.</summary>
    public static string AppDir { get; set; } = ResolveAppDir();

    static string ResolveAppDir()
    {
        var exe = Environment.ProcessPath;
        var dir = exe != null ? Path.GetDirectoryName(exe) : null;
        return string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : dir;
    }

    public static string ConfigFile => Path.Combine(AppDir, "config.json");
    public static string IndexFile => Path.Combine(AppDir, "episode_index.json");
    public static string DefaultDownloads => Path.Combine(AppDir, "downloads");

    /// <summary>Write to a temp file and swap it in, so a crash can't leave a
    /// half-written file behind.</summary>
    public static void AtomicWrite(string path, byte[] data)
    {
        lock (WriteLock)
        {
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, overwrite: true);
        }
    }

    static readonly object WriteLock = new();
}

/// <summary>config.json. Keys match the Python versions so an upgrade keeps
/// every setting; unknown keys are carried through untouched.</summary>
public sealed class Settings
{
    readonly JsonObject _data;
    readonly string _path;

    Settings(JsonObject data, string path)
    {
        _data = data;
        _path = path;
    }

    public static Settings Load(string? path = null)
    {
        path ??= AppPaths.ConfigFile;
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o)
                return new Settings(o, path);
        }
        catch (Exception) { /* unreadable: start fresh */ }
        return new Settings(new JsonObject(), path);
    }

    public void Save()
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(_data, new JsonSerializerOptions { WriteIndented = true });
            AppPaths.AtomicWrite(_path, bytes);
        }
        catch (Exception) { /* read-only folder: settings just don't persist */ }
    }

    string? Str(string key) => _data[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    bool Bool(string key, bool def = false) => _data[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : def;

    public string SaveFolder
    {
        get => Str("save_folder") is { Length: > 0 } s ? s : AppPaths.DefaultDownloads;
        set => _data["save_folder"] = value;
    }

    public string Source
    {
        get => Str("source") is { } s && SourceRules.Kinds.Contains(s) ? s : SourceRules.OnePace;
        set => _data["source"] = value;
    }

    public string DefaultVersion
    {
        get => Str("default_version") is { } s && SourceRules.Versions.Contains(s) ? s : SourceRules.Sub;
        set => _data["default_version"] = value;
    }

    public string DefaultQuality
    {
        get => Str("default_quality") is { } s && SourceRules.Qualities.Contains(s) ? s : "1080p";
        set => _data["default_quality"] = value;
    }

    /// <summary>System, Light or Dark.</summary>
    public string Appearance
    {
        get => Str("appearance") is "Light" or "Dark" ? Str("appearance")! : "System";
        set => _data["appearance"] = value;
    }

    public bool OrganizeForMediaServer
    {
        get => Bool("organize_for_media_server");
        set => _data["organize_for_media_server"] = value;
    }

    public bool AllowSubFallback
    {
        get => Bool("allow_sub_fallback");
        set => _data["allow_sub_fallback"] = value;
    }

    public string UsenetIndexerUrl
    {
        get => Str("usenet_indexer_url") ?? Usenet.DefaultIndexerUrl;
        set => _data["usenet_indexer_url"] = value;
    }

    public string UsenetApiKey
    {
        get => Str("usenet_api_key") ?? "";
        set => _data["usenet_api_key"] = value;
    }

    public bool UsenetConfigured => UsenetIndexerUrl.Length > 0 && UsenetApiKey.Length > 0;

    JsonObject RefreshCache
    {
        get
        {
            if (_data["refresh_cache"] is not JsonObject rc)
                _data["refresh_cache"] = rc = new JsonObject();
            return rc;
        }
    }

    public string? RemoteEtag
    {
        get => RefreshCache["remote_etag"]?.GetValue<string>();
        set => RefreshCache["remote_etag"] = value;
    }

    public string? LastRefresh
    {
        get => RefreshCache["last_refresh"]?.GetValue<string>();
        set => RefreshCache["last_refresh"] = value;
    }

    public WindowPlacement? Window
    {
        get
        {
            if (_data["window"] is not JsonObject w) return null;
            try
            {
                return new WindowPlacement(w["x"]!.GetValue<double>(), w["y"]!.GetValue<double>(),
                    w["width"]!.GetValue<double>(), w["height"]!.GetValue<double>(),
                    w["maximized"]?.GetValue<bool>() ?? false);
            }
            catch (Exception) { return null; }
        }
        set => _data["window"] = value is null ? null : new JsonObject
        {
            ["x"] = value.X, ["y"] = value.Y, ["width"] = value.Width,
            ["height"] = value.Height, ["maximized"] = value.Maximized,
        };
    }

    public bool HasDnsBackup => _data.ContainsKey("dns_backup");

    public void SetDnsBackup(string iface, IEnumerable<string> ips)
    {
        var arr = new JsonArray();
        foreach (var ip in ips) arr.Add(ip);
        _data["dns_backup"] = new JsonObject { ["iface"] = iface, ["ips"] = arr };
    }
}

public sealed record WindowPlacement(double X, double Y, double Width, double Height, bool Maximized);

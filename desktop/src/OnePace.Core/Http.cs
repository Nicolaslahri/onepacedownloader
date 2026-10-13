using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace OnePace.Core;

/// <summary>Shared HTTP plumbing. Windows' certificate store is refreshed
/// lazily, so a PC can be missing ISRG Root X2, which Pixeldrain's chain now
/// runs through. When the system chain fails we retry the check against the
/// Let's Encrypt roots we ship. Anything else (corporate TLS inspection,
/// antivirus HTTPS scanning) still goes through the system store.</summary>
public static class Http
{
    public static string AppVersion { get; set; } = "3.0.0";

    public const string BrowserUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";

    static readonly Lazy<X509Certificate2Collection> Roots = new(LoadRoots);

    static X509Certificate2Collection LoadRoots()
    {
        var col = new X509Certificate2Collection();
        using var s = typeof(Http).Assembly.GetManifestResourceStream("isrg-roots.pem");
        if (s == null) return col;
        using var r = new StreamReader(s);
        col.ImportFromPem(r.ReadToEnd());
        return col;
    }

    internal static bool ValidateCertificate(X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (errors != SslPolicyErrors.RemoteCertificateChainErrors || cert == null) return false;
        using var custom = new X509Chain();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.CustomTrustStore.AddRange(Roots.Value);
        custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (chain != null)
            foreach (var el in chain.ChainElements)
                custom.ChainPolicy.ExtraStore.Add(el.Certificate);
        return custom.Build(cert as X509Certificate2 ?? new X509Certificate2(cert));
    }

    static HttpClient Create(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, c, ch, e) => ValidateCertificate(c, ch, e),
            },
        };
        return new HttpClient(handler) { Timeout = timeout };
    }

    /// <summary>For API calls and small files.</summary>
    public static HttpClient Api { get; } = Create(TimeSpan.FromSeconds(60));

    /// <summary>For long downloads: no overall timeout, the reader handles stalls.</summary>
    public static HttpClient Stream { get; } = Create(Timeout.InfiniteTimeSpan);

    public static HttpRequestMessage Request(HttpMethod method, string url, bool browser = false)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.UserAgent.ParseAdd(browser ? BrowserUa : $"OnePaceDownloader/{AppVersion}");
        if (browser)
        {
            req.Headers.Accept.ParseAdd("text/html,application/json;q=0.9,*/*;q=0.8");
            req.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));
        }
        return req;
    }

    static bool Transient(HttpStatusCode code) =>
        (int)code >= 500 || code == HttpStatusCode.RequestTimeout || code == HttpStatusCode.TooManyRequests;

    /// <summary>GET with retries for rate limits, server hiccups and dropped
    /// connections. A 403/404 won't change by waiting, so those throw at once.</summary>
    public static async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default,
                                                   int retries = 5, bool browser = true)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < retries; attempt++)
        {
            try
            {
                using var req = Request(HttpMethod.Get, url, browser);
                using var resp = await Api.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                    return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                var err = new HttpRequestException($"HTTP {(int)resp.StatusCode} from {new Uri(url).Host}", null, resp.StatusCode);
                if (!Transient(resp.StatusCode)) throw err;
                last = err;
            }
            catch (HttpRequestException e) when (e.StatusCode == null && !IsCertError(e))
            {
                last = e;
            }
            catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
            {
                last = e;   // timeout
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 15)), ct).ConfigureAwait(false);
        }
        throw last ?? new HttpRequestException("Request failed");
    }

    public static bool IsCertError(Exception e) =>
        e is System.Security.Authentication.AuthenticationException
        || e.InnerException is System.Security.Authentication.AuthenticationException;
}

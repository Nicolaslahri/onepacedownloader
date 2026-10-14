using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace OnePace.Core;

/// <summary>Shared HTTP plumbing. Windows' certificate store is refreshed
/// lazily, so a PC can be missing ISRG Root X2, which Pixeldrain's chain now
/// runs through. When the system chain fails we retry the check against the
/// Let's Encrypt roots we ship. Anything else (corporate TLS inspection,
/// antivirus HTTPS scanning) still goes through the system store.
///
/// .NET tries a host's addresses one at a time, IPv6 first, so on a network
/// with broken IPv6 every request to a dual-stack host (GitHub, jsDelivr)
/// hangs until the connect timeout. <see cref="ConnectAsync"/> races the
/// addresses instead, the way browsers do.</summary>
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
            ConnectCallback = ConnectAsync,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, c, ch, e) => ValidateCertificate(c, ch, e),
            },
        };
        return new HttpClient(handler) { Timeout = timeout };
    }

    /// <summary>Happy Eyeballs (RFC 8305), simplified: IPv4 and IPv6 addresses
    /// interleaved, IPv4 first, a new attempt every 250 ms while the earlier
    /// ones are still pending, and the first connection to succeed wins.</summary>
    static async ValueTask<System.IO.Stream> ConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var host = ctx.DnsEndPoint;
        IPAddress[] addresses = IPAddress.TryParse(host.Host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(host.Host, ct).ConfigureAwait(false);
        var ordered = Interleave(addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList(),
                                 addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).ToList());
        if (ordered.Count == 0) throw new SocketException((int)SocketError.HostNotFound);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = new List<Task<Socket>>();
        Exception? last = null;
        int next = 0;
        try
        {
            while (next < ordered.Count || pending.Count > 0)
            {
                if (next < ordered.Count) pending.Add(TryConnectAsync(ordered[next++], host.Port, cts.Token));
                var stagger = next < ordered.Count ? Task.Delay(250, cts.Token) : null;
                while (pending.Count > 0)
                {
                    var waitOn = stagger == null ? pending.ToArray<Task>() : pending.Append<Task>(stagger).ToArray();
                    var done = await Task.WhenAny(waitOn).ConfigureAwait(false);
                    if (done == stagger) break;
                    var attempt = (Task<Socket>)done;
                    pending.Remove(attempt);
                    if (attempt.Status == TaskStatus.RanToCompletion)
                        return new NetworkStream(attempt.Result, ownsSocket: true);
                    last = attempt.Exception?.InnerException ?? last;
                }
            }
        }
        finally
        {
            cts.Cancel();
            foreach (var loser in pending)
                _ = loser.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) t.Result.Dispose(); },
                                       TaskScheduler.Default);
        }
        ct.ThrowIfCancellationRequested();
        throw last ?? new SocketException((int)SocketError.HostUnreachable);
    }

    static async Task<Socket> TryConnectAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal static List<IPAddress> Interleave(List<IPAddress> first, List<IPAddress> second)
    {
        var list = new List<IPAddress>(first.Count + second.Count);
        for (int i = 0; i < Math.Max(first.Count, second.Count); i++)
        {
            if (i < first.Count) list.Add(first[i]);
            if (i < second.Count) list.Add(second[i]);
        }
        return list;
    }

    /// <summary>A short, plain reason a request failed, for the status bar and log.</summary>
    public static string Describe(Exception e)
    {
        if (e is AggregateException { InnerException: { } inner }) return Describe(inner);
        if (IsCertError(e))
            return "the secure connection was refused. Antivirus HTTPS scanning or a wrong PC clock can cause this";
        if (e is HttpRequestException { StatusCode: { } code })
            return $"the server answered HTTP {(int)code}";
        if (e is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError }
            || e.InnerException is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData }
            || e is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData })
            return "the address couldn't be looked up. Your DNS may be blocking it; try the DNS switcher";
        if (e is TaskCanceledException or TimeoutException || e.InnerException is TimeoutException)
            return "the server took too long to answer";
        if (e is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } || e.InnerException is SocketException)
            return "the connection was refused or dropped";
        if (e is System.Text.Json.JsonException or InvalidDataException)
            return "the download was damaged";
        return e.Message;
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

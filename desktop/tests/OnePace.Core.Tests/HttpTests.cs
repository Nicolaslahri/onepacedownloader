using System.Net;
using System.Net.Sockets;
using System.Text;
using OnePace.Core;
using Xunit;

namespace OnePace.Core.Tests;

public class HttpTests
{
    [Fact]
    public void AddressesAlternateFamiliesIPv4First()
    {
        var v4 = new List<IPAddress> { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("1.0.0.1"), IPAddress.Parse("9.9.9.9") };
        var v6 = new List<IPAddress> { IPAddress.Parse("2606:4700::1111") };
        Assert.Equal(new[] { "1.1.1.1", "2606:4700::1111", "1.0.0.1", "9.9.9.9" },
                     Http.Interleave(v4, v6).Select(a => a.ToString()));
    }

    [Fact]
    public void DescribesCommonFailures()
    {
        Assert.Contains("HTTP 404", Http.Describe(new HttpRequestException("x", null, HttpStatusCode.NotFound)));
        Assert.Contains("DNS", Http.Describe(new HttpRequestException(HttpRequestError.NameResolutionError, "x")));
        Assert.Contains("DNS", Http.Describe(new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound))));
        Assert.Contains("too long", Http.Describe(new TimeoutException("x")));
        Assert.Contains("damaged", Http.Describe(new InvalidDataException("x")));
    }

    [Fact]
    public async Task ConnectsThroughTheCustomConnector()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buf = new byte[4096];
            await stream.ReadAsync(buf);
            var body = "hello";
            var resp = $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(resp));
        });
        var text = await Http.Api.GetStringAsync($"http://localhost:{port}/");
        Assert.Equal("hello", text);
        await server;
    }
}

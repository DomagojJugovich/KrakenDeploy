using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using KrakenDeploy.Server.Data.Net;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Tests the pinning connect callback in <see cref="SsrfHttpHandlerFactory"/>.
/// Uses only loopback sockets (no external network, no Docker) — one raw TCP
/// server that issues a redirect toward the cloud-metadata address to prove the
/// redirect hop is re-validated and refused at connect time.
/// </summary>
public sealed class SsrfHttpHandlerFactoryTests
{
    [Fact]
    public async Task Blocked_address_throws_at_connect_under_default_policy()
    {
        // Default policy denies loopback; the connect callback refuses before any
        // socket is opened, so no server needs to be listening.
        using var client = new HttpClient(
            SsrfHttpHandlerFactory.Create(new SsrfPolicy(), allowAutoRedirect: false));

        var act = async () => await client.GetAsync("http://127.0.0.1:1/");

        var ex = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        FullChain(ex).Should().Contain("SSRF guard");
    }

    [Fact]
    public async Task Redirect_to_metadata_is_refused_without_following()
    {
        // Acceptance: a webhook/catalog target that 302s to 169.254.169.254 must
        // fail at the redirect hop without contacting the metadata endpoint.
        using var server = new OneShotRedirectServer("http://169.254.169.254/latest/meta-data/");

        // Allow the first (loopback) hop so we actually reach the redirect; the
        // metadata hop is hard-blocked regardless of policy.
        using var client = new HttpClient(
            SsrfHttpHandlerFactory.Create(
                new SsrfPolicy { AllowLoopback = true }, allowAutoRedirect: true))
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        var act = async () => await client.GetAsync(server.BaseUrl);

        var ex = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        FullChain(ex).Should().Contain("SSRF guard",
            "the redirect target 169.254.169.254 is hard-blocked at connect");
        server.RequestCount.Should().Be(1, "only the first (loopback) hop is contacted");
    }

    [Fact]
    public async Task First_hop_to_allowed_loopback_succeeds()
    {
        // Sanity: with loopback allowed, a normal 200 from the loopback server
        // comes back — proving the callback connects (not just blocks).
        using var server = new OneShotRedirectServer(location: null); // returns 200
        using var client = new HttpClient(
            SsrfHttpHandlerFactory.Create(
                new SsrfPolicy { AllowLoopback = true }, allowAutoRedirect: false))
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        var response = await client.GetAsync(server.BaseUrl);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static string FullChain(Exception ex)
    {
        var sb = new StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            sb.Append(e.Message).Append(" | ");
        }
        return sb.ToString();
    }

    /// <summary>Minimal raw-socket HTTP/1.1 responder on 127.0.0.1. Returns a 302
    /// to <c>location</c> (or 200 when null) for each connection. Raw sockets
    /// avoid the HttpListener URL-ACL requirement on Windows.</summary>
    private sealed class OneShotRedirectServer : IDisposable
    {
        private readonly TcpListener _listener;
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);
        public string BaseUrl { get; }

        public OneShotRedirectServer(string? location)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}/";
            _ = AcceptLoopAsync(location);
        }

        private async Task AcceptLoopAsync(string? location)
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    Interlocked.Increment(ref _requestCount);
                    using var stream = client.GetStream();

                    // Drain the request head so the client considers it sent.
                    var buf = new byte[4096];
                    var head = new StringBuilder();
                    while (!head.ToString().Contains("\r\n\r\n"))
                    {
                        var n = await stream.ReadAsync(buf).ConfigureAwait(false);
                        if (n == 0) { break; }
                        head.Append(Encoding.ASCII.GetString(buf, 0, n));
                    }

                    var response = location is null
                        ? "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                        : $"HTTP/1.1 302 Found\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    var bytes = Encoding.ASCII.GetBytes(response);
                    await stream.WriteAsync(bytes).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                // Listener stopped (Dispose) — end the loop.
            }
        }

        public void Dispose() => _listener.Stop();
    }
}

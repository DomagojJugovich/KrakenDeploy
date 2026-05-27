using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KrakenDeploy.Mcp.Cli;

/// <summary>
/// <c>kraken-mcp</c> — a stdio↔HTTP proxy for the KrakenDeploy MCP server.
/// <para>
/// MCP clients that speak stdio (Claude Desktop, Cursor, Copilot Chat) run
/// this binary as a subprocess. It opens an MCP session to the remote
/// Kraken server's <c>/mcp</c> Streamable-HTTP endpoint (injecting the API
/// key) and pumps raw JSON-RPC messages between the two transports. The
/// pump is protocol-version-agnostic: it forwards every message verbatim,
/// so tools / resources / prompts / notifications all pass through without
/// this proxy needing to understand them.
/// </para>
/// <para>
/// <strong>stdout is the protocol channel</strong> — only JSON-RPC goes
/// there. All diagnostics go to stderr so they never corrupt the stream.
/// </para>
/// <para>
/// Usage: <c>kraken-mcp --server https://kraken.example --key &lt;api-key&gt;</c>
/// (or set <c>KRAKEN_API_KEY</c> instead of <c>--key</c>).
/// </para>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            await Console.Error.WriteLineAsync(Options.Usage).ConfigureAwait(false);
            return 2;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            await RunProxyAsync(options, shutdown.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            // Clean shutdown (Ctrl+C or a transport closing) — not an error.
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"kraken-mcp: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task RunProxyAsync(Options options, CancellationToken ct)
    {
        var endpoint = new Uri(options.ServerBaseUrl.TrimEnd('/') + "/mcp");
        await Console.Error.WriteLineAsync(
            $"kraken-mcp: bridging stdio ↔ {endpoint}").ConfigureAwait(false);

        // Remote side: HTTP client transport to Kraken's /mcp, API key in
        // the header the server's ApiKey auth scheme expects.
        var httpTransport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint          = endpoint,
            Name              = "kraken-mcp-remote",
            AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = options.ApiKey },
        });

        await using var remote = await httpTransport.ConnectAsync(ct).ConfigureAwait(false);

        // Local side: stdio server transport — this is what the MCP client
        // (the parent process) talks to over our stdin/stdout.
        await using var local = new StdioServerTransport("kraken-mcp");

        // Two-way verbatim pump. Whichever side ends first cancels the other.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var localToRemote = PumpAsync(local, remote, linked.Token);
        var remoteToLocal = PumpAsync(remote, local, linked.Token);

        await Task.WhenAny(localToRemote, remoteToLocal).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        // Let both pumps unwind; swallow the cancellation they throw.
        try
        {
            await Task.WhenAll(localToRemote, remoteToLocal).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* expected on the slower pump */ }
    }

    /// <summary>
    /// Forwards every JSON-RPC message from <paramref name="from"/> to
    /// <paramref name="to"/> until the source channel completes or the
    /// token trips.
    /// </summary>
    private static async Task PumpAsync(ITransport from, ITransport to, CancellationToken ct)
    {
        await foreach (var message in from.MessageReader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            await to.SendMessageAsync(message, ct).ConfigureAwait(false);
        }
    }

    private sealed record Options(string ServerBaseUrl, string ApiKey)
    {
        public const string Usage =
            "Usage: kraken-mcp --server <https://kraken-host> [--key <api-key>]\n" +
            "  --server   Base URL of the KrakenDeploy server (required).\n" +
            "  --key      API key. If omitted, read from KRAKEN_API_KEY env var.";

        public static Options Parse(string[] args)
        {
            string? server = null;
            string? key = null;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--server" when i + 1 < args.Length:
                        server = args[++i];
                        break;
                    case "--key" when i + 1 < args.Length:
                        key = args[++i];
                        break;
                    case "-h" or "--help":
                        throw new ArgumentException(Usage);
                    default:
                        throw new ArgumentException($"Unknown or incomplete argument: '{args[i]}'.");
                }
            }

            if (string.IsNullOrWhiteSpace(server))
            {
                throw new ArgumentException("--server is required.");
            }
            if (!Uri.TryCreate(server, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("--server must be an absolute http(s) URL.");
            }

            // Prefer the env var when --key is omitted — keeps the key out of
            // the client's launch-config args + the process table when the
            // operator sets it via the environment instead.
            key ??= Environment.GetEnvironmentVariable("KRAKEN_API_KEY");
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException(
                    "API key required: pass --key <key> or set KRAKEN_API_KEY.");
            }

            return new Options(server, key);
        }
    }
}

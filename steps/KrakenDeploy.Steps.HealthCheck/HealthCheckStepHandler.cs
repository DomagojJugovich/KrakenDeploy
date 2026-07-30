using System.Net.Sockets;
using KrakenDeploy.Contracts.Steps;

namespace KrakenDeploy.Steps.HealthCheck;

public static class HealthCheckConfigKeys
{
    private const string Prefix = "Octopus.Action.HealthCheck.";

    public const string Uri = Prefix + "Uri";
    public const string Protocol = Prefix + "Protocol";
    public const string Host = Prefix + "Host";
    public const string Port = Prefix + "Port";
    public const string ExpectedStatusCode = Prefix + "ExpectedStatusCode";
    public const string ExpectedBodyContains = Prefix + "ExpectedBodyContains";
    public const string TimeoutSeconds = Prefix + "TimeoutSeconds";
    public const string RetryAttempts = Prefix + "RetryAttempts";
    public const string RetryDelaySeconds = Prefix + "RetryDelaySeconds";
    public const string FailureAction = Prefix + "FailureAction";
}

public sealed class HealthCheckStepHandler : IStepHandler
{
    private static readonly HttpClient _http = CreateClient();

    public bool CanHandle(string stepType)
        => stepType.Equals("Octopus.HealthCheck", StringComparison.OrdinalIgnoreCase);

    public bool RequiresPackage => false;

    public async Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct)
    {
        var cfg = HealthCheckConfig.Parse(context.Step.Config);
        if (cfg is null)
        {
            await context.LogAsync("error",
                "Health check requires either Octopus.Action.HealthCheck.Uri or " +
                "Octopus.Action.HealthCheck.Host to be set.").ConfigureAwait(false);
            return false;
        }

        var isHttp = cfg.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || cfg.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        await context.LogAsync("info",
            $"Health check — {cfg.Uri} (protocol {(isHttp ? "HTTP" : "TCP")}, " +
            $"{cfg.RetryAttempts} attempt(s), {cfg.TimeoutSeconds}s timeout).")
            .ConfigureAwait(false);

        for (var attempt = 1; attempt <= cfg.RetryAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var (ok, detail) = isHttp
                ? await ProbeHttpAsync(cfg, ct).ConfigureAwait(false)
                : await ProbeTcpAsync(cfg, ct).ConfigureAwait(false);

            if (ok)
            {
                await context.LogAsync("info",
                    $"Health check succeeded on attempt {attempt}: {detail}")
                    .ConfigureAwait(false);
                return true;
            }

            await context.LogAsync("warning",
                $"Health check attempt {attempt}/{cfg.RetryAttempts} failed: {detail}")
                .ConfigureAwait(false);

            if (attempt < cfg.RetryAttempts && cfg.RetryDelaySeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(cfg.RetryDelaySeconds), ct)
                    .ConfigureAwait(false);
            }
        }

        var message = $"Health check failed after {cfg.RetryAttempts} attempt(s): {cfg.Uri}";
        if (cfg.FailureAction == "warn")
        {
            await context.LogAsync("warning", message + " (FailureAction=warn — continuing).")
                .ConfigureAwait(false);
            return true;
        }

        await context.LogAsync("error", message).ConfigureAwait(false);
        return false;
    }

    private static async Task<(bool Ok, string Detail)> ProbeHttpAsync(
        HealthCheckConfig cfg, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));

            using var resp = await _http.GetAsync(cfg.Uri, cts.Token).ConfigureAwait(false);
            var code = (int)resp.StatusCode;

            if (code != cfg.ExpectedStatusCode)
            {
                return (false, $"expected status {cfg.ExpectedStatusCode}, got {code}");
            }

            if (!string.IsNullOrEmpty(cfg.ExpectedBodyContains))
            {
                var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                if (body.IndexOf(cfg.ExpectedBodyContains, StringComparison.Ordinal) < 0)
                {
                    return (false,
                        $"status {code} OK but body did not contain '{cfg.ExpectedBodyContains}'");
                }
            }

            return (true, $"status {code}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, $"timed out after {cfg.TimeoutSeconds}s");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static async Task<(bool Ok, string Detail)> ProbeTcpAsync(
        HealthCheckConfig cfg, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));

            var host = cfg.Uri;
            var port = cfg.Port;
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return (true, $"TCP connect to {host}:{port} succeeded");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, $"timed out after {cfg.TimeoutSeconds}s");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }
}

internal sealed class HealthCheckConfig
{
    public required string Uri { get; init; }
    public required int Port { get; init; }
    public required int ExpectedStatusCode { get; init; }
    public string? ExpectedBodyContains { get; init; }
    public required int TimeoutSeconds { get; init; }
    public required int RetryAttempts { get; init; }
    public required int RetryDelaySeconds { get; init; }
    public required string FailureAction { get; init; }

    public static HealthCheckConfig? Parse(IReadOnlyDictionary<string, string> config)
    {
        var uri = Get(config, HealthCheckConfigKeys.Uri);
        if (string.IsNullOrWhiteSpace(uri))
        {
            var host = Get(config, HealthCheckConfigKeys.Host);
            if (string.IsNullOrWhiteSpace(host))
            {
                return null;
            }
            var scheme = string.Equals(Get(config, HealthCheckConfigKeys.Protocol), "tcp",
                StringComparison.OrdinalIgnoreCase) ? "tcp" : "http";
            var port = GetInt(config, HealthCheckConfigKeys.Port, scheme == "tcp" ? 0 : 80);
            uri = scheme == "tcp"
                ? host
                : $"{scheme}://{host}{(port is 80 or 443 ? "" : $":{port}")}";
        }

        return new HealthCheckConfig
        {
            Uri = uri!,
            Port = GetInt(config, HealthCheckConfigKeys.Port, 0),
            ExpectedStatusCode = GetInt(config, HealthCheckConfigKeys.ExpectedStatusCode, 200),
            ExpectedBodyContains = Get(config, HealthCheckConfigKeys.ExpectedBodyContains),
            TimeoutSeconds = Math.Max(1, GetInt(config, HealthCheckConfigKeys.TimeoutSeconds, 30)),
            RetryAttempts = Math.Max(1, GetInt(config, HealthCheckConfigKeys.RetryAttempts, 3)),
            RetryDelaySeconds = Math.Max(0, GetInt(config, HealthCheckConfigKeys.RetryDelaySeconds, 5)),
            FailureAction = string.Equals(Get(config, HealthCheckConfigKeys.FailureAction), "warn",
                StringComparison.OrdinalIgnoreCase) ? "warn" : "fail",
        };
    }

    private static string? Get(IReadOnlyDictionary<string, string> config, string key)
        => config.GetValueOrDefault(key);

    private static int GetInt(IReadOnlyDictionary<string, string> config, string key, int fallback)
        => int.TryParse(Get(config, key), out var v) ? v : fallback;
}

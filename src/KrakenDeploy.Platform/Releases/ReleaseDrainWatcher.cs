using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Platform.Releases;

/// <summary>
/// Recurring platform job (<c>kraken.release-drain-watch</c>, blue-green
/// topologies only): polls every Draining release's slot instances for their
/// live-circuit and in-flight-deployment counts (<c>/slot-metrics</c>) and retires
/// the release once <see cref="ReleaseDrainDecision"/> says it is empty (§5/§9 of
/// the design).
/// <para>
/// Node inventory comes from configuration —
/// <c>Releases:DrainWatch:SlotUrls:&lt;slotNo&gt;</c> is the list of that slot's
/// instance base URLs (one per app node). With no inventory configured the watcher
/// no-ops (manual <c>releases retire</c> still works). <b>Fail-safe:</b> an
/// unreachable probe, or a slot reporting a different release id (already
/// redeployed?), blocks retirement for this round rather than guessing.
/// </para>
/// </summary>
public sealed class ReleaseDrainWatcher(
    ReleaseRegistry registry,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<ReleaseDrainWatcher> logger)
{
    public const string HttpClientName = "kraken.release-drain-watch";

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var snapshot = await registry.GetSnapshotAsync(ct).ConfigureAwait(false);
        var draining = snapshot.Releases.Where(r => r.Status == AppReleaseStatus.Draining).ToList();
        if (draining.Count == 0)
        {
            return;
        }

        foreach (var release in draining)
        {
            var slotUrls = configuration
                .GetSection($"Releases:DrainWatch:SlotUrls:{release.SlotNo}")
                .Get<string[]>() ?? [];
            if (slotUrls.Length == 0)
            {
                logger.LogDebug(
                    "No slot inventory configured for slot {Slot}; release {ReleaseId} " +
                    "stays Draining until retired manually (releases retire).",
                    release.SlotNo, release.Id);
                continue;
            }

            var totals = await ProbeSlotsAsync(release.Id, slotUrls, ct).ConfigureAwait(false);
            if (totals is null)
            {
                // A probe failed or disagreed on the release id — fail safe, try next round.
                continue;
            }

            var (circuits, inFlight) = totals.Value;
            if (!ReleaseDrainDecision.ShouldRetire(
                    timeProvider.GetUtcNow(), release.DrainDeadlineUtc, circuits, inFlight))
            {
                logger.LogDebug(
                    "Release {ReleaseId} still draining: {Circuits} circuit(s), {InFlight} in-flight.",
                    release.Id, circuits, inFlight);
                continue;
            }

            await registry.RetireAsync(release.Id, ct).ConfigureAwait(false);
            logger.LogInformation(
                "Auto-retired release {ReleaseId} (slot {Slot}): {Circuits} circuit(s), " +
                "{InFlight} in-flight, deadline {Deadline:u}.",
                release.Id, release.SlotNo, circuits, inFlight, release.DrainDeadlineUtc);
            await InvalidateRoutersAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task<(int Circuits, int InFlight)?> ProbeSlotsAsync(
        string releaseId, string[] slotUrls, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var circuits = 0;
        var inFlight = 0;

        foreach (var baseUrl in slotUrls)
        {
            try
            {
                var metrics = await client
                    .GetFromJsonAsync<SlotMetrics>(new Uri(new Uri(baseUrl), "/slot-metrics"), ct)
                    .ConfigureAwait(false);
                if (metrics is null)
                {
                    logger.LogWarning("Slot probe {Url} returned an empty body; deferring retire.", baseUrl);
                    return null;
                }

                if (!string.Equals(metrics.Release, releaseId, StringComparison.Ordinal))
                {
                    // The instance at this URL runs a different release — the slot was
                    // redeployed under us, or inventory is misconfigured. Never retire
                    // on numbers that belong to another release.
                    logger.LogWarning(
                        "Slot probe {Url} reports release {Actual}, expected {Expected}; deferring retire.",
                        baseUrl, metrics.Release ?? "<none>", releaseId);
                    return null;
                }

                circuits += metrics.ActiveCircuits;
                inFlight += metrics.InFlightDeployments;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
            {
                logger.LogWarning(
                    "Slot probe {Url} failed ({Error}); deferring retire of {ReleaseId}.",
                    baseUrl, ex.Message, releaseId);
                return null;
            }
        }

        return (circuits, inFlight);
    }

    private async Task InvalidateRoutersAsync(CancellationToken ct)
    {
        var urls = configuration.GetSection("Releases:RouterInvalidateUrls").Get<string[]>() ?? [];
        if (urls.Length == 0)
        {
            return;
        }

        var client = httpClientFactory.CreateClient(HttpClientName);
        var opsToken = configuration["Releases:RouterOpsToken"];
        foreach (var url in urls)
        {
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post, new Uri(new Uri(url), "/kd-router/invalidate"));
                if (!string.IsNullOrWhiteSpace(opsToken))
                {
                    request.Headers.Add("X-KD-Ops-Token", opsToken);
                }

                using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
            {
                logger.LogWarning(
                    "Router invalidate {Url} failed ({Error}); it converges via its cache TTL.",
                    url, ex.Message);
            }
        }
    }

    private sealed record SlotMetrics(string? Release, int ActiveCircuits, int InFlightDeployments);
}

using KrakenDeploy.Server.Core.Domain.Settings;
using KrakenDeploy.Server.Data.Services;

namespace KrakenDeploy.Server.Data.Net;

/// <summary>Converts persisted or effective settings into runtime SSRF policies.</summary>
public static class SsrfSettingsConversions
{
    public static SsrfOptions ToSsrfOptions(this EffectiveSsrfSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new SsrfOptions
        {
            Webhook = settings.Webhook.ToSsrfPolicy(),
            StepCatalog = settings.StepCatalog.ToSsrfPolicy(),
            Oidc = settings.Oidc.ToSsrfPolicy(),
            Ai = settings.Ai.ToSsrfPolicy(),
        };
    }

    public static SsrfPolicy ToSsrfPolicy(this EffectiveSsrfPolicy settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new SsrfPolicy
        {
            AllowLoopback = settings.AllowLoopback.Value,
            AllowPrivate = settings.AllowPrivate.Value,
            AllowedHosts = [.. settings.AllowedHosts.Value],
        };
    }

    public static SsrfPolicy ToSsrfPolicy(this SsrfPolicySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new SsrfPolicy
        {
            AllowLoopback = settings.AllowLoopback,
            AllowPrivate = settings.AllowPrivate,
            AllowedHosts = [.. settings.AllowedHosts],
        };
    }
}

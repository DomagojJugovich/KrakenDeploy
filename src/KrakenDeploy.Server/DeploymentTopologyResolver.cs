using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Platform;

namespace KrakenDeploy.Server;

/// <summary>
/// Single place the web host and the CLI resolve <c>Deployment:Topology</c>
/// (BG1/T2). Also the enforcement point for the config-key migration: any config
/// still carrying <c>MultiAccount:Enabled</c> fails with a named message instead
/// of silently running the wrong topology (the old key's default-off would turn
/// a SaaS install into a single-tenant one).
/// </summary>
public static class DeploymentTopologyResolver
{
    public static DeploymentTopology Resolve(IConfiguration configuration)
    {
        var staleKey =
            $"{MultiAccountOptions.SectionName}:{MultiAccountOptions.RemovedEnabledKeyName}";
        if (configuration.GetSection(staleKey).Exists())
        {
            throw new InvalidOperationException(
                $"Configuration key '{staleKey}' was replaced by '{DeploymentOptions.TopologyKey}' (BG1/T2). " +
                "Remove 'MultiAccount:Enabled' (or the MultiAccount__Enabled environment variable) and set " +
                $"'{DeploymentOptions.TopologyKey}' to one of: {ValidValues}. " +
                "Former 'MultiAccount:Enabled=true' installs are Topology=Saas; " +
                "former single-instance installs are Topology=OnPrem (the default).");
        }

        var raw = configuration[DeploymentOptions.TopologyKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DeploymentTopology.OnPrem;
        }

        if (!TryParseName(raw, out var topology))
        {
            throw new InvalidOperationException(
                $"'{DeploymentOptions.TopologyKey}' has the unrecognised value '{raw}'. " +
                $"Valid values: {ValidValues}.");
        }

        return topology;
    }

    /// <summary>
    /// The ONE topology parser: matches the trimmed value case-insensitively
    /// against the enum NAMES only. Deliberately not <c>Enum.TryParse</c> —
    /// that also accepts numeric strings, including SIGNED ones ("+1", "-0")
    /// that a leading-digit check misses, and three call sites (config, the
    /// <c>--topology</c> flag, the setup prompt) had grown three divergent rule
    /// sets. Names-only keeps a config diff readable and refuses everything else.
    /// </summary>
    public static bool TryParseName(string? raw, out DeploymentTopology topology)
    {
        var trimmed = raw?.Trim();
        foreach (var name in Enum.GetNames<DeploymentTopology>())
        {
            if (string.Equals(trimmed, name, StringComparison.OrdinalIgnoreCase))
            {
                topology = Enum.Parse<DeploymentTopology>(name);
                return true;
            }
        }

        topology = default;
        return false;
    }

    private static string ValidValues =>
        string.Join(" | ", Enum.GetNames<DeploymentTopology>());
}

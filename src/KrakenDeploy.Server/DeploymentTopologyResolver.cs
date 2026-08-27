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

        if (!Enum.TryParse<DeploymentTopology>(raw, ignoreCase: true, out var topology)
            || !Enum.IsDefined(topology)
            || char.IsAsciiDigit(raw.TrimStart()[0]))
        {
            throw new InvalidOperationException(
                $"'{DeploymentOptions.TopologyKey}' has the unrecognised value '{raw}'. " +
                $"Valid values: {ValidValues}.");
        }

        return topology;
    }

    private static string ValidValues =>
        string.Join(" | ", Enum.GetNames<DeploymentTopology>());
}

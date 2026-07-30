namespace KrakenDeploy.Contracts.Steps;

/// <summary>
/// The <c>Octopus.DeployRelease</c> step type and the config keys shared ACROSS layers.
/// <para>
/// WP3-b — hoisted out of <c>OctopusDeployReleaseConfigKeys</c> (which lives in
/// <c>KrakenDeploy.Server.Transport</c> and stays the full contract, aliasing these) so
/// <c>KrakenDeploy.Server.Data</c> can reason about a DeployRelease step without a
/// reference in the forbidden direction: Transport references Data, not the reverse. The
/// need arose from process validation, which warns when a DeployRelease step's CHILD
/// project contains a manual-intervention gate — the parent then waits for a human, and
/// nothing else on the parent's process hints at that.
/// </para>
/// <para>
/// Only the keys more than one layer reads live here. Runner-only keys (the deployment
/// condition, for instance) stay in Transport.
/// </para>
/// </summary>
public static class DeployReleaseConfigKeys
{
    /// <summary>The step type these keys configure.</summary>
    public const string StepType = "Octopus.DeployRelease";

    /// <summary>
    /// Required. Identifier of the child project to deploy. The runner accepts a Kraken
    /// project GUID, slug, or name (case-insensitive); an imported Octopus export carries
    /// the Octopus-style <c>"Projects-NN"</c> id, which must be remapped. Readers that
    /// only need "which project is this" — process validation, for example — should parse
    /// it as a GUID and skip anything else rather than duplicating the runner's
    /// resolution.
    /// </summary>
    public const string ProjectId = "Octopus.Action.DeployRelease.ProjectId";
}

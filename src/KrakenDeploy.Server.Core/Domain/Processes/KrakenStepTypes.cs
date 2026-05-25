namespace KrakenDeploy.Server.Core.Domain.Processes;

/// <summary>
/// M15 — well-known step-type identifiers + the catalogue of "leaf-only"
/// Config keys (script body, package selectors, etc.) that a
/// <see cref="StepGroup"/>-typed step is NOT allowed to carry.
///
/// <para>
/// Centralised here so the validator (<c>ProcessService.ValidateAsync</c>)
/// and the importer (<c>OctopusDeploymentProcessImporter</c>) agree on the
/// definition — adding a new leaf-only key (e.g. when a new step package
/// lands) is a one-line change.
/// </para>
/// </summary>
public static class KrakenStepTypes
{
    /// <summary>
    /// M15 — the one marker step type that can have children. A
    /// <c>Kraken.StepGroup</c> step has no script body / no package;
    /// its Config carries step-level metadata only (Target Roles,
    /// ForEach properties, future rolling-deployment properties).
    /// Driven by properties on the step's Config, not its type:
    /// <list type="bullet">
    ///   <item><c>Octopus.Action.ForEach.Collection</c> set → loop body
    ///         over the named array variable.</item>
    ///   <item><c>Octopus.Action.MaxParallelism</c> set → reserved for
    ///         M-RollingDeployments; M15 reads + preserves but
    ///         doesn't act on it.</item>
    ///   <item>Neither set → plain container; children run in declared
    ///         order, with per-child <see cref="StepStartTrigger"/>
    ///         driving any parallel-with-previous behaviour.</item>
    /// </list>
    /// </summary>
    public const string StepGroup = "Kraken.StepGroup";

    /// <summary>
    /// Config keys that imply the step is a leaf (script body, package
    /// selector, etc.). A <see cref="StepGroup"/>-typed step MUST NOT
    /// carry any of these — validation refuses it. Mirrors the catalogue
    /// from the M15 plan body; extend additively when new step packages
    /// introduce leaf-only keys.
    /// </summary>
    public static readonly IReadOnlySet<string> LeafOnlyConfigKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Script step
            "Octopus.Action.Script.ScriptBody",
            "Octopus.Action.Script.Syntax",
            "Octopus.Action.Script.ScriptSource",
            "Octopus.Action.Script.ScriptFileName",
            "Octopus.Action.PowerShell.Edition",
            // Package-deploy step
            "Octopus.Action.Package.PackageId",
            "Octopus.Action.Package.FeedId",
            "Octopus.Action.Package.DownloadOnTentacle",
            "Octopus.Action.Package.PackageReferences",
            // IIS / Windows Service step packages
            "Octopus.Action.IISWebSite.WebSiteName",
            "Octopus.Action.WindowsService.ServiceName",
            // Substitute / JSON config step packages
            "Octopus.Action.SubstituteVariables.TargetFiles",
            "Octopus.Action.SubstituteVariables.Enabled",
            "Octopus.Action.Package.JsonConfigurationVariablesEnabled",
            "Octopus.Action.Package.JsonConfigurationVariablesTargets",
            // Manual intervention
            "Octopus.Action.Manual.Instructions",
            "Octopus.Action.Manual.ResponsibleTeamIds",
        };

    /// <summary>
    /// True if any of <paramref name="config"/>'s keys is in
    /// <see cref="LeafOnlyConfigKeys"/>. Used by the validator + importer
    /// to reject (or refuse to create) a <see cref="StepGroup"/> that
    /// carries leaf semantics.
    /// </summary>
    public static bool HasLeafOnlyConfigKey(IReadOnlyDictionary<string, string> config)
    {
        ArgumentNullException.ThrowIfNull(config);
        foreach (var key in config.Keys)
        {
            if (LeafOnlyConfigKeys.Contains(key))
            {
                return true;
            }
        }
        return false;
    }
}

namespace KrakenDeploy.Server.Data.Services.Ai.Curators;

// Built-in curators for the step types KrakenDeploy ships. Each declares
// the step type(s) it handles via [CuratesStepType] and emits the 3-5
// high-signal keys for that type, dropping the long Octopus namespace
// prefix for readability. Config-key string literals are the stable
// Octopus-compatible names (same ones the step packages + importer use);
// duplicated here rather than referenced so Server.Data stays free of a
// dependency on the individual step-package assemblies.

/// <summary>Script steps — emits syntax, edition, run-on-server flag, and a
/// truncated script body + its hash (so the AI can spot a changed script
/// without us shipping the whole body).</summary>
[CuratesStepType("Octopus.Script")]
[CuratesStepType("Kraken.Script")]
public sealed class ScriptStepConfigCurator : IStepConfigCurator
{
    private const string ScriptBody = "Octopus.Action.Script.ScriptBody";
    private const string Syntax = "Octopus.Action.Script.Syntax";
    private const string Edition = "Octopus.Action.PowerShell.Edition";
    private const string RunOnServer = "Octopus.Action.RunOnServer";
    private const int BodyPreviewChars = 200;

    public IReadOnlyDictionary<string, string> Curate(IReadOnlyDictionary<string, string> config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var summary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CuratorHelpers.CopyIfPresent(config, summary,
            (Syntax, "syntax"),
            (Edition, "powerShellEdition"),
            (RunOnServer, "runOnServer"));

        if (config.TryGetValue(ScriptBody, out var body) && !string.IsNullOrEmpty(body))
        {
            summary["scriptPreview"] = CuratorHelpers.Elide(body, BodyPreviewChars);
            summary["scriptSha256"] = CuratorHelpers.ShortHash(body);
        }
        return summary;
    }
}

/// <summary>Package-deploy steps — emits package id / version / feed.</summary>
[CuratesStepType("Octopus.TentaclePackage")]
public sealed class PackageStepConfigCurator : IStepConfigCurator
{
    private const string PackageId = "Octopus.Action.Package.PackageId";
    private const string FeedId = "Octopus.Action.Package.FeedId";
    private const string DownloadOnTentacle = "Octopus.Action.Package.DownloadOnTentacle";

    public IReadOnlyDictionary<string, string> Curate(IReadOnlyDictionary<string, string> config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var summary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CuratorHelpers.CopyIfPresent(config, summary,
            (PackageId, "packageId"),
            (FeedId, "feedId"),
            (DownloadOnTentacle, "downloadOnTentacle"));
        return summary;
    }
}

/// <summary>IIS steps — emits site name, app pool, web root / physical path.</summary>
[CuratesStepType("Kraken.IIS")]
[CuratesStepType("Octopus.IIS")]
public sealed class IisStepConfigCurator : IStepConfigCurator
{
    public IReadOnlyDictionary<string, string> Curate(IReadOnlyDictionary<string, string> config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var summary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Kraken.IIS uses Kraken.IIS.* keys; Octopus.IIS imports use
        // Octopus.Action.IISWebSite.*. Cover both so an imported step
        // curates as cleanly as a native one.
        CuratorHelpers.CopyIfPresent(config, summary,
            ("Kraken.IIS.SiteName", "siteName"),
            ("Octopus.Action.IISWebSite.WebSiteName", "siteName"),
            ("Kraken.IIS.AppPoolName", "appPoolName"),
            ("Octopus.Action.IISWebSite.ApplicationPoolName", "appPoolName"),
            ("Kraken.IIS.WebRoot", "webRoot"),
            ("Octopus.Action.IISWebSite.PhysicalPath", "physicalPath"),
            ("Kraken.IIS.AppPath", "appPath"));
        return summary;
    }
}

/// <summary>Windows-service steps — emits service name, executable, account, start mode.</summary>
[CuratesStepType("Octopus.WindowsService")]
[CuratesStepType("Kraken.WindowsService")]
public sealed class WindowsServiceStepConfigCurator : IStepConfigCurator
{
    private const string Prefix = "Octopus.Action.WindowsService.";

    public IReadOnlyDictionary<string, string> Curate(IReadOnlyDictionary<string, string> config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var summary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CuratorHelpers.CopyIfPresent(config, summary,
            (Prefix + "ServiceName", "serviceName"),
            (Prefix + "DisplayName", "displayName"),
            (Prefix + "ExecutablePath", "executablePath"),
            (Prefix + "ServiceAccount", "serviceAccount"),
            (Prefix + "StartMode", "startMode"));
        return summary;
    }
}

/// <summary>Manual-intervention steps — emits truncated instructions +
/// responsible team ids.</summary>
[CuratesStepType("Octopus.Manual")]
public sealed class ManualStepConfigCurator : IStepConfigCurator
{
    private const string Instructions = "Octopus.Action.Manual.Instructions";
    private const string ResponsibleTeamIds = "Octopus.Action.Manual.ResponsibleTeamIds";
    private const int InstructionsPreviewChars = 300;

    public IReadOnlyDictionary<string, string> Curate(IReadOnlyDictionary<string, string> config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var summary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (config.TryGetValue(Instructions, out var instr) && !string.IsNullOrWhiteSpace(instr))
        {
            summary["instructions"] = CuratorHelpers.Elide(instr, InstructionsPreviewChars);
        }
        CuratorHelpers.CopyIfPresent(config, summary,
            (ResponsibleTeamIds, "responsibleTeamIds"));
        return summary;
    }
}

/// <summary>Substitute-variables-in-files steps — emits target file globs +
/// enabled flag.</summary>
[CuratesStepType("Octopus.SubstituteVariables")]
public sealed class SubstituteVariablesStepConfigCurator : IStepConfigCurator
{
    public IReadOnlyDictionary<string, string> Curate(IReadOnlyDictionary<string, string> config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var summary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CuratorHelpers.CopyIfPresent(config, summary,
            ("Octopus.Action.SubstituteVariables.TargetFiles", "targetFiles"),
            ("Octopus.Action.SubstituteVariables.Enabled", "enabled"));
        return summary;
    }
}

/// <summary>Step Group container — emits the ForEach collection +
/// MaxParallelism (rolling window) if either is set, so the AI can see the
/// group's loop / fan-out shape at a glance.</summary>
[CuratesStepType("Kraken.StepGroup")]
public sealed class StepGroupConfigCurator : IStepConfigCurator
{
    public IReadOnlyDictionary<string, string> Curate(IReadOnlyDictionary<string, string> config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var summary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CuratorHelpers.CopyIfPresent(config, summary,
            ("Octopus.Action.ForEach.Collection", "forEachCollection"),
            ("Octopus.Action.ForEach.Parallel", "forEachParallel"),
            ("Octopus.Action.MaxParallelism", "maxParallelism"));
        return summary;
    }
}

namespace KrakenDeploy.Contracts.Steps;

/// <summary>
/// Step config keys for the <c>Kraken.Script</c> step type — a drop-in superset
/// of <c>Octopus.Script</c>. Keys use the Octopus-compatible names so processes
/// exported from Octopus Deploy import without renaming.
/// <para>
/// All values support Kraken variable expressions; substitution is applied
/// server-side before the config reaches the agent.
/// </para>
/// </summary>
public static class KrakenScriptConfigKeys
{
    /// <summary>Inline script source. Required.</summary>
    public const string ScriptBody = "Octopus.Action.Script.ScriptBody";

    /// <summary>
    /// Script language. One of: <c>PowerShell</c>, <c>Bash</c>, <c>CSharp</c>,
    /// <c>FSharp</c>, <c>Python</c>. Defaults to <c>PowerShell</c>.
    /// </summary>
    public const string Syntax = "Octopus.Action.Script.Syntax";

    /// <summary>
    /// Where the script comes from. Currently only <c>Inline</c> is honoured;
    /// <c>Package</c> is accepted for Octopus-import compatibility and treated
    /// as <c>Inline</c> using <see cref="ScriptBody"/>.
    /// </summary>
    public const string ScriptSource = "Octopus.Action.Script.ScriptSource";

    /// <summary>
    /// PowerShell edition when <see cref="Syntax"/> is <c>PowerShell</c>.
    /// <c>Desktop</c> = Windows PowerShell 5.x; <c>Core</c> = pwsh 7+.
    /// Defaults to <c>Desktop</c>.
    /// </summary>
    public const string PowerShellEdition = "Octopus.Action.PowerShell.Edition";

    /// <summary>
    /// When true, the script runs on the server rather than the agent. Accepted
    /// for Octopus-import compatibility; agent execution is the Kraken default.
    /// </summary>
    public const string RunOnServer = "Octopus.Action.RunOnServer";

    /// <summary>
    /// JSON-encoded array of <see cref="PackageReference"/> records — extra
    /// packages a step depends on. Resolved versions are pinned at release-
    /// creation time. Empty / missing means the step only uses its primary
    /// package (if any).
    /// </summary>
    public const string PackageReferences = "Octopus.Action.Package.PackageReferences";
}

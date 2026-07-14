namespace KrakenDeploy.Contracts;

/// <summary>
/// The full deployment plan sent from the server to the agent over SignalR.
/// The agent executes all steps autonomously and reports back via
/// <see cref="IAgentHubServer.AppendLogAsync"/> and
/// <see cref="IAgentHubServer.CompleteDeploymentAsync"/>.
/// </summary>
public sealed record DeploymentPlan(
    Guid DeploymentId,
    string EnvironmentName,
    DeploymentStepPlan[] Steps,
    /// <summary>
    /// Resolved, Octostache-substituted scalar variables (string and sensitive).
    /// StringArray variables appear here as comma-joined strings for Octostache
    /// and backward-compat <c>$OctopusParameters</c> access.
    /// </summary>
    IReadOnlyDictionary<string, string> Variables,
    /// <summary>
    /// StringArray variable values as parsed string arrays, for
    /// <c>$OctopusArrays</c> PowerShell exposure and <c>#{each}</c> Octostache iteration.
    /// </summary>
    IReadOnlyDictionary<string, string[]> ArrayVariables,
    /// <summary>
    /// T0-6: names of the variables (in <see cref="Variables"/>, and any per-step
    /// <see cref="DeploymentStepPlan.StepVariables"/> / <c>Config</c>) whose resolved
    /// values are <c>Sensitive</c>. The agent builds a value-based log redactor from
    /// these — looking each name up in <see cref="Variables"/> to get the plaintext,
    /// then masking those substrings to <c>***</c> before any log line leaves the
    /// agent. Sensitive output-variable values captured mid-run are folded into the
    /// same redactor. Appended + defaulted for back-compat: an older agent ignores
    /// it (prior no-masking behaviour); a newer agent reading an older plan masks
    /// nothing extra.
    /// </summary>
    IReadOnlyCollection<string>? SensitiveVariableNames = null);

/// <summary>
/// One step within a <see cref="DeploymentPlan"/>.
/// Config values have already had Octostache variable substitution applied server-side.
/// </summary>
public sealed record DeploymentStepPlan(
    int Index,
    string Name,
    string StepType,
    string PackageId,
    string PackageVersion,
    IReadOnlyDictionary<string, string> Config,
    IReadOnlyList<string>? TargetRoles = null,
    IReadOnlyList<KrakenDeploy.Contracts.Steps.PackageReference>? ReferencedPackages = null,
    /// <summary>
    /// Phase D-6: the pinned step-package <c>(Name, Version)</c> the agent
    /// hands to its <c>StepPackageLoader</c>. <c>null</c> means the snapshot
    /// didn't have an installed step-package claiming the step type — the
    /// agent uses its hardcoded handler instead. Pair must travel together:
    /// the loader's cache key is <c>(name, version)</c> so the version alone
    /// is meaningless without the name. Appended for back-compat with older
    /// clients.
    /// </summary>
    string? StepPackageName = null,
    string? StepPackageVersion = null,
    // ── M14 step-execution knobs ─────────────────────────────────────────
    // Defaulted at the end of the record so older agents (pre-M14)
    // deserializing a newer plan behave as
    // "Success Condition / Required=true / no retries / no timeout /
    // sequential" — same as pre-M14 behaviour. Lets server upgrade
    // ahead of agent during a rolling deploy without breaking.
    /// <summary>M14.2 Run Condition — int value of
    /// <c>KrakenDeploy.Execution.StepCondition</c>. The online server
    /// evaluates it before dispatching; the offline agent runner evaluates
    /// it itself (orchestrate mode) via the same shared
    /// <c>StepConditionEvaluator</c>, so it is plumbed through the contract
    /// for both consumers.</summary>
    int Condition = 0,
    string? ConditionVariableExpression = null,
    bool Required = true,
    int MaxRetries = 0,
    int RetryDelaySeconds = 0,
    /// <summary>M14.2 Per-step timeout in seconds. <c>0</c> = unlimited.
    /// Agent-side step runners cancel on this token after the configured
    /// duration; server-side runners do the same inline.</summary>
    int TimeoutSeconds = 0,
    /// <summary>M14.4 Start trigger — int value of
    /// <c>KrakenDeploy.Server.Core.Domain.Processes.StepStartTrigger</c>.
    /// The agent doesn't consume this; the server pre-flattens parallel
    /// waves before sending the plan (so the agent still sees a flat
    /// sequential list within each sub-plan).</summary>
    int StartTrigger = 0,
    /// <summary>M15 — internal accumulator key for output-variable
    /// reporting. For regular steps this equals <see cref="Name"/>; for
    /// ForEach iterations it is the stable synthetic key
    /// <c>OriginalName[index]</c> (e.g. <c>Deploy[0]</c>, <c>Deploy[1]</c>)
    /// so cross-iteration output references via Octostache
    /// <c>#{Octopus.Action[Deploy[0]].Output.X}</c> resolve correctly.
    /// <para>
    /// The agent uses <c>AccumulatorKey ?? Name</c> when reporting
    /// outputs back to the server via
    /// <c>IAgentHubServer.ReportStepCompletedAsync</c>, so older
    /// (pre-M15) agents that don't read this field still produce
    /// usable output-by-step-name rows. The display name (<see cref="Name"/>)
    /// stays the human-readable form for logs + Steps tab UI.
    /// </para></summary>
    string? AccumulatorKey = null,
    /// <summary>
    /// Per-step variable scope (step/action dimension). Only the variables
    /// whose resolved winner DIFFERS from the deployment-wide
    /// <see cref="DeploymentPlan.Variables"/> for this step's context — i.e. a
    /// delta the agent overlays onto the deployment-wide dict when executing
    /// this step. <c>null</c>/empty (the common case, no step-scoped variables)
    /// means the agent uses the deployment-wide variables unchanged. Appended
    /// for back-compat: older agents ignore it and behave as before.
    /// </summary>
    IReadOnlyDictionary<string, string>? StepVariables = null);

/// <summary>
/// Sent by the agent to the server when a deployment is triggered.
/// Contains all the data needed to download the package via gRPC.
/// </summary>
public sealed record PackageDownloadInfo(
    string PackageId,
    string Version);

using System.Globalization;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Runbooks;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Core.Domain.Tenants;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Builds the full set of Octopus-compatible system variables for a running
/// deployment or runbook run, so that scripts imported from Octopus that read
/// <c>$OctopusParameters["Octopus.Project.Name"]</c>, <c>#{Octopus.Release.Number}</c>,
/// etc. find the values they expect.
/// <para>
/// Variables that don't yet have a Kraken-equivalent (Azure-specific keys,
/// user-account fields not yet wired, etc.) are emitted as empty strings and
/// flagged with <c>// TODO(kraken-equivalent)</c> for follow-up. This keeps
/// the contract complete from the script's perspective — <c>$OctopusParameters[X]</c>
/// returns "" rather than $null — and lets us audit gaps by grepping this file.
/// </para>
/// <para>
/// Per-action variables that depend on which step is currently running
/// (un-indexed <c>Octopus.Action.Name</c>, <c>Octopus.Step.Number</c>, etc.)
/// are injected by the agent's <c>ScriptStepHandler</c> at execution time.
/// This builder emits the indexed forms — <c>Octopus.Action[StepName].Name</c> —
/// which any step can reference from any other step's script.
/// </para>
/// </summary>
public static class OctopusSystemVariablesBuilder
{
    /// <summary>
    /// Returns the full Octopus.* system-variable dictionary for a deployment.
    /// Caller merges this into the plan's <c>Variables</c>.
    /// </summary>
    public static Dictionary<string, string> BuildForDeployment(
        Deployment deployment,
        Release release,
        Project project,
        DeploymentEnvironment environment,
        DeploymentTarget? target,
        Tenant? tenant,
        IReadOnlyList<StepSnapshot> steps,
        string? serverBaseUrl = null,
        IReadOnlyList<string>? tenantTagCanonicals = null)
    {
        var v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddDeploymentScoped(v, deployment, release, environment, target);
        AddProjectScoped(v, project);
        AddReleaseScoped(v, release);
        AddEnvironmentScoped(v, environment);
        AddTenantScoped(v, tenant, tenantTagCanonicals);
        AddMachineScoped(v, target);
        AddStepsScoped(v, steps);
        AddWebScoped(v, deployment.Id, project, release, serverBaseUrl);
        AddTimeScoped(v, deployment.CreatedUtc);
        AddDeferredPlaceholders(v);
        return v;
    }

    /// <summary>
    /// Returns the Octopus.* system-variable dictionary for a runbook run.
    /// Runbook runs have no Release; release-scoped variables are emitted as
    /// empty strings.
    /// </summary>
    public static Dictionary<string, string> BuildForRunbookRun(
        RunbookRun run,
        Runbook runbook,
        Project project,
        DeploymentEnvironment environment,
        DeploymentTarget? target,
        Tenant? tenant,
        IReadOnlyList<StepSnapshot> steps,
        string? serverBaseUrl = null,
        IReadOnlyList<string>? tenantTagCanonicals = null)
    {
        var v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // ── Deployment-shaped keys (Octopus reuses Octopus.Deployment.* for runbooks) ──
        v["Octopus.Deployment.Id"] = run.Id.ToString();
        v["Octopus.Deployment.Name"] = $"Run {runbook.Name} in {environment.Name}";
        v["Octopus.Deployment.CreatedUtc"] = run.CreatedUtc.ToString("O", CultureInfo.InvariantCulture);
        v["Octopus.Deployment.QueueTime"] = run.CreatedUtc.ToString("O", CultureInfo.InvariantCulture);
        v["Octopus.Deployment.SortableQueueTime"] = run.CreatedUtc.ToString("o", CultureInfo.InvariantCulture);
        v["Octopus.Deployment.CreatedBy.DisplayName"]  = "";    // TODO(kraken-equivalent): wire up created-by user
        v["Octopus.Deployment.CreatedBy.Username"]     = "";    // TODO(kraken-equivalent)
        v["Octopus.Deployment.CreatedBy.EmailAddress"] = "";    // TODO(kraken-equivalent)
        v["Octopus.Deployment.ForcePackageDownload"]   = "False";
        v["Octopus.Deployment.PreviousSuccessful.Id"]  = "";    // TODO(kraken-equivalent)
        v["Octopus.Deployment.SpecificMachines"]       = target?.Id.ToString() ?? "";

        // ── Runbook-specific ───────────────────────────────────────────────
        v["Octopus.Runbook.Id"]   = runbook.Id.ToString();
        v["Octopus.Runbook.Name"] = runbook.Name;
        v["Octopus.RunbookRun.Id"]   = run.Id.ToString();
        v["Octopus.RunbookRun.Name"] = $"Run {runbook.Name} in {environment.Name}";

        // Release variables are emitted empty for runbook runs (no Release context).
        v["Octopus.Release.Id"]              = "";
        v["Octopus.Release.Number"]          = "";
        v["Octopus.Release.Notes"]           = "";
        v["Octopus.Release.Channel.Id"]      = "";
        v["Octopus.Release.Channel.Name"]    = "";
        v["Octopus.Release.PreviousVersion"] = "";
        v["Octopus.Release.CreatedUtc"]      = "";

        AddProjectScoped(v, project);
        AddEnvironmentScoped(v, environment);
        AddTenantScoped(v, tenant, tenantTagCanonicals);
        AddMachineScoped(v, target);

        AddStepsScoped(v, steps);

        AddWebScoped(v, run.Id, project, release: null, serverBaseUrl);
        AddTimeScoped(v, run.CreatedUtc);
        AddDeferredPlaceholders(v);
        return v;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Section helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static void AddDeploymentScoped(
        Dictionary<string, string> v,
        Deployment deployment,
        Release release,
        DeploymentEnvironment environment,
        DeploymentTarget? target)
    {
        v["Octopus.Deployment.Id"]                     = deployment.Id.ToString();
        v["Octopus.Deployment.Name"]                   = $"Deploy {release.Version} to {environment.Name}";
        v["Octopus.Deployment.Number"]                 = release.Version;
        v["Octopus.Deployment.CreatedUtc"]             = deployment.CreatedUtc.ToString("O", CultureInfo.InvariantCulture);
        v["Octopus.Deployment.QueueTime"]              = deployment.CreatedUtc.ToString("O", CultureInfo.InvariantCulture);
        v["Octopus.Deployment.SortableQueueTime"]      = deployment.CreatedUtc.ToString("o", CultureInfo.InvariantCulture);
        v["Octopus.Deployment.StartedUtc"]             = deployment.StartedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "";
        v["Octopus.Deployment.CreatedBy.DisplayName"]  = "";    // TODO(kraken-equivalent): wire up created-by user (need deployment.CreatedById on AuditableEntity)
        v["Octopus.Deployment.CreatedBy.Username"]     = "";    // TODO(kraken-equivalent)
        v["Octopus.Deployment.CreatedBy.EmailAddress"] = "";    // TODO(kraken-equivalent)
        v["Octopus.Deployment.ForcePackageDownload"]   = "False";
        v["Octopus.Deployment.PreviousSuccessful.Id"]  = "";    // TODO(kraken-equivalent): query previous successful deployment for (project, env, [tenant])
        v["Octopus.Deployment.PreviousSuccessful.ReleaseId"] = ""; // TODO(kraken-equivalent)
        v["Octopus.Deployment.SpecificMachines"]       = target?.Id.ToString() ?? "";
        v["Octopus.Deployment.Error"]                  = ""; // populated by the agent on failure
        v["Octopus.Deployment.ErrorDetail"]            = ""; // populated by the agent on failure
    }

    private static void AddProjectScoped(Dictionary<string, string> v, Project project)
    {
        v["Octopus.Project.Id"]          = project.Id.ToString();
        v["Octopus.Project.Name"]        = project.Name;
        v["Octopus.Project.Slug"]        = project.Slug;
        v["Octopus.Project.Description"] = project.Description ?? "";
    }

    private static void AddReleaseScoped(Dictionary<string, string> v, Release release)
    {
        v["Octopus.Release.Id"]              = release.Id.ToString();
        v["Octopus.Release.Number"]          = release.Version;
        v["Octopus.Release.Notes"]           = release.ReleaseNotes ?? "";
        v["Octopus.Release.Channel.Id"]      = release.ChannelId?.ToString() ?? "";
        v["Octopus.Release.Channel.Name"]    = "";    // TODO(kraken-equivalent): include channel navigation when loading release
        v["Octopus.Release.PreviousVersion"] = "";    // TODO(kraken-equivalent): previous release on same channel
        v["Octopus.Release.CreatedUtc"]      = release.CreatedUtc.ToString("O", CultureInfo.InvariantCulture);
    }

    private static void AddEnvironmentScoped(Dictionary<string, string> v, DeploymentEnvironment env)
    {
        v["Octopus.Environment.Id"]          = env.Id.ToString();
        v["Octopus.Environment.Name"]        = env.Name;
        v["Octopus.Environment.Slug"]        = env.Slug;
        v["Octopus.Environment.SortOrder"]   = env.SortOrder.ToString(CultureInfo.InvariantCulture);
        v["Octopus.Environment.Description"] = "";    // TODO(kraken-equivalent): add Description field on DeploymentEnvironment
    }

    private static void AddTenantScoped(
        Dictionary<string, string> v, Tenant? tenant,
        IReadOnlyList<string>? tenantTagCanonicals = null)
    {
        if (tenant is null)
        {
            v["Octopus.Deployment.Tenant.Id"]          = "";
            v["Octopus.Deployment.Tenant.Name"]        = "";
            v["Octopus.Deployment.Tenant.Description"] = "";
            v["Octopus.Deployment.Tenant.Tags"]        = "";
            return;
        }

        v["Octopus.Deployment.Tenant.Id"]          = tenant.Id.ToString();
        v["Octopus.Deployment.Tenant.Name"]        = tenant.Name;
        v["Octopus.Deployment.Tenant.Description"] = tenant.Description ?? "";
        // Canonical "TagSetName/TagName" strings of the tenant's applied tags
        // (extended tag sets) — comma-separated, matching Octopus's format.
        // The caller resolves them (TagService.GetTenantTagCanonicalsAsync);
        // tags live in the polymorphic tag_applications table, not on Tenant.
        v["Octopus.Deployment.Tenant.Tags"]        =
            tenantTagCanonicals is { Count: > 0 } ? string.Join(",", tenantTagCanonicals) : "";
    }

    private static void AddMachineScoped(Dictionary<string, string> v, DeploymentTarget? target)
    {
        if (target is null)
        {
            v["Octopus.Machine.Id"]              = "";
            v["Octopus.Machine.Name"]            = "";
            v["Octopus.Machine.Hostname"]        = "";
            v["Octopus.Machine.OperatingSystem"] = "";
            v["Octopus.Machine.Roles"]           = "";
            v["Octopus.Tentacle.Agent.Version"]  = "";
            v["Octopus.Tentacle.Agent.ApplicationDirectoryPath"] = "";
            return;
        }

        v["Octopus.Machine.Id"]              = target.Id.ToString();
        v["Octopus.Machine.Name"]            = target.Name;
        v["Octopus.Machine.Hostname"]        = target.MachineName ?? "";
        v["Octopus.Machine.OperatingSystem"] = target.OperatingSystem ?? "";
        v["Octopus.Machine.Roles"]           = string.Join(",", target.Roles);
        v["Octopus.Tentacle.Agent.Version"]  = target.AgentVersion ?? "";
        v["Octopus.Tentacle.Agent.ApplicationDirectoryPath"] = "";    // TODO(kraken-equivalent): agent's working directory once exposed
    }

    private static void AddStepsScoped(Dictionary<string, string> v, IReadOnlyList<StepSnapshot> steps)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            AddIndexedActionScoped(v, s.Name, i + 1, s.PackageId, s.PackageVersion, s.TargetRoles);
        }
    }

    /// <summary>
    /// Adds <c>Octopus.Action[StepName].*</c> and <c>Octopus.Step[StepName].*</c>
    /// keys that any other step's script may reference.
    /// </summary>
    private static void AddIndexedActionScoped(
        Dictionary<string, string> v,
        string stepName,
        int number,
        string packageId,
        string packageVersion,
        IReadOnlyList<string> targetRoles)
    {
        var idx = $"[{stepName}]";
        v[$"Octopus.Action{idx}.Name"]                   = stepName;
        v[$"Octopus.Action{idx}.Id"]                     = stepName;    // Kraken steps don't have a separate Action.Id
        v[$"Octopus.Action{idx}.Number"]                 = number.ToString(CultureInfo.InvariantCulture);
        v[$"Octopus.Action{idx}.Package.PackageId"]      = packageId;
        v[$"Octopus.Action{idx}.Package.PackageVersion"] = packageVersion;
        v[$"Octopus.Action{idx}.Package.OriginalInstalledPath"] = "";    // TODO(kraken-equivalent): set by agent after package extraction
        v[$"Octopus.Action{idx}.TargetRoles"]            = string.Join(",", targetRoles);
        v[$"Octopus.Action{idx}.PreviousStatus.Status"]  = "";    // TODO(kraken-equivalent): previous step status accessible mid-run
        v[$"Octopus.Step{idx}.Name"]                     = stepName;
        v[$"Octopus.Step{idx}.Number"]                   = number.ToString(CultureInfo.InvariantCulture);
        v[$"Octopus.Step{idx}.Status.Code"]              = "";    // TODO(kraken-equivalent)
    }

    private static void AddWebScoped(
        Dictionary<string, string> v,
        Guid deploymentOrRunId,
        Project project,
        Release? release,
        string? serverBaseUrl)
    {
        var baseUrl = string.IsNullOrWhiteSpace(serverBaseUrl) ? "" : serverBaseUrl.TrimEnd('/');
        v["Octopus.Web.ServerUri"]  = baseUrl;
        v["Octopus.Web.BaseUrl"]    = baseUrl;
        v["Octopus.Web.ProjectLink"]    = string.IsNullOrEmpty(baseUrl) ? "" : $"{baseUrl}/projects/{project.Slug}";
        v["Octopus.Web.ReleaseLink"]    = (string.IsNullOrEmpty(baseUrl) || release is null)
            ? "" : $"{baseUrl}/projects/{project.Slug}/releases/{release.Id}";
        v["Octopus.Web.DeploymentLink"] = string.IsNullOrEmpty(baseUrl) ? "" : $"{baseUrl}/deployments/{deploymentOrRunId}";
    }

    private static void AddTimeScoped(Dictionary<string, string> v, DateTimeOffset queuedUtc)
    {
        v["Octopus.Time.Year"]   = queuedUtc.Year.ToString("D4", CultureInfo.InvariantCulture);
        v["Octopus.Time.Month"]  = queuedUtc.Month.ToString("D2", CultureInfo.InvariantCulture);
        v["Octopus.Time.Day"]    = queuedUtc.Day.ToString("D2", CultureInfo.InvariantCulture);
        v["Octopus.Time.Hour"]   = queuedUtc.Hour.ToString("D2", CultureInfo.InvariantCulture);
        v["Octopus.Time.Minute"] = queuedUtc.Minute.ToString("D2", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Variables we deliberately leave empty for now. Grouping them here
    /// makes the unsupported surface easy to audit.
    /// </summary>
    private static void AddDeferredPlaceholders(Dictionary<string, string> v)
    {
        // TODO(kraken-equivalent): Azure.* keys land when the Azure step pack is implemented.
        v["Octopus.Action.Azure.AccountId"]              = "";
        v["Octopus.Action.Azure.SubscriptionId"]         = "";
        v["Octopus.Action.Azure.TenantId"]               = "";
        v["Octopus.Action.Azure.ClientId"]               = "";
        v["Octopus.Action.Azure.ResourceGroupName"]      = "";
        v["Octopus.Action.Azure.WebAppName"]             = "";

        // TODO(kraken-equivalent): AWS.* keys land when the AWS step pack is implemented.
        v["Octopus.Action.Aws.AccountId"]                = "";
        v["Octopus.Action.Aws.Region"]                   = "";

        // TODO(kraken-equivalent): Kubernetes / Helm / Docker keys land with those step packs.
        v["Octopus.Action.Kubernetes.ClusterUrl"]        = "";
        v["Octopus.Action.Kubernetes.Namespace"]         = "";

        // TODO(kraken-equivalent): build-info / package metadata fed from external CI
        v["Octopus.Release.BuildInformation"]            = "";

        // TODO(kraken-equivalent): retry context — populated when an attempt is a retry of a failed deployment
        v["Octopus.Deployment.RetryCount"]               = "0";
    }
}

namespace KrakenDeploy.Contracts.Offline;

/// <summary>
/// File/dir conventions for an offline-drop bundle, shared by the server-side
/// generator (<c>DropBundleService</c>) and the offline runner so both agree
/// on layout. The bundle carries a serialized <see cref="DeploymentPlan"/>
/// (encrypted), the packages + step-package archives the plan needs, and the
/// self-contained runner — so it executes through the same
/// <c>DeploymentExecutor</c> as an online deployment, on a machine with no
/// .NET runtime installed.
/// </summary>
public static class OfflineBundleLayout
{
    /// <summary>AES-GCM-encrypted serialized <see cref="DeploymentPlan"/>.</summary>
    public const string EncryptedPlanFile = "plan.enc";

    /// <summary>Aggregated <see cref="OfflineDropResult"/> written by the runner.</summary>
    public const string ResultFile = "deployment-result.json";

    /// <summary>HMAC-SHA256 of <see cref="ResultFile"/> (see <see cref="OfflineResultSigner"/>).</summary>
    public const string ResultSignatureFile = "result-signature.bin";

    /// <summary>Human-readable run log appended by the runner.</summary>
    public const string LogFile = "deployment-log.txt";

    /// <summary>Root of collected step artifacts (<c>artifacts/{step}/{file}</c>).</summary>
    public const string ArtifactsDir = "artifacts";

    /// <summary>Deployable packages (<c>packages/{packageId}/{version}/{file}</c>).</summary>
    public const string PackagesDir = "packages";

    /// <summary>Step-handler package archives (<c>step-packages/{name}/{version}/{file}</c>).</summary>
    public const string StepPackagesDir = "step-packages";

    /// <summary>Self-contained runner binaries.</summary>
    public const string RunnerDir = "runner";

    public static string PackageDir(string packageId, string version)
        => $"{PackagesDir}/{packageId}/{version}";

    public static string StepPackageDir(string name, string version)
        => $"{StepPackagesDir}/{name}/{version}";

    /// <summary>
    /// Canonical, <b>platform-independent</b> sanitizer for the per-step artifact
    /// directory segment (<c>artifacts/{step}/</c>). Replaces anything outside
    /// <c>[A-Za-z0-9._-]</c> with <c>_</c>. Deterministic across the target OS
    /// (where the runner writes artifacts) and the server OS (where the result is
    /// ingested) — unlike <c>Path.GetInvalidFileNameChars()</c>, which differs by
    /// platform — so the server can reverse-map a sanitized dir back to the real
    /// step name via the result's step list.
    /// </summary>
    public static string SanitizeStepName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "_";
        }
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        }
        return sb.ToString();
    }
}

/// <summary>
/// Result the offline runner writes after executing a bundle, uploaded back to
/// the server to reconcile the deployment. Carries per-step outcomes + captured
/// output variables so the server can populate the same step-outcome and
/// output-variable rows an online deployment produces.
/// </summary>
public sealed record OfflineDropResult
{
    public Guid DeploymentId { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    public List<OfflineStepResult> Steps { get; init; } = [];
}

/// <summary>One step's outcome inside an <see cref="OfflineDropResult"/>.</summary>
public sealed record OfflineStepResult
{
    public int StepIndex { get; init; }
    public string StepName { get; init; } = "";
    public bool Success { get; init; }
    /// <summary>The step's Run Condition skipped it; it did not execute.</summary>
    public bool Skipped { get; init; }
    /// <summary>The step's Required flag at execution time (from the plan), so
    /// the server records the real value rather than assuming required.</summary>
    public bool Required { get; init; } = true;
    public string? ErrorMessage { get; init; }
    public Dictionary<string, string> OutputVariables { get; init; } = [];
}

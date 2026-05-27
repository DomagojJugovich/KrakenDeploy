using System.ComponentModel;

namespace KrakenDeploy.Server.Data.Services.Ai.Assistant;

/// <summary>
/// M11.D.1 — the structured-output shape the LLM fills when suggesting a
/// starter deployment process for a package. Passed as <c>TResult</c> to
/// <c>IKrakenAi.CompleteAsync&lt;StepSuggestionResult&gt;</c>.
/// </summary>
public sealed class StepSuggestionResult
{
    [Description("A one- or two-sentence summary of the proposed process and why it fits the package.")]
    public string OverallRationale { get; set; } = string.Empty;

    [Description("The ordered steps to create. Empty if the package layout gives no clear signal.")]
    public List<SuggestedStep> Steps { get; set; } = [];
}

/// <summary>One suggested step.</summary>
public sealed class SuggestedStep
{
    [Description("A short human-readable step name, e.g. \"Deploy IIS site\".")]
    public string Name { get; set; } = string.Empty;

    [Description("The Kraken/Octopus step type, e.g. \"Kraken.IIS\", \"Octopus.WindowsService\", \"Octopus.Script\", \"Octopus.TentaclePackage\".")]
    public string StepType { get; set; } = string.Empty;

    [Description("Why this step is suggested for this package.")]
    public string Rationale { get; set; } = string.Empty;
}

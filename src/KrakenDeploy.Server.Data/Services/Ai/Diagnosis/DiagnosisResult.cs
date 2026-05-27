using System.ComponentModel;

namespace KrakenDeploy.Server.Data.Services.Ai.Diagnosis;

/// <summary>
/// M11.C — the structured-output shape the LLM fills for a failed-deployment
/// diagnosis. Passed as <c>TResult</c> to
/// <c>IKrakenAi.CompleteAsync&lt;DiagnosisResult&gt;</c>; the provider's
/// structured-output mode (Anthropic tool use / OpenAI json_schema)
/// constrains the model to this shape. The <see cref="Description"/>
/// attributes feed the generated JSON schema so the model knows what each
/// field means.
/// </summary>
public sealed class DiagnosisResult
{
    [Description("One or two plain-language sentences naming the most probable cause of the failure.")]
    public string ProbableCause { get; set; } = string.Empty;

    [Description("Confidence in the diagnosis: exactly one of \"Low\", \"Medium\", or \"High\".")]
    public string Confidence { get; set; } = "Low";

    [Description("One or two concrete next-step actions an operator should take. Empty if none are clear.")]
    public string SuggestedFix { get; set; } = string.Empty;

    [Description("The log lines most relevant to the diagnosis, each with its sequence number and text. May be empty.")]
    public List<DiagnosisLogLine> RelevantLogLines { get; set; } = [];
}

/// <summary>One log line the model flagged as relevant.</summary>
public sealed class DiagnosisLogLine
{
    [Description("The log line's sequence number as shown in the provided log.")]
    public int Sequence { get; set; }

    [Description("The text of the log line.")]
    public string Text { get; set; } = string.Empty;
}

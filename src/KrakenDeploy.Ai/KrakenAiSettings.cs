namespace KrakenDeploy.Ai;

/// <summary>
/// Per-Space AI configuration (M11.A.1). Bound from the Space's settings
/// row at runtime; the <see cref="KrakenAiClientFactory"/> consumes this to
/// build the right <c>IChatClient</c>. <strong>Never stored in source or
/// committed to the repo</strong> — every installation supplies its own.
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle: settings come from a Space's row in the database, loaded on
/// the request path. They're NOT bound from <c>appsettings.json</c> — that
/// would force a single instance-wide config and break the per-Space
/// budget / key / provider story (M11.A.3 and onward).
/// </para>
/// <para>
/// <strong>Sensitive value:</strong> <see cref="ApiKey"/> is the only field
/// that must never appear in logs / audit / serialised state outside the
/// settings row. The wrapper redacts it before any diagnostic dump.
/// </para>
/// </remarks>
public sealed record KrakenAiSettings
{
    /// <summary>The provider this Space is using.</summary>
    public KrakenAiProvider Provider { get; init; } = KrakenAiProvider.Disabled;

    /// <summary>
    /// Model identifier, provider-specific shape:
    /// <list type="bullet">
    ///   <item><description><b>Anthropic:</b> <c>claude-3-5-sonnet-20241022</c>, <c>claude-3-5-haiku-20241022</c>, etc.</description></item>
    ///   <item><description><b>OpenAI:</b> <c>gpt-4o</c>, <c>gpt-4o-mini</c>.</description></item>
    ///   <item><description><b>Azure OpenAI:</b> the deployment name in your Azure resource.</description></item>
    ///   <item><description><b>DeepSeek:</b> <c>deepseek-chat</c>, <c>deepseek-reasoner</c>.</description></item>
    ///   <item><description><b>Local:</b> whatever the local server's model alias is (<c>llama3.2:3b</c>, etc.).</description></item>
    /// </list>
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Provider API key. Stored encrypted in the Space's settings row;
    /// decrypted only when the wrapper builds the <c>IChatClient</c>.
    /// Empty/null = no provider configured → wrapper acts as if
    /// <see cref="Provider"/> were <see cref="KrakenAiProvider.Disabled"/>.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Base URL for OpenAI-compatible providers (Azure OpenAI deployments,
    /// DeepSeek, Ollama / LM Studio / vLLM, etc.). Ignored when
    /// <see cref="Provider"/> is <see cref="KrakenAiProvider.OpenAI"/> or
    /// <see cref="KrakenAiProvider.Anthropic"/> (they use the SDK's default).
    /// <para>
    /// Required when <see cref="Provider"/> is
    /// <see cref="KrakenAiProvider.AzureOpenAI"/>,
    /// <see cref="KrakenAiProvider.DeepSeek"/>, or
    /// <see cref="KrakenAiProvider.LocalOpenAiCompatible"/>.
    /// </para>
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// Monthly budget cap in USD (M11.A.5). The wrapper checks month-to-date
    /// cost (computed from <c>AiCallLog</c> token counts × per-1k-token
    /// rates) before every call. Exceeded → <see cref="KrakenAiBudgetExceededException"/>.
    /// Zero or negative = no cap (use cautiously).
    /// </summary>
    public decimal BudgetUsdPerMonth { get; init; }

    /// <summary>
    /// When <c>true</c>, the <c>AiCallLog</c> rows include the full
    /// prompt + response bodies for this Space's calls. Default <c>false</c>:
    /// the audit table is a GDPR target with bodies on, so opt-in only.
    /// Admins flipping this should also configure a retention window.
    /// </summary>
    public bool LogPromptBodies { get; init; }

    /// <summary>Per-feature kill switches.</summary>
    public KrakenAiFeatureFlags Features { get; init; } = new();
}

/// <summary>Feature gates for the four M11 sub-features (M11.A.6).</summary>
public sealed record KrakenAiFeatureFlags
{
    /// <summary>M11.C — autonomous failure diagnosis Hangfire job.</summary>
    public bool DiagnosisEnabled { get; init; }

    /// <summary>M11.B — MCP server endpoint inside this Space's auth scope.</summary>
    public bool McpEnabled { get; init; }

    /// <summary>M11.E — ad-hoc agent actions (natural-language → script).</summary>
    public bool AdhocEnabled { get; init; }

    /// <summary>M11.D — process-builder UI assistant (inline suggestions, field help).</summary>
    public bool AssistantEnabled { get; init; }
}

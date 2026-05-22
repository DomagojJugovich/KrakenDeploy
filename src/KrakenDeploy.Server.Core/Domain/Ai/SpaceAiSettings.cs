using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Ai;

/// <summary>
/// Per-Space AI configuration (Phase M11.A.6). One row per Space; the
/// settings page GET returns this row when present and a default-shaped
/// record (<see cref="Provider"/> = <see cref="KrakenAiProviderValue.Disabled"/>,
/// all feature flags off) when absent — Spaces that never use AI never
/// allocate a row.
/// </summary>
/// <remarks>
/// <para>
/// <strong>API key</strong> (<see cref="ApiKeyEncrypted"/>) is stored as
/// ciphertext produced by <c>IEncryptionService</c> (AES-256-GCM, same
/// primitive that protects <c>Sensitive</c> variable values). It MUST
/// be decrypted only inside the request path that needs it (LLM call,
/// the explicit reveal endpoint) — never serialised to JSON, never
/// logged, never crossed to the browser. The settings GET returns a
/// masked placeholder; the reveal endpoint hits a separate
/// permission-gated path that writes a <c>SpaceAi.ApiKeyRevealed</c>
/// audit event on every call.
/// </para>
/// <para>
/// <strong>Unique index</strong> on <see cref="ISpaceScoped.SpaceId"/>
/// enforces the 1-to-1 with Space at the DB level — multiple settings
/// rows for one Space would be a bug.
/// </para>
/// </remarks>
public class SpaceAiSettings : AuditableEntity, ISpaceScoped
{
    /// <summary>FK to the owning Space. Auto-stamped by SpaceScopingInterceptor.</summary>
    public Guid SpaceId { get; set; }

    /// <summary>
    /// <see cref="KrakenAiProviderValue"/> as a string for forward-compat
    /// (adding a new provider doesn't require a data backfill). The
    /// settings provider in <c>KrakenDeploy.Server.Data</c> parses this
    /// into the <c>KrakenAiProvider</c> enum at read time.
    /// </summary>
    public string Provider { get; set; } = KrakenAiProviderValue.Disabled;

    /// <summary>Model id at provider's documented casing (e.g. <c>claude-sonnet-4.6</c>).</summary>
    public string? Model { get; set; }

    /// <summary>
    /// AES-256-GCM ciphertext of the provider API key. <c>null</c> when
    /// the operator hasn't configured one yet — settings page treats null
    /// as "no key" and refuses to enable any feature flag. NEVER returned
    /// to the UI as ciphertext; the reveal endpoint decrypts on demand.
    /// </summary>
    public string? ApiKeyEncrypted { get; set; }

    /// <summary>
    /// Base URL override — required for <c>AzureOpenAI</c> + <c>LocalOpenAiCompatible</c>,
    /// ignored otherwise.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Monthly USD cap. Zero = no cap (used cautiously — surfaces a
    /// warning in the settings UI). Negative is rejected at the API
    /// boundary.
    /// </summary>
    public decimal BudgetUsdPerMonth { get; set; }

    /// <summary>
    /// When <c>true</c>, the <c>AiCallLog</c> rows for this Space include
    /// full prompt + response bodies. GDPR-relevant — defaults off, surfaced
    /// in the UI with a warning footer.
    /// </summary>
    public bool LogPromptBodies { get; set; }

    /// <summary>M11.C — autonomous failure diagnosis Hangfire job.</summary>
    public bool DiagnosisEnabled { get; set; }

    /// <summary>M11.B — MCP server endpoint exposes this Space's data.</summary>
    public bool McpEnabled { get; set; }

    /// <summary>M11.E — ad-hoc agent actions feature.</summary>
    public bool AdhocEnabled { get; set; }

    /// <summary>M11.D — process-builder UI assistant.</summary>
    public bool AssistantEnabled { get; set; }
}

/// <summary>
/// String constants matching the <c>KrakenAiProvider</c> enum names in
/// <c>KrakenDeploy.Ai</c>. Kept in Core (not referencing the Ai project)
/// so the Core domain stays free of the AI dependency chain.
/// </summary>
public static class KrakenAiProviderValue
{
    public const string Disabled              = nameof(Disabled);
    public const string Anthropic             = nameof(Anthropic);
    public const string OpenAI                = nameof(OpenAI);
    public const string AzureOpenAI           = nameof(AzureOpenAI);
    public const string DeepSeek              = nameof(DeepSeek);
    public const string LocalOpenAiCompatible = nameof(LocalOpenAiCompatible);
}

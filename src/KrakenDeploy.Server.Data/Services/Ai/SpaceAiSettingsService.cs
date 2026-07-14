using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Core.Domain.Variables;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services.Ai;

/// <summary>
/// CRUD service for the per-Space <see cref="SpaceAiSettings"/> row
/// (Phase M11.A.6.3). Used by the REST endpoints; the
/// <see cref="DbKrakenAiSettingsProvider"/> reads independently for
/// AI calls because it doesn't need the masking + audit overhead.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Wire surface</strong>: GET returns a DTO with the API key
/// masked; PUT preserves the existing key when the caller leaves the
/// <c>ApiKey</c> field blank (sentinel for "leave alone"). Reveal goes
/// through a separate call that writes a <c>SpaceAi.ApiKeyRevealed</c>
/// audit row on every invocation.
/// </para>
/// <para>
/// <strong>Lazy creation</strong>: PUT against a Space with no existing
/// row inserts one. GET against the same returns a default-shaped DTO
/// without allocating a row — Spaces that never use AI never pay
/// storage cost.
/// </para>
/// </remarks>
public sealed class SpaceAiSettingsService(
    SettingsService                        settings,
    IDbContextFactory<KrakenDbContext>   dbFactory,
    ISpaceContext                          spaceContext,
    IEncryptionService                     encryption,
    ILogger<SpaceAiSettingsService>        logger)
{
    /// <summary>
    /// Returns the current Space's settings as a DTO with the API key
    /// masked. Spaces with no row get a default-shaped record.
    /// </summary>
    public async Task<SpaceAiSettingsDto> GetAsync(CancellationToken ct = default)
    {
        var row = await settings.GetAsync<SpaceAiSettings>(spaceContext.CurrentSpaceId, ct).ConfigureAwait(false);

        return new SpaceAiSettingsDto
        {
            Provider          = row.Provider,
            Model             = row.Model,
            ApiKeyMasked      = MaskApiKey(row.ApiKeyEncrypted),
            HasApiKey         = !string.IsNullOrEmpty(row.ApiKeyEncrypted),
            BaseUrl           = row.BaseUrl,
            BudgetUsdPerMonth = row.BudgetUsdPerMonth,
            LogPromptBodies   = row.LogPromptBodies,
            DiagnosisEnabled  = row.DiagnosisEnabled,
            McpEnabled        = row.McpEnabled,
            AdhocEnabled      = row.AdhocEnabled,
            AdhocMaxIterations = row.AdhocMaxIterations,
            AdhocTwoPersonApproval = row.AdhocTwoPersonApproval,
            AssistantEnabled  = row.AssistantEnabled,
        };
    }

    /// <summary>
    /// Writes the update. <see cref="UpdateSpaceAiSettingsRequest.ApiKey"/>
    /// semantics:
    /// <list type="bullet">
    ///   <item><c>null</c> or empty whitespace → preserve existing ciphertext (sentinel).</item>
    ///   <item>Any non-empty value → encrypt + replace.</item>
    ///   <item>The literal string <c>"<CLEAR>"</c> → set to null (explicit clear).</item>
    /// </list>
    /// </summary>
    public async Task<SpaceAiSettingsDto> UpdateAsync(
        UpdateSpaceAiSettingsRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        await settings.MutateAsync<SpaceAiSettings>(spaceContext.CurrentSpaceId, row =>
        {
            row.Provider          = request.Provider;
            row.Model             = request.Model;
            row.BaseUrl           = request.BaseUrl;
            row.BudgetUsdPerMonth = request.BudgetUsdPerMonth;
            row.LogPromptBodies   = request.LogPromptBodies;
            row.DiagnosisEnabled  = request.DiagnosisEnabled;
            row.McpEnabled        = request.McpEnabled;
            row.AdhocEnabled      = request.AdhocEnabled;
            row.AdhocMaxIterations = request.AdhocMaxIterations;
            row.AdhocTwoPersonApproval = request.AdhocTwoPersonApproval;
            row.AssistantEnabled  = request.AssistantEnabled;

            if (request.ApiKey == ApiKeyClearSentinel)
            {
                row.ApiKeyEncrypted = null;
            }
            else if (!string.IsNullOrWhiteSpace(request.ApiKey))
            {
                row.ApiKeyEncrypted = encryption.Encrypt(request.ApiKey);
            }
            // else: leave ApiKeyEncrypted alone (preserve existing key on edits
            // that don't touch the key field — operator changing only Model
            // shouldn't accidentally clear the key).

            return row;
        }, ct).ConfigureAwait(false);

        logger.LogInformation(
            "SpaceAiSettings updated for Space {SpaceId}: Provider={Provider}, " +
            "Model={Model}, ApiKeyChanged={ApiKeyChanged}, BudgetUsd={Budget}.",
            spaceContext.CurrentSpaceId, request.Provider, request.Model,
            !string.IsNullOrWhiteSpace(request.ApiKey), request.BudgetUsdPerMonth);

        return await GetAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the decrypted API key. The REST endpoint must audit-log
    /// every call regardless of outcome — operators reading the key is a
    /// sensitive operation.
    /// </summary>
    /// <returns>
    /// The plaintext key, or <c>null</c> when no key is configured.
    /// </returns>
    public async Task<string?> RevealApiKeyAsync(CancellationToken ct = default)
    {
        var row = await settings.TryGetAsync<SpaceAiSettings>(spaceContext.CurrentSpaceId, ct).ConfigureAwait(false);
        return string.IsNullOrEmpty(row?.ApiKeyEncrypted)
            ? null
            : encryption.Decrypt(row.ApiKeyEncrypted);
    }

    /// <summary>
    /// Returns the current Space's month-to-date AI usage breakdown for
    /// the settings page's "Spent this month" readout. Includes per-feature
    /// rollup so the operator can see which sub-feature is burning budget.
    /// </summary>
    public async Task<SpaceAiUsageDto> GetUsageAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now           = DateTimeOffset.UtcNow;
        var startOfMonth  = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

        // Per-feature rollup. Token totals + cost. Empty result set =
        // no calls this month → zeros.
        var rollup = await db.AiCallLogs
            .Where(x => x.CreatedUtc >= startOfMonth)
            .GroupBy(x => x.Feature)
            .Select(g => new SpaceAiUsageFeatureBreakdown
            {
                Feature           = g.Key,
                Calls             = g.Count(),
                PromptTokens      = g.Sum(x => x.PromptTokens),
                CompletionTokens  = g.Sum(x => x.CompletionTokens),
                CostUsd           = g.Sum(x => x.CostUsd),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new SpaceAiUsageDto
        {
            MonthStartUtc      = startOfMonth,
            TotalCalls         = rollup.Sum(r => r.Calls),
            TotalCostUsd       = rollup.Sum(r => r.CostUsd),
            FeatureBreakdown   = rollup,
        };
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Sentinel value the PUT request uses to explicitly clear the API
    /// key (distinct from "field omitted" which preserves the existing
    /// key). Documented in <see cref="UpdateSpaceAiSettingsRequest.ApiKey"/>.
    /// </summary>
    public const string ApiKeyClearSentinel = "<CLEAR>";

    private static void ValidateRequest(UpdateSpaceAiSettingsRequest request)
    {
        if (request.BudgetUsdPerMonth < 0m)
        {
            throw new ArgumentException(
                "BudgetUsdPerMonth must be ≥ 0 (0 = no cap).",
                nameof(request));
        }

        if (request.AdhocMaxIterations is < 1 or > 20)
        {
            throw new ArgumentException(
                "AdhocMaxIterations must be between 1 and 20 — each iteration is " +
                "a full LLM round-trip plus a dispatch against live targets.",
                nameof(request));
        }

        // Provider-specific BaseUrl requirement is enforced by the client
        // factory at AI-call time (KrakenAiClientFactory). We don't
        // duplicate that check here so the source of truth stays single.

        if (!string.IsNullOrEmpty(request.BaseUrl)
            && !Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out _))
        {
            throw new ArgumentException(
                $"BaseUrl '{request.BaseUrl}' is not a valid absolute URI.",
                nameof(request));
        }
    }

    /// <summary>
    /// Masking format for the GET response: 8 bullets + last 4 chars of
    /// the CIPHERTEXT (not the plaintext key — we can't see plaintext
    /// without decrypting, which we deliberately don't do here). Lets the
    /// operator distinguish "key set" from "no key" + cycles when they
    /// rotate the key (ciphertext changes → masked suffix changes).
    /// </summary>
    private static string? MaskApiKey(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) { return null; }
        // Use the LAST 4 chars of the ciphertext as a stable identifier.
        // Ciphertext is base64; same plaintext re-encrypted yields a different
        // suffix because the nonce changes — that's intentional, it surfaces
        // "the key was re-saved".
        var suffix = ciphertext.Length >= 4 ? ciphertext[^4..] : ciphertext;
        return $"••••••••{suffix}";
    }
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

/// <summary>Response body for <c>GET /api/spaces/{id}/ai-settings</c>.</summary>
public sealed record SpaceAiSettingsDto
{
    public string Provider { get; init; } = KrakenAiProviderValue.Disabled;
    public string? Model { get; init; }
    /// <summary>Always masked. <c>null</c> when no key is configured.</summary>
    public string? ApiKeyMasked { get; init; }
    /// <summary><c>true</c> when ciphertext exists. Convenient for the UI.</summary>
    public bool HasApiKey { get; init; }
    public string? BaseUrl { get; init; }
    public decimal BudgetUsdPerMonth { get; init; }
    public bool LogPromptBodies { get; init; }
    public bool DiagnosisEnabled { get; init; }
    public bool McpEnabled { get; init; }
    public bool AdhocEnabled { get; init; }
    /// <summary>M11.E per-Space ad-hoc iteration cap. Defaults to 5.</summary>
    public int AdhocMaxIterations { get; init; } = 5;
    /// <summary>M11.E.11 per-Space two-person approval opt-in. Defaults to false.</summary>
    public bool AdhocTwoPersonApproval { get; init; }
    public bool AssistantEnabled { get; init; }
}

/// <summary>Request body for <c>PUT /api/spaces/{id}/ai-settings</c>.</summary>
public sealed record UpdateSpaceAiSettingsRequest
{
    public required string Provider { get; init; }
    public string? Model { get; init; }
    /// <summary>
    /// <c>null</c> or empty whitespace = preserve existing ciphertext.
    /// Literal <c>"&lt;CLEAR&gt;"</c> (see <see cref="SpaceAiSettingsService.ApiKeyClearSentinel"/>)
    /// = explicit clear. Any other value = new key, will be encrypted at rest.
    /// </summary>
    public string? ApiKey { get; init; }
    public string? BaseUrl { get; init; }
    public decimal BudgetUsdPerMonth { get; init; }
    public bool LogPromptBodies { get; init; }
    public bool DiagnosisEnabled { get; init; }
    public bool McpEnabled { get; init; }
    public bool AdhocEnabled { get; init; }
    /// <summary>
    /// M11.E per-Space ad-hoc iteration cap. Bounded 1..20 (validated).
    /// Defaults to 5 so callers that don't set it preserve current behaviour.
    /// </summary>
    public int AdhocMaxIterations { get; init; } = 5;
    /// <summary>M11.E.11 per-Space two-person approval opt-in. Defaults to false.</summary>
    public bool AdhocTwoPersonApproval { get; init; }
    public bool AssistantEnabled { get; init; }
}

/// <summary>Response body for <c>GET /api/spaces/{id}/ai-settings/usage</c>.</summary>
public sealed record SpaceAiUsageDto
{
    public DateTimeOffset MonthStartUtc { get; init; }
    public int TotalCalls { get; init; }
    public decimal TotalCostUsd { get; init; }
    public List<SpaceAiUsageFeatureBreakdown> FeatureBreakdown { get; init; } = [];
}

/// <summary>Per-feature usage row.</summary>
public sealed record SpaceAiUsageFeatureBreakdown
{
    public string Feature { get; init; } = string.Empty;
    public int Calls { get; init; }
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public decimal CostUsd { get; init; }
}

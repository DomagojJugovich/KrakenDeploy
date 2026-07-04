using KrakenDeploy.Ai;
using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Core.Domain.Variables;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services.Ai;

/// <summary>
/// Reads the current Space's <see cref="SpaceAiSettings"/> row from the DB
/// and projects it into the <see cref="KrakenAiSettings"/> record the
/// <see cref="IKrakenAi"/> wrapper consumes (Phase M11.A.6.2).
/// <para>
/// API key decryption happens here, on the request path that's about to
/// make the LLM call. The decrypted plaintext lives only inside the
/// returned record and is dropped when the wrapper's <c>using</c> over
/// the <c>IChatClient</c> exits.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <strong>Missing-row semantics:</strong> a Space without a settings row
/// gets a default-shaped <see cref="KrakenAiSettings"/> with
/// <see cref="KrakenAiProvider.Disabled"/>. The wrapper short-circuits
/// with <see cref="KrakenAiDisabledException"/> — no Space silently
/// reaches the LLM through a missing row.
/// </para>
/// <para>
/// <strong>Decryption failures</strong> (key rotation without migration,
/// tampered ciphertext) bubble as <see cref="System.Security.Cryptography.CryptographicException"/>.
/// We don't swallow — an unreadable key is a configuration error the
/// operator needs to see.
/// </para>
/// </remarks>
public sealed class DbKrakenAiSettingsProvider(
    IDbContextFactory<KrakenDbContext>   dbFactory,
    ISpaceContext                          spaceContext,
    IEncryptionService                     encryption,
    ILogger<DbKrakenAiSettingsProvider>    logger)
    : IKrakenAiSettingsProvider
{
    public async ValueTask<KrakenAiSettings> GetAsync(CancellationToken ct = default)
    {
        // No ambient Space (background-job paths that haven't WithSpace'd
        // yet) → return the default-disabled record. Wrapper throws
        // KrakenAiDisabledException; symmetric with how an unconfigured
        // Space behaves.
        var spaceId = spaceContext.CurrentSpaceId;
        if (spaceId == Guid.Empty)
        {
            return new KrakenAiSettings();
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // The global query filter (SpaceScopingInterceptor) already
        // restricts to the current Space, so a simple FirstOrDefault
        // returns the row (or null) for THIS Space.
        var row = await db.SpaceAiSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            return new KrakenAiSettings();
        }

        var provider = ParseProvider(row.Provider);
        var apiKey   = DecryptApiKey(row.ApiKeyEncrypted, spaceId);

        return new KrakenAiSettings
        {
            Provider          = provider,
            Model             = row.Model,
            ApiKey            = apiKey,
            BaseUrl           = row.BaseUrl,
            BudgetUsdPerMonth = row.BudgetUsdPerMonth,
            LogPromptBodies   = row.LogPromptBodies,
            Features = new KrakenAiFeatureFlags
            {
                DiagnosisEnabled = row.DiagnosisEnabled,
                McpEnabled       = row.McpEnabled,
                AdhocEnabled     = row.AdhocEnabled,
                AssistantEnabled = row.AssistantEnabled,
            },
        };
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// String → enum dispatch. Unknown stored values get logged + mapped
    /// to <see cref="KrakenAiProvider.Disabled"/> so a stale row from a
    /// downgraded binary doesn't crash AI on read; the operator gets a
    /// clear warning to either upgrade or clear the row.
    /// </summary>
    private KrakenAiProvider ParseProvider(string value)
    {
        if (Enum.TryParse<KrakenAiProvider>(value, ignoreCase: false, out var provider))
        {
            return provider;
        }
        logger.LogWarning(
            "SpaceAiSettings.Provider='{Value}' is not a known KrakenAiProvider; treating " +
            "as Disabled. Either upgrade the running binary or clear the row.",
            value);
        return KrakenAiProvider.Disabled;
    }

    private string? DecryptApiKey(string? ciphertext, Guid spaceId)
    {
        if (string.IsNullOrEmpty(ciphertext))
        {
            return null;
        }

        try
        {
            return encryption.Decrypt(ciphertext);
        }
        catch (Exception ex)
        {
            // Wrap with context but rethrow — under envelope encryption
            // (M13.D.2) an unreadable value means the DEK is wrong/corrupt
            // (a rotation re-encrypts all rows atomically, so it's no longer
            // "rotated without a re-encryption pass") OR the row was tampered
            // with. Both deserve operator visibility.
            logger.LogError(ex,
                "Failed to decrypt SpaceAiSettings.ApiKeyEncrypted for Space {SpaceId}. " +
                "The wrapper will surface this as a hard failure on the next AI call.",
                spaceId);
            throw;
        }
    }
}

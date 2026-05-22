using System.Security.Claims;
using KrakenDeploy.Ai;
using KrakenDeploy.Server.Core.Domain.Ai;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services.Ai;

/// <summary>
/// EF-backed <see cref="IKrakenAiCallSink"/> (Phase M11.A.3). Writes one
/// <see cref="AiCallLog"/> row per call. <c>SpaceId</c> is auto-stamped by
/// the existing <c>SpaceScopingInterceptor</c> from the ambient
/// <c>ISpaceContext</c>; <c>UserId</c> is resolved from the current request's
/// claims when available (UI-triggered paths) and left <c>null</c> for
/// background jobs (Hangfire-triggered diagnosis, MCP-triggered actions).
/// </summary>
/// <remarks>
/// The sink swallows all exceptions inside <see cref="WriteAsync"/> — a
/// failure here must NEVER break the user-facing AI call. The contract is
/// "best-effort audit"; missing rows are logged at Error level so an
/// operator can correlate against the underlying issue (DB outage, schema
/// drift, etc.).
/// </remarks>
public sealed class DbKrakenAiCallSink(
    IDbContextFactory<KrakenDbContext> dbFactory,
    IHttpContextAccessor               httpContextAccessor,
    ILogger<DbKrakenAiCallSink>        logger)
    : IKrakenAiCallSink
{
    public async ValueTask WriteAsync(AiCallLogEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

            db.AiCallLogs.Add(new AiCallLog
            {
                // SpaceId auto-stamped by SpaceScopingInterceptor.
                Provider              = entry.Provider,
                Model                 = entry.Model,
                Feature               = entry.Feature,
                PromptTokens          = entry.PromptTokens,
                CompletionTokens      = entry.CompletionTokens,
                LatencyMs             = entry.LatencyMs,
                CostUsd               = entry.CostUsd,
                Success               = entry.Success,
                ErrorMessage          = entry.ErrorMessage,
                CorrelationId         = entry.CorrelationId,
                ScrubbedVariableNames = entry.ScrubbedVariableNames,
                PromptBodyJson        = entry.PromptBodyJson,
                ResponseBody          = entry.ResponseBody,
                UserId                = TryGetCurrentUserId(),
            });

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort audit. Don't propagate — the user-facing AI call
            // must not be broken by an audit-table outage.
            logger.LogError(ex,
                "Failed to write AiCallLog row for provider {Provider} model {Model} feature {Feature}.",
                entry.Provider, entry.Model, entry.Feature);
        }
    }

    /// <summary>
    /// Resolves the current user's id from the ambient HTTP request, or
    /// <c>null</c> when no HTTP context exists (background-job paths).
    /// Mirrors the convention in <c>PermissionEvaluator.TryGetUserId</c>.
    /// </summary>
    private Guid? TryGetCurrentUserId()
    {
        var user = httpContextAccessor.HttpContext?.User;
        var idClaim = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(idClaim, out var id) ? id : null;
    }
}

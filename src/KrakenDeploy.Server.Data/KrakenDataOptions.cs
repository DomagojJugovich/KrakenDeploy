namespace KrakenDeploy.Server.Data;

/// <summary>
/// Connection-resiliency knobs for the tenant <see cref="KrakenDbContext"/>
/// (C3/T1-19). Only the WEB HOST passes these — CLI callers deliberately do not:
/// <list type="bullet">
/// <item><see cref="EnableRetryOnFailure"/> installs
/// <c>NpgsqlRetryingExecutionStrategy</c>, which is INCOMPATIBLE with the
/// user-initiated transaction in <c>encryption rotate-dek/rotate-kek</c>
/// (EF throws "does not support user-initiated transactions"). The CLI builds
/// its context via the same <c>AddKrakenDeployData</c>, so retry must stay a
/// web-host opt-in — the same split as <c>CliHost</c>'s <c>ValidateOnBuild=false</c>.</item>
/// <item>One-shot CLI commands need neither retry nor a pool cap.</item>
/// </list>
/// </summary>
public sealed class KrakenDataOptions
{
    /// <summary>
    /// Install <c>NpgsqlRetryingExecutionStrategy</c> so an in-flight query
    /// survives a transient Postgres blip / Patroni failover instead of
    /// hard-failing a deployment. MUST remain <c>false</c> for any context that
    /// opens a user-initiated transaction.
    /// </summary>
    public bool EnableRetryOnFailure { get; init; }

    /// <summary>Max retry attempts (default 6, matching Npgsql's own default).</summary>
    public int MaxRetryCount { get; init; } = 6;

    /// <summary>Max delay between retries (default 30s, matching Npgsql's own default).</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Cap the Npgsql connection pool (the "Maximum Pool Size" keyword). <c>null</c>
    /// leaves the connection-string value — or Npgsql's default of 100 — untouched.
    /// A shared single Postgres behind an HA pair sees 2 x this cap plus Hangfire's
    /// own pool against <c>max_connections</c> (default 100); see docs/ha-pair.md.
    /// </summary>
    public int? MaxPoolSize { get; init; }
}

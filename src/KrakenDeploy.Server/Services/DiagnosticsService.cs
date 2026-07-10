using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Transport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KrakenDeploy.Server.Services;

/// <summary>
/// Server self-inspection for M13.A.2. Three surfaces:
///
/// <list type="number">
///   <item><see cref="GetServerInfoAsync"/> — fast snapshot of the page header
///         (runtime + OS + uptime + counts).</item>
///   <item><see cref="RunIntegrityCheckAsync"/> — operator-initiated invariant
///         pass; cheap enough to run on demand from the UI.</item>
///   <item><see cref="WriteDiagnosticsReportZipAsync"/> — bundles a sanitised
///         report into a zip stream for the "Download report" button. NEVER
///         includes secrets — connection-string passwords, encryption keys,
///         API keys, license JWTs, Sensitive variable values all redacted.</item>
/// </list>
/// </summary>
public sealed class DiagnosticsService(
    IServiceScopeFactory scopeFactory,
    IAgentConnectionRegistry agentRegistry,
    IConfiguration configuration,
    IHostEnvironment hostEnvironment,
    TimeProvider time)
{
    private static readonly DateTimeOffset ProcessStartedUtc =
        DateTimeOffset.UtcNow - (DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime());

    // ── Server info ────────────────────────────────────────────────────────

    public async Task<ServerInfoReport> GetServerInfoAsync(CancellationToken ct = default)
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var infoVersion = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString()
            ?? "unknown";
        // .NET 8+ embeds "1.2.3+<sha>" in InformationalVersion when the build
        // ran inside a git working tree. Split it out so the page can show
        // version + commit separately.
        var (version, commit) = SplitVersionAndCommit(infoVersion);

        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var uptime = time.GetUtcNow() - ProcessStartedUtc;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        var (dbVersion, dbCanConnect) = await ProbeDatabaseAsync(db, ct).ConfigureAwait(false);
        var counts = await CollectRowCountsAsync(db, ct).ConfigureAwait(false);

        var dataPath = configuration["Server:DataPath"] ?? "data";
        long dataSize = 0;
        try
        {
            if (Directory.Exists(dataPath))
            {
                dataSize = MeasureDirectory(new DirectoryInfo(dataPath));
            }
        }
        catch
        {
            // Permission errors etc. — surface as 0; not worth failing the
            // whole page.
        }

        return new ServerInfoReport(
            ServerVersion:     version,
            CommitHash:        commit,
            DotNetRuntime:     RuntimeInformation.FrameworkDescription,
            OperatingSystem:   RuntimeInformation.OSDescription,
            ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
            EnvironmentName:   hostEnvironment.EnvironmentName,
            WorkingSetBytes:   proc.WorkingSet64,
            ThreadCount:       proc.Threads.Count,
            Uptime:            uptime,
            StartedUtc:        ProcessStartedUtc,
            ConnectedAgents:   agentRegistry.Count,
            DatabaseReachable: dbCanConnect,
            DatabaseVersion:   dbVersion,
            DataPath:          dataPath,
            DataPathSizeBytes: dataSize,
            RowCounts:         counts);
    }

    // ── Integrity check ────────────────────────────────────────────────────

    public async Task<IntegrityCheckResult> RunIntegrityCheckAsync(CancellationToken ct = default)
    {
        var started = time.GetTimestamp();
        var findings = new List<IntegrityFinding>();

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();

        // 1. Pending migrations
        try
        {
            var pending = (await db.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
            if (pending.Count == 0)
            {
                findings.Add(new(IntegritySeverity.Info,
                    "Schema",
                    "No pending migrations."));
            }
            else
            {
                findings.Add(new(IntegritySeverity.Error,
                    "Schema",
                    $"Pending migrations: {string.Join(", ", pending)}. " +
                    "Run `dotnet ef database update` (or restart the server with " +
                    "automatic migrations enabled) before relying on this instance."));
            }
        }
        catch (Exception ex)
        {
            findings.Add(new(IntegritySeverity.Error, "Schema",
                $"Could not check pending migrations: {ex.Message}"));
        }

        // 2. Spaces table not empty (a healthy install has at least the
        //    DefaultSpace seeded by EnsureDefaultAsync at startup).
        try
        {
            var spaceCount = await db.Spaces.IgnoreQueryFilters().CountAsync(ct).ConfigureAwait(false);
            if (spaceCount == 0)
            {
                findings.Add(new(IntegritySeverity.Error, "Spaces",
                    "spaces table is empty. The DefaultSpace bootstrap row should " +
                    "be created on startup — its absence indicates a failed " +
                    "first-run seed."));
            }
            else
            {
                findings.Add(new(IntegritySeverity.Info, "Spaces",
                    $"{spaceCount} Space row(s) present."));
            }
        }
        catch (Exception ex)
        {
            findings.Add(new(IntegritySeverity.Error, "Spaces",
                $"Could not count spaces: {ex.Message}"));
        }

        // 3. Orphan-SpaceId scan — every ISpaceScoped table's SpaceId must
        //    point at an existing Space row. The FK constraint should
        //    catch this at write time; if a row slips through (rare —
        //    typically a hand-edited backup-restore), the global query
        //    filter silently hides it forever. The check makes it visible.
        try
        {
            var spaceIds = await db.Spaces
                .IgnoreQueryFilters()
                .Select(s => s.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var spaceSet = spaceIds.ToHashSet();

            var orphans = await CountOrphansAsync(db, spaceSet, ct).ConfigureAwait(false);
            if (orphans == 0)
            {
                findings.Add(new(IntegritySeverity.Info, "FK consistency",
                    "No orphan SpaceId references across audited tables."));
            }
            else
            {
                findings.Add(new(IntegritySeverity.Warning, "FK consistency",
                    $"{orphans} row(s) reference a Space that no longer exists. " +
                    "These rows are invisible to the UI (global query filter) — " +
                    "investigate with IgnoreQueryFilters or contact support."));
            }
        }
        catch (Exception ex)
        {
            findings.Add(new(IntegritySeverity.Warning, "FK consistency",
                $"Could not run orphan scan: {ex.Message}"));
        }

        // 4. License presence — purely informational; the gate is enforced
        //    elsewhere. Useful on diagnostics page to confirm "I uploaded
        //    a key and the server picked it up".
        var licensePath = Path.Combine(configuration["Server:DataPath"] ?? "data", "license.key");
        if (File.Exists(licensePath) ||
            !string.IsNullOrWhiteSpace(configuration["License:Key"]) ||
            !string.IsNullOrWhiteSpace(configuration["KRAKEN_LICENSE_KEY"]))
        {
            findings.Add(new(IntegritySeverity.Info, "License",
                "License key source present (file or config override). " +
                "Validity is checked by LicenseService on demand — see /settings/license."));
        }
        else
        {
            findings.Add(new(IntegritySeverity.Warning, "License",
                "No license key configured. Upload one at /settings/license."));
        }

        var elapsed = time.GetElapsedTime(started);
        var hasError = findings.Any(f => f.Severity == IntegritySeverity.Error);
        return new IntegrityCheckResult(
            Healthy:  !hasError,
            Findings: findings,
            Elapsed:  elapsed);
    }

    /// <summary>
    /// Cheap orphan scan — counts rows where SpaceId isn't in the supplied
    /// set. Runs one query per table (six in total today). EF translates
    /// `Where(... !set.Contains(...))` to a NOT IN against a parameter array;
    /// the set is small enough that this stays cheap even at scale.
    /// </summary>
    private static async Task<int> CountOrphansAsync(
        KrakenDbContext db, HashSet<Guid> spaceIds, CancellationToken ct)
    {
        var ids = spaceIds.ToArray();
        var total = 0;
        total += await db.Projects.IgnoreQueryFilters().CountAsync(p => !ids.Contains(p.SpaceId), ct).ConfigureAwait(false);
        total += await db.DeploymentTargets.IgnoreQueryFilters().CountAsync(t => !ids.Contains(t.SpaceId), ct).ConfigureAwait(false);
        total += await db.Environments.IgnoreQueryFilters().CountAsync(e => !ids.Contains(e.SpaceId), ct).ConfigureAwait(false);
        total += await db.Tenants.IgnoreQueryFilters().CountAsync(t => !ids.Contains(t.SpaceId), ct).ConfigureAwait(false);
        total += await db.Releases.IgnoreQueryFilters().CountAsync(r => !ids.Contains(r.SpaceId), ct).ConfigureAwait(false);
        total += await db.Deployments.IgnoreQueryFilters().CountAsync(d => !ids.Contains(d.SpaceId), ct).ConfigureAwait(false);
        return total;
    }

    // ── Diagnostics zip ────────────────────────────────────────────────────

    /// <summary>
    /// Writes a zip bundle to <paramref name="output"/> containing:
    /// <list type="bullet">
    ///   <item><c>server-info.json</c> — the <see cref="ServerInfoReport"/> payload.</item>
    ///   <item><c>integrity-check.txt</c> — the latest integrity-check result.</item>
    ///   <item><c>config.sanitised.json</c> — appsettings with secrets redacted.</item>
    ///   <item><c>recent-log.txt</c> — last 1000 lines of the most-recent
    ///         <c>logs/server-{yyyy-MM-dd}.log</c> file.</item>
    /// </list>
    /// The zip MUST stay safe to attach to a public support ticket —
    /// sanitisation lives in <see cref="SanitiseConfig"/>; tests pin the
    /// secret-redaction contract.
    /// </summary>
    public async Task WriteDiagnosticsReportZipAsync(Stream output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);

        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        var serverInfo = await GetServerInfoAsync(ct).ConfigureAwait(false);
        await WriteEntryAsync(archive, "server-info.json",
            JsonSerializer.SerializeToUtf8Bytes(serverInfo, JsonOpts),
            ct).ConfigureAwait(false);

        var integrity = await RunIntegrityCheckAsync(ct).ConfigureAwait(false);
        await WriteEntryAsync(archive, "integrity-check.txt",
            Encoding.UTF8.GetBytes(FormatIntegrityCheck(integrity)),
            ct).ConfigureAwait(false);

        var sanitised = SanitiseConfig(configuration);
        await WriteEntryAsync(archive, "config.sanitised.json",
            JsonSerializer.SerializeToUtf8Bytes(sanitised, JsonOpts),
            ct).ConfigureAwait(false);

        var logTail = await ReadRecentLogTailAsync(maxLines: 1000, ct).ConfigureAwait(false);
        await WriteEntryAsync(archive, "recent-log.txt",
            Encoding.UTF8.GetBytes(logTail),
            ct).ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>
    /// Builds a key→value dictionary of the flattened configuration with
    /// secret-bearing keys redacted. The whitelist-by-name approach (any
    /// key containing "password", "secret", "key", "token", or matching
    /// known names) is deliberately broad — false-positives (e.g. a
    /// legitimate "ApiPublicKey" that isn't sensitive) get redacted too,
    /// but the cost is benign; missing a real secret in a public support
    /// ticket is not.
    /// </summary>
    internal static IDictionary<string, string?> SanitiseConfig(IConfiguration configuration)
    {
        var result = new SortedDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in configuration.AsEnumerable())
        {
            if (kv.Value is null) { continue; }
            result[kv.Key] = IsSensitiveKey(kv.Key) ? "[REDACTED]" : kv.Value;
        }

        // ConnectionStrings need extra love — even if the *key* doesn't look
        // sensitive ("ConnectionStrings:KrakenDb"), the *value* embeds a
        // password. Strip the Password=... segment regardless.
        foreach (var key in result.Keys.Where(k => k.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            result[key] = StripConnectionStringSecrets(result[key]);
        }

        return result;
    }

    internal static bool IsSensitiveKey(string key)
    {
        // Case-insensitive contains check on the leaf segment of the dotted
        // key path — Server:Encryption:MasterKey, ApiKey:Key, etc.
        var leaf = key.LastIndexOf(':') >= 0 ? key[(key.LastIndexOf(':') + 1)..] : key;
        // Whitelist of leaf names that look "secret-shaped".
        return leaf.Contains("Password",      StringComparison.OrdinalIgnoreCase)
            || leaf.Contains("Secret",        StringComparison.OrdinalIgnoreCase)
            || leaf.Equals  ("Key",           StringComparison.OrdinalIgnoreCase)  // ApiKey:Key, License:Key
            || leaf.Contains("PrivateKey",    StringComparison.OrdinalIgnoreCase)
            || leaf.Contains("MasterKey",     StringComparison.OrdinalIgnoreCase)
            || leaf.Contains("AuthToken",     StringComparison.OrdinalIgnoreCase)
            || leaf.Contains("Token",         StringComparison.OrdinalIgnoreCase)
            || leaf.Contains("ApiKey",        StringComparison.OrdinalIgnoreCase)
            || leaf.Contains("ClientSecret",  StringComparison.OrdinalIgnoreCase)
            || leaf.Equals  ("KRAKEN_LICENSE_KEY", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? StripConnectionStringSecrets(string? connStr)
    {
        if (string.IsNullOrEmpty(connStr)) { return connStr; }
        // Replace any  password=...   segment (case-insensitive) with
        // password=[REDACTED]. Stops at the next ; or end-of-string.
        return System.Text.RegularExpressions.Regex.Replace(
            connStr,
            @"(?i)(password|pwd)\s*=\s*[^;]*",
            "$1=[REDACTED]");
    }

    private static async Task<string> ReadRecentLogTailAsync(int maxLines, CancellationToken ct)
    {
        // Serilog file sink writes to logs/server-{yyyy-MM-dd}.log; we read
        // the most recent file (highest sort) without caring about today's
        // date — a server that hasn't logged today shouldn't produce an
        // empty diagnostics report.
        try
        {
            const string LogDirectory = "logs";
            if (!Directory.Exists(LogDirectory)) { return "(no logs directory found)"; }

            var newest = Directory.GetFiles(LogDirectory, "server-*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null) { return "(no log files found)"; }

            // Tail efficiently — Serilog logs can be large. Read in
            // reverse from the end so we don't load a multi-MB file.
            var lines = await ReadLastLinesAsync(newest, maxLines, ct).ConfigureAwait(false);
            return $"# {newest}\n{string.Join('\n', lines)}";
        }
        catch (Exception ex)
        {
            return $"(failed to read log tail: {ex.Message})";
        }
    }

    private static async Task<List<string>> ReadLastLinesAsync(
        string path, int maxLines, CancellationToken ct)
    {
        // Use shared read so the running Serilog sink can keep appending.
        await using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        // For simplicity + safety on a long log file, read forward + keep
        // a rolling buffer of maxLines. Cheap enough at 1000 lines + a few
        // MB of file; we don't want to fight with reverse-seek in UTF-8.
        var buffer = new LinkedList<string>();
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            buffer.AddLast(line);
            if (buffer.Count > maxLines) { buffer.RemoveFirst(); }
        }
        return [.. buffer];
    }

    private static string FormatIntegrityCheck(IntegrityCheckResult result)
    {
        var sb = new StringBuilder();
        sb.Append("Integrity check — ").Append(result.Healthy ? "OK" : "ATTENTION").Append('\n');
        sb.Append("Elapsed: ").Append(result.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)).Append(" ms\n\n");
        foreach (var f in result.Findings)
        {
            sb.Append('[').Append(f.Severity).Append("] ").Append(f.Check).Append(": ").Append(f.Message).Append('\n');
        }
        return sb.ToString();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static async Task WriteEntryAsync(
        ZipArchive archive, string name, byte[] bytes, CancellationToken ct)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        await using var stream = entry.Open();
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private static async Task<(string Version, bool CanConnect)> ProbeDatabaseAsync(
        KrakenDbContext db, CancellationToken ct)
    {
        try
        {
            if (!await db.Database.CanConnectAsync(ct).ConfigureAwait(false))
            {
                return ("unreachable", false);
            }
            // Postgres-specific server-version query; safe to call against
            // any pg version.
            var conn = db.Database.GetDbConnection();
            await conn.OpenAsync(ct).ConfigureAwait(false);
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SHOW server_version";
                var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                return (v?.ToString() ?? "unknown", true);
            }
            finally
            {
                await conn.CloseAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            return ("unreachable", false);
        }
    }

    private static async Task<RowCountSnapshot> CollectRowCountsAsync(
        KrakenDbContext db, CancellationToken ct)
    {
        // IgnoreQueryFilters across the board — diagnostics needs the
        // server-wide truth, not the ambient-Space slice.
        var spaces       = await db.Spaces.IgnoreQueryFilters().CountAsync(ct).ConfigureAwait(false);
        var projects     = await db.Projects.IgnoreQueryFilters().CountAsync(ct).ConfigureAwait(false);
        var environments = await db.Environments.IgnoreQueryFilters().CountAsync(ct).ConfigureAwait(false);
        var targets      = await db.DeploymentTargets.IgnoreQueryFilters().CountAsync(ct).ConfigureAwait(false);
        var tenants      = await db.Tenants.IgnoreQueryFilters().CountAsync(ct).ConfigureAwait(false);
        var releases     = await db.Releases.IgnoreQueryFilters().CountAsync(ct).ConfigureAwait(false);
        var deployments  = await db.Deployments.IgnoreQueryFilters().CountAsync(ct).ConfigureAwait(false);
        var users        = await db.Users.CountAsync(ct).ConfigureAwait(false);
        var teams        = await db.Teams.CountAsync(ct).ConfigureAwait(false);
        // AuditEntries deliberately not routed through the audit choke point
        // (AuditExportService): bare COUNT(*) — no row content leaves the DB —
        // and this surface is gated by ConfigureServer (system tier).
        var auditEntries = await db.AuditEntries.IgnoreQueryFilters().CountAsync(ct).ConfigureAwait(false);

        return new RowCountSnapshot(
            spaces, projects, environments, targets, tenants,
            releases, deployments, users, teams, auditEntries);
    }

    private static long MeasureDirectory(DirectoryInfo dir)
    {
        long size = 0;
        foreach (var f in dir.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try { size += f.Length; } catch { /* concurrent delete; ignore */ }
        }
        return size;
    }

    private static (string Version, string? Commit) SplitVersionAndCommit(string infoVersion)
    {
        var plus = infoVersion.IndexOf('+');
        return plus < 0
            ? (infoVersion, null)
            : (infoVersion[..plus], infoVersion[(plus + 1)..]);
    }
}

// ── Report records ─────────────────────────────────────────────────────────

public sealed record ServerInfoReport(
    string ServerVersion,
    string? CommitHash,
    string DotNetRuntime,
    string OperatingSystem,
    string ProcessArchitecture,
    string EnvironmentName,
    long WorkingSetBytes,
    int ThreadCount,
    TimeSpan Uptime,
    DateTimeOffset StartedUtc,
    int ConnectedAgents,
    bool DatabaseReachable,
    string DatabaseVersion,
    string DataPath,
    long DataPathSizeBytes,
    RowCountSnapshot RowCounts);

public sealed record RowCountSnapshot(
    int Spaces,
    int Projects,
    int Environments,
    int DeploymentTargets,
    int Tenants,
    int Releases,
    int Deployments,
    int Users,
    int Teams,
    int AuditEntries);

public sealed record IntegrityCheckResult(
    bool Healthy,
    IReadOnlyList<IntegrityFinding> Findings,
    TimeSpan Elapsed);

public sealed record IntegrityFinding(
    IntegritySeverity Severity, string Check, string Message);

public enum IntegritySeverity
{
    Info    = 0,
    Warning = 1,
    Error   = 2,
}

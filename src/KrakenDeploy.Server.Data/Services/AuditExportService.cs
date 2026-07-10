using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using KrakenDeploy.Server.Core.Domain.Audit;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Streams filtered <see cref="AuditEntry"/> rows to a caller-provided
/// output stream in CSV or JSON form. The audit log can grow to hundreds of
/// thousands of rows at long retention windows; we deliberately avoid
/// buffering the whole result-set into a string before flushing — each row
/// is read from the DB and written to the stream individually.
///
/// <para>
/// The filter parameters mirror the <c>Audit.razor</c> page's filter UI so
/// "Export" delivers exactly what the operator sees on screen.
/// </para>
/// <para>
/// This class is also the single query choke point for reading audit rows:
/// <see cref="AuditEntry"/> is not <c>ISpaceScoped</c> (no global query
/// filter), so every read of <c>db.AuditEntries</c> must go through
/// <see cref="ApplySpaceVisibility"/> / <see cref="ApplyScopedFilter"/> —
/// or carry a comment justifying why not. Audit rows contain full
/// before/after entity snapshots; an unscoped read is a cross-Space data
/// leak.
/// </para>
/// </summary>
public sealed class AuditExportService(IDbContextFactory<KrakenDbContext> dbFactory)
{
    /// <summary>
    /// Filter shape shared between the page and the export endpoints.
    /// <para>
    /// <paramref name="SpaceIds"/> is required and positional on purpose: a
    /// <see cref="Filter"/> cannot be constructed without a Space decision.
    /// Callers must only pass Space ids the current user is entitled to —
    /// validated against <c>IPermissionEvaluator.GetAccessibleSpaceIdsAsync</c>
    /// (the page's Space is validated by <c>SpaceScopedComponentBase</c>;
    /// the export endpoints validate explicitly).
    /// </para>
    /// <para>
    /// <paramref name="IncludeSystemRows"/> adds rows with <c>SpaceId IS NULL</c>
    /// (platform-level events: license uploads, KEK rotations, sign-ins …).
    /// It must only be set <c>true</c> after verifying the caller holds
    /// <c>Permission.AdministerSystem</c>.
    /// </para>
    /// </summary>
    public sealed record Filter(
        IReadOnlyCollection<Guid> SpaceIds,
        bool            IncludeSystemRows,
        DateTimeOffset? FromUtc,
        DateTimeOffset? ToUtcExclusive,
        string?         EventTypeContains,
        string?         UserDisplayContains,
        string?         SubjectTypeContains);

    /// <summary>
    /// THE audit Space-visibility choke point. Restricts <paramref name="source"/>
    /// to rows whose <c>SpaceId</c> is in <paramref name="spaceIds"/>, plus —
    /// only when <paramref name="includeSystemRows"/> — rows with no Space
    /// (platform-level events). Every audit read must flow through here.
    /// </summary>
    public static IQueryable<AuditEntry> ApplySpaceVisibility(
        IQueryable<AuditEntry> source,
        IReadOnlyCollection<Guid> spaceIds,
        bool includeSystemRows)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(spaceIds);

        // List<T> so EF translates Contains to "= ANY(@spaceIds)".
        var ids = spaceIds as List<Guid> ?? spaceIds.ToList();
        return source.Where(e =>
            (e.SpaceId != null && ids.Contains(e.SpaceId.Value))
            || (includeSystemRows && e.SpaceId == null));
    }

    /// <summary>
    /// Composes the full scoped audit query: Space visibility first, then the
    /// date/text filters. Shared by the <c>Audit.razor</c> page grid and both
    /// export formats so screen and download can never disagree on which rows
    /// the caller may see.
    /// </summary>
    public static IQueryable<AuditEntry> ApplyScopedFilter(
        IQueryable<AuditEntry> source, Filter f)
    {
        ArgumentNullException.ThrowIfNull(f);

        var q = ApplySpaceVisibility(source, f.SpaceIds, f.IncludeSystemRows);
        if (f.FromUtc.HasValue)
        {
            var from = f.FromUtc.Value;
            q = q.Where(e => e.OccurredUtc >= from);
        }
        if (f.ToUtcExclusive.HasValue)
        {
            var to = f.ToUtcExclusive.Value;
            q = q.Where(e => e.OccurredUtc < to);
        }
        if (!string.IsNullOrWhiteSpace(f.EventTypeContains))
        {
            var s = f.EventTypeContains.Trim();
            q = q.Where(e => e.EventType.Contains(s));
        }
        if (!string.IsNullOrWhiteSpace(f.UserDisplayContains))
        {
            var s = f.UserDisplayContains.Trim();
            q = q.Where(e => e.UserDisplay.Contains(s));
        }
        if (!string.IsNullOrWhiteSpace(f.SubjectTypeContains))
        {
            var s = f.SubjectTypeContains.Trim();
            q = q.Where(e => e.SubjectType != null && e.SubjectType.Contains(s));
        }
        return q;
    }

    /// <summary>
    /// Writes a CSV file to <paramref name="output"/>. The file starts with
    /// a UTF-8 BOM so Excel auto-detects UTF-8 (without it Excel mis-decodes
    /// Croatian diacritics in customer / display names). RFC 4180 escaping.
    /// </summary>
    public async Task WriteCsvAsync(
        Stream output, Filter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(filter);

        await using var writer = new StreamWriter(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            leaveOpen: true);

        // Header
        await writer.WriteLineAsync(string.Join(',', [
            "OccurredUtc",
            "EventType",
            "UserDisplay",
            "UserId",
            "IpAddress",
            "SpaceId",
            "SubjectType",
            "SubjectId",
            "SubjectName",
            "Details",
        ])).ConfigureAwait(false);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = ApplyScopedFilter(db.AuditEntries.AsNoTracking(), filter)
            .OrderByDescending(e => e.OccurredUtc)
            .AsAsyncEnumerable();

        await foreach (var e in query.WithCancellation(ct))
        {
            await writer.WriteLineAsync(string.Join(',', [
                e.OccurredUtc.ToString("O", CultureInfo.InvariantCulture),
                CsvEscape(e.EventType),
                CsvEscape(e.UserDisplay),
                e.UserId?.ToString() ?? "",
                CsvEscape(e.IpAddress),
                e.SpaceId?.ToString() ?? "",
                CsvEscape(e.SubjectType),
                CsvEscape(e.SubjectId),
                CsvEscape(e.SubjectName),
                CsvEscape(e.Details),
            ])).ConfigureAwait(false);
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a JSON array of audit rows to <paramref name="output"/>. The
    /// before/after snapshot JSON (used for EF entity changes) is included
    /// inline — they're already JSON, so we drop them in raw rather than
    /// double-stringifying. CSV consumers usually skip these because they
    /// can be large; JSON consumers expect to round-trip the full row.
    /// </summary>
    public async Task WriteJsonAsync(
        Stream output, Filter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(filter);

        var options = new JsonWriterOptions
        {
            Indented        = false,
            Encoder         = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            SkipValidation  = false,
        };
        await using var json = new Utf8JsonWriter(output, options);
        json.WriteStartArray();

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = ApplyScopedFilter(db.AuditEntries.AsNoTracking(), filter)
            .OrderByDescending(e => e.OccurredUtc)
            .AsAsyncEnumerable();

        await foreach (var e in query.WithCancellation(ct))
        {
            json.WriteStartObject();
            json.WriteString("occurredUtc",  e.OccurredUtc);
            json.WriteString("eventType",    e.EventType);
            json.WriteString("userDisplay",  e.UserDisplay);
            if (e.UserId.HasValue)      { json.WriteString("userId",     e.UserId.Value); }
            if (e.SpaceId.HasValue)     { json.WriteString("spaceId",    e.SpaceId.Value); }
            if (e.IpAddress is not null){ json.WriteString("ipAddress",  e.IpAddress); }
            if (e.UserAgent is not null){ json.WriteString("userAgent",  e.UserAgent); }
            if (e.SubjectType is not null) { json.WriteString("subjectType", e.SubjectType); }
            if (e.SubjectId is not null)   { json.WriteString("subjectId",   e.SubjectId); }
            if (e.SubjectName is not null) { json.WriteString("subjectName", e.SubjectName); }
            if (e.Details is not null)     { json.WriteString("details",     e.Details); }
            // BeforeJson / AfterJson are already JSON text. Stream them in
            // as raw so consumers can deserialise the row in one pass.
            if (!string.IsNullOrEmpty(e.BeforeJson))
            {
                json.WritePropertyName("before");
                json.WriteRawValue(e.BeforeJson, skipInputValidation: true);
            }
            if (!string.IsNullOrEmpty(e.AfterJson))
            {
                json.WritePropertyName("after");
                json.WriteRawValue(e.AfterJson, skipInputValidation: true);
            }
            json.WriteEndObject();

            // Flush every batch — the default buffer is 16 KB and we want
            // the response to start arriving at the client before the full
            // query completes (large audit windows can take seconds).
            if (json.BytesPending > 16 * 1024)
            {
                await json.FlushAsync(ct).ConfigureAwait(false);
            }
        }

        json.WriteEndArray();
        await json.FlushAsync(ct).ConfigureAwait(false);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// RFC 4180 escape: empty string for null; wrap in double quotes when the
    /// value contains comma, quote, CR or LF; escape internal quotes by
    /// doubling them. Returns the value unchanged when no escaping is
    /// needed.
    /// </summary>
    internal static string CsvEscape(string? value)
    {
        if (string.IsNullOrEmpty(value)) { return ""; }

        var needsQuoting = false;
        foreach (var c in value)
        {
            if (c is ',' or '"' or '\r' or '\n') { needsQuoting = true; break; }
        }
        if (!needsQuoting) { return value; }

        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            if (c == '"') { sb.Append('"'); }
            sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString();
    }
}

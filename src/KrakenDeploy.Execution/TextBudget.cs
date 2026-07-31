namespace KrakenDeploy.Execution;

/// <summary>
/// Bounding free text that crosses a trust or storage boundary.
/// <para>
/// Lives here because this is the only assembly both <c>KrakenDeploy.Agent</c> and
/// <c>KrakenDeploy.Server.Core</c> reference (the Agent must never reference <c>Server.*</c>),
/// and three separate places had each grown their own version: the audit entity's
/// <c>Details</c> setter, the wire-contract gate's header echo, and the agent's forwarding of a
/// server-supplied deferral reason. All three bound the SAME data path — free text into
/// <c>AuditEntry.Details</c> and onward to the webhook, e-mail and AI-inspect transports — with
/// three different markers and one shared bug.
/// </para>
/// </summary>
public static class TextBudget
{
    /// <summary>Marker appended when text was trimmed. One char, so a cap of N yields N.</summary>
    public const char Ellipsis = '…';

    /// <summary>
    /// Trims <paramref name="value"/> so its length never exceeds <paramref name="max"/>,
    /// appending <see cref="Ellipsis"/> when it had to cut. Returns the input unchanged when it
    /// already fits, and <c>null</c> for <c>null</c>.
    /// <para>
    /// Cuts on a RUNE boundary, never a UTF-16 code unit. Slicing with <c>value[..max]</c> or
    /// <c>AsSpan(0, max)</c> can split a surrogate pair and leave a lone high surrogate, which
    /// is not valid UTF-16 — and Npgsql's parameter writer uses
    /// <c>EncoderExceptionFallback</c>, so persisting such a string THROWS rather than storing
    /// it. That turned an over-long detail from "stored truncated" into a 500 that lost the
    /// whole audit row, which is how this helper came to exist. <c>System.Text.Json</c> and
    /// <c>Response.WriteAsync</c> substitute U+FFFD instead, so the corruption was invisible
    /// upstream of the database.
    /// </para>
    /// </summary>
    public static string? Trim(string? value, int max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);

        if (value is null || value.Length <= max)
        {
            return value;
        }

        // Room for the marker, then step back off a trailing high surrogate so the cut never
        // lands between the two halves of a pair.
        var cut = max - 1;
        if (cut > 0 && char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        return string.Concat(value.AsSpan(0, cut), stackalloc[] { Ellipsis });
    }

    /// <summary>
    /// <see cref="Trim(string?, int)"/> plus the original length, for a diagnostic that wants to
    /// say how much was dropped: <c>"abc… (30000 chars)"</c>. Never exceeds
    /// <paramref name="max"/> for the text portion; the suffix is bounded by construction.
    /// </summary>
    public static string Describe(string value, int max)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length <= max
            ? $"\"{value}\""
            : $"\"{Trim(value, max)}\" ({value.Length} chars)";
    }
}

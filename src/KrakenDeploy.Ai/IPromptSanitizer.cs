using Microsoft.Extensions.AI;

namespace KrakenDeploy.Ai;

/// <summary>
/// Strips <c>Sensitive</c>-flagged variable values from prompts before they
/// reach an external LLM (Phase M11.A.4). Each value found in a chat
/// message's text is replaced with the marker
/// <c>[REDACTED:&lt;variableName&gt;]</c> — the variable's NAME is kept
/// (the LLM still needs to understand that "the database password" is the
/// thing being referenced), but the VALUE is gone before any byte leaves
/// the process.
/// <para>
/// Replacement is a literal find-and-replace over <see cref="ChatMessage.Text"/>.
/// Values are matched longest-first so a longer sensitive value (e.g.
/// <c>secret-key-with-prefix</c>) isn't broken in half by a shorter
/// overlapping one (e.g. <c>secret-key</c>).
/// </para>
/// </summary>
public interface IPromptSanitizer
{
    /// <summary>
    /// Returns sanitised messages + the names of variables whose values
    /// were substituted at least once. The caller passes those names to
    /// the audit sink so an operator can see, after the fact, which
    /// sensitive values were prevented from leaking.
    /// </summary>
    /// <param name="messages">The original messages bound for the LLM.</param>
    /// <param name="sensitiveValuesByName">
    /// Map of variable name → sensitive value, supplied by the calling
    /// feature (M11.C / M11.D / M11.E). Sanitiser doesn't know which Space
    /// is active — it just substitutes whatever the caller hands it.
    /// </param>
    SanitizationResult Sanitize(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyDictionary<string, string> sensitiveValuesByName);
}

/// <summary>Outcome of <see cref="IPromptSanitizer.Sanitize"/>.</summary>
public sealed record SanitizationResult(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<string>      ScrubbedNames);

/// <summary>
/// Default <see cref="IPromptSanitizer"/> — pure string replacement,
/// stateless, safe as a singleton.
/// </summary>
public sealed class PromptSanitizer : IPromptSanitizer
{
    public SanitizationResult Sanitize(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyDictionary<string, string> sensitiveValuesByName)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(sensitiveValuesByName);

        if (messages.Count == 0 || sensitiveValuesByName.Count == 0)
        {
            return new SanitizationResult(messages, Array.Empty<string>());
        }

        // Filter out empty / whitespace values — substituting "" everywhere
        // would destroy the prompt. Order by descending value length so
        // longer matches beat shorter overlapping ones (e.g. a key whose
        // value is "secret-key-prefix" stays atomic instead of being
        // chopped by an earlier substitution of "secret-key").
        var pairs = sensitiveValuesByName
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .OrderByDescending(kv => kv.Value.Length)
            .ToArray();

        if (pairs.Length == 0)
        {
            return new SanitizationResult(messages, Array.Empty<string>());
        }

        var scrubbed   = new HashSet<string>(StringComparer.Ordinal);
        var newMessages = new List<ChatMessage>(messages.Count);

        foreach (var message in messages)
        {
            var text = message.Text;
            if (string.IsNullOrEmpty(text))
            {
                newMessages.Add(message);
                continue;
            }

            foreach (var (name, value) in pairs)
            {
                if (text.Contains(value, StringComparison.Ordinal))
                {
                    text = text.Replace(value, $"[REDACTED:{name}]", StringComparison.Ordinal);
                    scrubbed.Add(name);
                }
            }

            // Rebuild the ChatMessage with the sanitised text, preserving role.
            newMessages.Add(new ChatMessage(message.Role, text));
        }

        return new SanitizationResult(newMessages, scrubbed.ToArray());
    }
}

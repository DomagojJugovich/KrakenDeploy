using System.Security.Cryptography;
using System.Text;

namespace KrakenDeploy.Server.Data.Services.Ai.Curators;

/// <summary>Shared helpers for the step-config curators.</summary>
internal static class CuratorHelpers
{
    /// <summary>
    /// Truncates <paramref name="value"/> to <paramref name="max"/> chars,
    /// appending a "… (N chars total)" marker so the AI knows content was
    /// trimmed + can decide whether the full Config drill-down is worth it.
    /// Returns the value unchanged when it's already short.
    /// </summary>
    public static string Elide(string value, int max)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length <= max)
        {
            return value;
        }
        return value[..max] + $"… ({value.Length} chars total)";
    }

    /// <summary>
    /// SHA-256 hex (first 12 chars) of <paramref name="value"/>. Lets the AI
    /// tell whether a truncated script body changed between two deployments
    /// without us shipping the whole body twice.
    /// </summary>
    public static string ShortHash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(bytes)[..12];
    }

    /// <summary>
    /// Copies <paramref name="keys"/> from <paramref name="config"/> into the
    /// summary when present + non-empty. The summary key drops the long
    /// Octopus namespace prefix for readability (e.g.
    /// <c>Octopus.Action.WindowsService.ServiceName</c> → <c>serviceName</c>).
    /// </summary>
    public static void CopyIfPresent(
        IReadOnlyDictionary<string, string> config,
        IDictionary<string, string> summary,
        params (string ConfigKey, string SummaryKey)[] keys)
    {
        foreach (var (configKey, summaryKey) in keys)
        {
            if (config.TryGetValue(configKey, out var v) && !string.IsNullOrWhiteSpace(v))
            {
                summary[summaryKey] = v;
            }
        }
    }
}

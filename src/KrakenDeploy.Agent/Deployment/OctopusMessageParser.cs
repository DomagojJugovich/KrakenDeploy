using System.Text;
using System.Text.RegularExpressions;

namespace KrakenDeploy.Agent.Deployment;

/// <summary>
/// Parses the <c>##octopus[...]</c> stdout markers Octopus community step
/// templates emit to signal back to the runner. Compatible with the format
/// documented at <see href="https://octopus.com/docs/projects/custom-scripts/logging-messages-in-scripts"/>.
/// <para>
/// Supported markers (and what we do with them):
/// <list type="bullet">
///   <item><c>##octopus[setVariable name='base64' value='base64']</c> — captured as output variable.</item>
///   <item><c>##octopus[setOutputVariable name='base64' value='base64']</c> — alias of <c>setVariable</c>.</item>
///   <item><c>##octopus[createArtifact path='base64' name='base64']</c> — accepted; in-band marker is informational since the agent already scans the artifacts directory after each step.</item>
///   <item><c>##octopus[stdout-warning]</c> / <c>stdout-error</c> / <c>stdout-default</c> — switches the log level of subsequent lines until the next directive.</item>
///   <item><c>##octopus[progress percentage='25' message='base64']</c> — accepted; surfaced as an info log line.</item>
/// </list>
/// </para>
/// <para>
/// Names and values are base64-encoded UTF-8 strings — that's how Octopus
/// safely transports multi-line values and quotes through the stdout channel.
/// </para>
/// </summary>
public static class OctopusMessageParser
{
    private static readonly Regex MarkerRegex = new(
        @"^##octopus\[(?<command>[a-zA-Z-]+)(?<attrs>(?:\s+[a-zA-Z]+='[^']*')*)\]\s*$",
        RegexOptions.Compiled);

    private static readonly Regex AttrRegex = new(
        @"(?<name>[a-zA-Z]+)='(?<value>[^']*)'",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses a single stdout line. Returns <c>null</c> if the line is not an
    /// Octopus marker. Otherwise returns a typed message; the caller dispatches
    /// based on the concrete subtype.
    /// </summary>
    public static OctopusMessage? TryParse(string line)
    {
        if (string.IsNullOrEmpty(line) || !line.StartsWith("##octopus[", StringComparison.Ordinal))
        {
            return null;
        }

        var m = MarkerRegex.Match(line);
        if (!m.Success)
        {
            return null;
        }

        var command = m.Groups["command"].Value;
        var attrs = ParseAttrs(m.Groups["attrs"].Value);

        return command.ToLowerInvariant() switch
        {
            "setvariable" or "setoutputvariable" =>
                new SetVariableMessage(
                    Name: DecodeBase64(attrs.GetValueOrDefault("name") ?? ""),
                    Value: DecodeBase64(attrs.GetValueOrDefault("value") ?? "")),

            "createartifact" =>
                new CreateArtifactMessage(
                    Path: DecodeBase64(attrs.GetValueOrDefault("path") ?? ""),
                    Name: DecodeBase64(attrs.GetValueOrDefault("name") ?? "")),

            "stdout-warning" => new SetLogLevelMessage("warning"),
            "stdout-error"   => new SetLogLevelMessage("error"),
            "stdout-default" => new SetLogLevelMessage("info"),

            "progress" =>
                new ProgressMessage(
                    Percentage: int.TryParse(attrs.GetValueOrDefault("percentage"), out var p) ? p : 0,
                    Message: DecodeBase64(attrs.GetValueOrDefault("message") ?? "")),

            _ => new UnknownMessage(command, attrs),
        };
    }

    private static Dictionary<string, string> ParseAttrs(string raw)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return d;
        }
        foreach (Match m in AttrRegex.Matches(raw))
        {
            d[m.Groups["name"].Value] = m.Groups["value"].Value;
        }
        return d;
    }

    private static string DecodeBase64(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            // Octopus convention is base64; if a script emits a marker with a
            // raw value, accept it rather than dropping the directive.
            return value;
        }
    }
}

public abstract record OctopusMessage;
public sealed record SetVariableMessage(string Name, string Value) : OctopusMessage;
public sealed record CreateArtifactMessage(string Path, string Name) : OctopusMessage;
public sealed record SetLogLevelMessage(string Level) : OctopusMessage;
public sealed record ProgressMessage(int Percentage, string Message) : OctopusMessage;
public sealed record UnknownMessage(string Command, IReadOnlyDictionary<string, string> Attributes) : OctopusMessage;

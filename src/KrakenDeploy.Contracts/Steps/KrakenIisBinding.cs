using System.Globalization;

namespace KrakenDeploy.Contracts.Steps;

/// <summary>
/// One IIS site binding. Encoded as a pipe-delimited string in the step config:
/// <c>protocol|ipAddress|port|hostname|certThumbprint|certStore|sniRequired|sslFlags</c>.
/// </summary>
public sealed record KrakenIisBinding
{
    /// <summary><c>http</c> or <c>https</c> (case-insensitive).</summary>
    public required string Protocol { get; init; }

    /// <summary>IP address; <c>*</c> for all unassigned.</summary>
    public string IpAddress { get; init; } = "*";

    /// <summary>TCP port (e.g. 80, 443).</summary>
    public required int Port { get; init; }

    /// <summary>Optional host header / SNI hostname.</summary>
    public string Hostname { get; init; } = "";

    /// <summary>SHA-1 thumbprint of the SSL certificate (HTTPS only).</summary>
    public string CertThumbprint { get; init; } = "";

    /// <summary>Certificate store name (default: <c>My</c>) — HTTPS only.</summary>
    public string CertStore { get; init; } = "My";

    /// <summary>Whether SNI is required for this binding.</summary>
    public bool SniRequired { get; init; }

    /// <summary>
    /// SSL flags bitmask. 0 = none, 1 = SNI, 2 = central cert store, 3 = both.
    /// Used for the IIS <c>sslFlags</c> binding info attribute.
    /// </summary>
    public int SslFlags { get; init; }

    public bool IsHttps => Protocol.Equals("https", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Renders the binding in the IIS binding-info format:
    /// <c>ipAddress:port:hostname</c>.
    /// </summary>
    public string BindingInformation =>
        string.Create(CultureInfo.InvariantCulture, $"{IpAddress}:{Port}:{Hostname}");

    /// <summary>Parses a single pipe-delimited binding line.</summary>
    public static KrakenIisBinding Parse(string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);

        var parts = line.Split('|');
        if (parts.Length < 3)
        {
            throw new FormatException(
                $"Invalid IIS binding '{line}'. Expected at least 'protocol|ip|port'.");
        }

        var protocol = parts[0].Trim();
        var ip       = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1].Trim() : "*";
        if (!int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            throw new FormatException($"Invalid port in IIS binding '{line}'.");
        }

        var hostname    = Field(parts, 3);
        var thumbprint  = Field(parts, 4);
        var store       = Field(parts, 5, "My");
        var sniRequired = bool.TryParse(Field(parts, 6, "false"), out var sni) && sni;
        var sslFlags    = int.TryParse(Field(parts, 7, "0"),
                            NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : 0;

        return new KrakenIisBinding
        {
            Protocol       = protocol,
            IpAddress      = ip,
            Port           = port,
            Hostname       = hostname,
            CertThumbprint = thumbprint,
            CertStore      = store,
            SniRequired    = sniRequired,
            SslFlags       = sslFlags,
        };
    }

    /// <summary>Parses newline-separated binding lines.</summary>
    public static IReadOnlyList<KrakenIisBinding> ParseAll(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var result = new List<KrakenIisBinding>();
        foreach (var rawLine in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
            {
                continue;
            }

            result.Add(Parse(line));
        }
        return result;
    }

    private static string Field(string[] parts, int idx, string fallback = "")
        => idx < parts.Length && !string.IsNullOrWhiteSpace(parts[idx]) ? parts[idx].Trim() : fallback;
}

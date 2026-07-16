using Npgsql;

namespace KrakenDeploy.Server.Data;

/// <summary>
/// Helpers for shaping the Npgsql connection string. Pool sizing is a
/// connection-string keyword ("Maximum Pool Size"), NOT an EF Core option, so a
/// pool cap has to be applied here rather than in <c>UseNpgsql(...)</c>.
/// </summary>
public static class NpgsqlConnectionStrings
{
    /// <summary>
    /// Returns <paramref name="connectionString"/> with "Maximum Pool Size" set to
    /// <paramref name="maxPoolSize"/> — UNLESS the operator already specified a pool
    /// size in the connection string, in which case their value wins (explicit
    /// per-deployment tuning is authoritative). Idempotent for a given input.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxPoolSize"/> is not positive.</exception>
    public static string WithMaxPoolSize(string connectionString, int maxPoolSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPoolSize);

        // Respect an operator-supplied pool size. Detect it from the RAW string:
        // NpgsqlConnectionStringBuilder.ContainsKey returns true for every known
        // keyword (typed properties carry defaults), so it can't tell "set" from
        // "default". Normalising away spaces/underscores covers "Maximum Pool Size",
        // "Max Pool Size" and "MaxPoolSize" in any casing.
        if (HasPoolSizeKeyword(connectionString))
        {
            return connectionString;
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = maxPoolSize,
        };
        return builder.ConnectionString;
    }

    private static bool HasPoolSizeKeyword(string connectionString)
    {
        foreach (var part in connectionString.Split(
                     ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = part[..eq].Replace(" ", string.Empty, StringComparison.Ordinal)
                                .Replace("_", string.Empty, StringComparison.Ordinal);
            if (key.Equals("MaximumPoolSize", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("MaxPoolSize", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

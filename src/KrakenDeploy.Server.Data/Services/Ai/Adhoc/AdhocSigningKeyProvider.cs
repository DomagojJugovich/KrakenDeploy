using System.Security.Cryptography;
using KrakenDeploy.Contracts.Adhoc;
using Microsoft.Extensions.Configuration;

namespace KrakenDeploy.Server.Data.Services.Ai.Adhoc;

/// <summary>
/// M11.E.6 — singleton holder for the server's <c>Adhoc:SigningKey</c> RSA
/// private key. Loaded lazily on first use (so the server boots cleanly even
/// when adhoc isn't configured); cached for the process lifetime so signing
/// stays cheap.
/// <para>
/// Accepts inline PEM (multi-line, with <c>-----BEGIN</c> marker) or a path
/// to a <c>.pem</c> file — same convention the agent uses for
/// <c>Adhoc:TrustedPublicKey</c>.
/// </para>
/// <para>
/// Throws <see cref="AdhocFeatureUnavailableException"/> with
/// <see cref="AdhocFeatureUnavailableReason.SigningKeyMissing"/> when the
/// config slot is empty / malformed / unreadable, so the API layer can return
/// a clean "feature not configured" error rather than a generic 500. The
/// orchestrator catches and surfaces this on every approval attempt — the key
/// is the operator-visible gate, not a hidden startup failure.
/// </para>
/// </summary>
public sealed class AdhocSigningKeyProvider(IConfiguration config) : IDisposable
{
    private readonly object _gate = new();
    private RSA? _cached;
    private bool _disposed;

    /// <summary>
    /// Returns a borrowed reference to the cached private key. DO NOT dispose
    /// the returned RSA — its lifetime is the provider's. Throws when the key
    /// is not configured.
    /// </summary>
    public RSA GetPrivateKey()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var existing = _cached;
        if (existing is not null) { return existing; }

        lock (_gate)
        {
            if (_cached is not null) { return _cached; }

            var raw = config["Adhoc:SigningKey"];
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new AdhocFeatureUnavailableException(
                    AdhocFeatureUnavailableReason.SigningKeyMissing,
                    "Adhoc:SigningKey is not configured. Set it to an RSA private-key " +
                    "PEM (inline or as a path to a .pem file) — a separate key from " +
                    "StepPackages:SigningKey so the two surfaces can be rotated " +
                    "independently. Without it the server cannot sign approved " +
                    "ad-hoc scripts and the feature is unavailable.",
                    new InvalidOperationException("Adhoc:SigningKey missing"));
            }

            string pem;
            if (raw.Contains("-----BEGIN", StringComparison.Ordinal))
            {
                pem = raw;
            }
            else if (File.Exists(raw))
            {
                pem = File.ReadAllText(raw);
            }
            else
            {
                throw new AdhocFeatureUnavailableException(
                    AdhocFeatureUnavailableReason.SigningKeyMissing,
                    $"Adhoc:SigningKey is set but is neither inline PEM nor a path " +
                    $"to an existing file: '{raw}'.",
                    new InvalidOperationException("Adhoc:SigningKey unreadable"));
            }

            try
            {
                _cached = AdhocScriptSigner.ImportPrivateKeyFromPem(pem);
            }
            catch (Exception ex)
            {
                throw new AdhocFeatureUnavailableException(
                    AdhocFeatureUnavailableReason.SigningKeyMissing,
                    $"Adhoc:SigningKey failed to load as an RSA private key: {ex.Message}",
                    ex);
            }
            return _cached;
        }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        lock (_gate)
        {
            _cached?.Dispose();
            _cached = null;
            _disposed = true;
        }
    }
}

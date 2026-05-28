using System.Security.Cryptography;
using System.Text;

namespace KrakenDeploy.Contracts.Adhoc;

/// <summary>
/// M11.E.6 — RSA-SHA256 sign + verify for an approved ad-hoc script. The
/// server signs immediately after an operator approves an iteration; the agent
/// verifies the signature BEFORE executing the script (M11.E.7). A mismatched
/// signature is rejected loudly; the script never runs.
/// <para>
/// <strong>Separate key from step-package signing.</strong> Uses the
/// <c>Adhoc:SigningKey</c> config slot, NOT the <c>StepPackages</c> key.
/// Compromising one key must not compromise the other; rotation cadence and
/// custody can also differ (the ad-hoc key sits in the live server process,
/// the step-package key in the build/publish pipeline).
/// </para>
/// <para>
/// <strong>Bound to the session + iteration.</strong> The canonical signature
/// input is <c>"adhoc:v1\n{sessionId:N}\n{iterNumber}\n{scriptBytes}"</c>, so
/// a signature produced for session A iteration 2 will NOT validate when
/// presented as session A iteration 3 or session B. This kills replay across
/// sessions / turns even if an attacker captures a previously-signed payload.
/// </para>
/// </summary>
public static class AdhocScriptSigner
{
    /// <summary>
    /// Schema marker prepended to the canonical signature input. Bumping this
    /// when the binding shape changes lets old + new servers/agents reject
    /// each other's signatures rather than silently accepting them.
    /// </summary>
    private const string SchemaVersion = "adhoc:v1";

    /// <summary>
    /// Signs the given script + session/iteration binding. Returns the
    /// base64-encoded RSA-SHA256 signature. Caller persists the result on
    /// <c>AdhocIteration.ScriptSignature</c> and dispatches it alongside the
    /// script to the agent.
    /// </summary>
    public static string Sign(
        Guid sessionId, int iterNumber, string script, RSA privateKey)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(privateKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterNumber);

        var input = BuildCanonicalInput(sessionId, iterNumber, script);
        var sig   = privateKey.SignData(input, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Convert.ToBase64String(sig);
    }

    /// <summary>
    /// Verifies a signature against the script + session/iteration binding +
    /// trusted public key. Returns a <see cref="VerifyResult"/> with the exact
    /// failure reason so the agent can log loudly. Returns
    /// <see cref="VerifyResult.IsValid"/> = false on every error case, never
    /// throws on bad input (e.g. malformed base64, empty signature) — the
    /// agent's fail-closed gate is the single point of truth.
    /// </summary>
    public static VerifyResult Verify(
        Guid sessionId,
        int iterNumber,
        string script,
        string? signature,
        RSA publicKey)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(publicKey);

        if (iterNumber <= 0)
        {
            return new VerifyResult(false, "Iteration number must be positive.");
        }
        if (string.IsNullOrEmpty(signature))
        {
            return new VerifyResult(false, "Signature is empty.");
        }

        byte[] sigBytes;
        try
        {
            sigBytes = Convert.FromBase64String(signature);
        }
        catch (FormatException)
        {
            return new VerifyResult(false, "Signature is not valid base64.");
        }

        var input = BuildCanonicalInput(sessionId, iterNumber, script);

        bool ok;
        try
        {
            ok = publicKey.VerifyData(input, sigBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException ex)
        {
            return new VerifyResult(false, $"Signature verification threw: {ex.Message}");
        }

        return ok
            ? new VerifyResult(true, null)
            : new VerifyResult(false,
                "Signature does not validate against the trusted public key. The script, " +
                "the session/iteration binding, or the signature itself has been tampered " +
                "with — or the signature was produced by a different key than the one " +
                "configured at Adhoc:TrustedPublicKey.");
    }

    /// <summary>
    /// Reads a PEM-encoded RSA private key (PKCS#1 or PKCS#8). Caller disposes.
    /// </summary>
    public static RSA ImportPrivateKeyFromPem(string pem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pem);
        var rsa = RSA.Create();
        try { rsa.ImportFromPem(pem); }
        catch { rsa.Dispose(); throw; }
        return rsa;
    }

    /// <summary>Reads a PEM-encoded RSA public key. Caller disposes.</summary>
    public static RSA ImportPublicKeyFromPem(string pem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pem);
        var rsa = RSA.Create();
        try { rsa.ImportFromPem(pem); }
        catch { rsa.Dispose(); throw; }
        return rsa;
    }

    private static byte[] BuildCanonicalInput(Guid sessionId, int iterNumber, string script)
    {
        // Canonical: "adhoc:v1\n{guid}\n{iter}\n{script-utf8}".
        // The script is appended verbatim — no normalisation — so byte-level
        // tampering (extra whitespace, encoded characters) breaks the signature.
        var prefix = $"{SchemaVersion}\n{sessionId:N}\n{iterNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n";
        var prefixBytes = Encoding.UTF8.GetBytes(prefix);
        var scriptBytes = Encoding.UTF8.GetBytes(script);

        var input = new byte[prefixBytes.Length + scriptBytes.Length];
        Buffer.BlockCopy(prefixBytes, 0, input, 0, prefixBytes.Length);
        Buffer.BlockCopy(scriptBytes, 0, input, prefixBytes.Length, scriptBytes.Length);
        return input;
    }

    /// <summary>Outcome of <see cref="Verify"/>.</summary>
    public sealed record VerifyResult(bool IsValid, string? Reason);
}

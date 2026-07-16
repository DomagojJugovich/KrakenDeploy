namespace KrakenDeploy.Contracts;

/// <summary>
/// RESERVED wire shapes for the post-v1 agent enrollment + proof-of-possession
/// design (<c>docs/design-agent-enrollment-cert-auth.md</c>). NOTHING here is
/// served or consumed today — the shapes are frozen NOW (B6, the last
/// pre-freeze contract pass) so cert auth can ship later without retrofitting
/// a frozen contract. The real forward-compatibility lever is
/// <see cref="AgentContract.CurrentVersion"/>: shipping the PoP flow will bump
/// it, and pre-PoP agents get the explicit registration refusal.
/// <para>
/// Summary of the reserved design (see the doc for the full threat model):
/// the agent enrolls once with an enroll-only API key and self-generates a
/// non-exportable keypair; the server pins the RFC 7638 JWK SHA-256 thumbprint
/// as the target identity. The ongoing channel is authenticated per
/// connect/request with a DPoP-style proof over a fresh single-use server
/// nonce.
/// </para>
/// </summary>
public static class AgentEnrollmentReserved
{
    /// <summary>Reserved enroll endpoint route (API-key authenticated).</summary>
    public const string EnrollRoute = "/api/agents/enroll";

    /// <summary>Reserved nonce-challenge endpoint route: issues the fresh
    /// single-use, short-TTL nonce a (re)connect proof must sign.</summary>
    public const string ConnectNonceRoute = "/api/agents/connect-nonce";

    /// <summary>Reserved HTTP/gRPC metadata header carrying the DPoP-style
    /// proof JWT ({cnf, nonce, iat, exp, jti, aud, srv[, htm, htu]}).</summary>
    public const string PopProofHeader = "kraken-pop";
}

/// <summary>
/// RESERVED — body of <c>POST /api/agents/enroll</c> (Authorization: ApiKey).
/// The machine fields are non-authenticating HINTS for the operator approval
/// UI, never identity selectors; <paramref name="PublicKeyJwk"/> is the
/// agent-generated public key whose RFC 7638 thumbprint the server pins.
/// Unimplemented until the cert-auth milestone.
/// </summary>
public sealed record AgentEnrollmentRequest(
    string PublicKeyJwk,
    string MachineName,
    string OperatingSystem,
    string AgentVersion,
    string? MachineFingerprint,
    string? RequestedName);

/// <summary>
/// RESERVED — response of <c>POST /api/agents/enroll</c>. A production-bound
/// first enroll lands <c>PendingApproval</c> (TOFU pins require an operator);
/// the pinned thumbprint is echoed for the agent's own bookkeeping.
/// Unimplemented until the cert-auth milestone.
/// </summary>
public sealed record AgentEnrollmentResponse(
    Guid TargetId,
    string PinnedThumbprint,
    bool PendingApproval,
    string? Message);

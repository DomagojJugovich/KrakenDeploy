using System.Security.Cryptography;
using FluentAssertions;
using KrakenDeploy.Contracts.Adhoc;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Tests for M11.E.6 — <see cref="AdhocScriptSigner"/>. Pure crypto; fast.
/// </summary>
public sealed class AdhocScriptSignerTests
{
    private static (RSA Private, RSA Public) NewKeyPair()
    {
        var priv = RSA.Create(2048);
        var pub  = RSA.Create();
        pub.ImportSubjectPublicKeyInfo(priv.ExportSubjectPublicKeyInfo(), out _);
        return (priv, pub);
    }

    [Fact]
    public void Sign_then_verify_roundtrips_with_the_matching_public_key()
    {
        var (priv, pub) = NewKeyPair();
        var sessionId = Guid.NewGuid();
        const string script = "Get-Process | Select-Object Name, Id";

        var sig = AdhocScriptSigner.Sign(sessionId, iterNumber: 1, script, priv);
        var result = AdhocScriptSigner.Verify(sessionId, 1, script, sig, pub);

        result.IsValid.Should().BeTrue(result.Reason);
    }

    [Fact]
    public void Tampered_script_fails_verification()
    {
        var (priv, pub) = NewKeyPair();
        var sessionId = Guid.NewGuid();
        var sig = AdhocScriptSigner.Sign(sessionId, 1, "Get-Service", priv);

        var result = AdhocScriptSigner.Verify(sessionId, 1, "Get-Service ", sig, pub);

        result.IsValid.Should().BeFalse(
            "a trailing space changes the canonical bytes — the signature must not validate");
    }

    [Fact]
    public void Signature_does_not_replay_across_iterations()
    {
        // The binding includes IterNumber so a signed iter-2 payload can't be
        // re-presented as iter-3.
        var (priv, pub) = NewKeyPair();
        var sessionId = Guid.NewGuid();
        const string script = "Restart-Service w3svc";

        var sig = AdhocScriptSigner.Sign(sessionId, iterNumber: 2, script, priv);

        AdhocScriptSigner.Verify(sessionId, 2, script, sig, pub).IsValid.Should().BeTrue();
        AdhocScriptSigner.Verify(sessionId, 3, script, sig, pub).IsValid.Should().BeFalse(
            "iteration is part of the canonical input");
    }

    [Fact]
    public void Signature_does_not_replay_across_sessions()
    {
        // Different session id → different canonical input → invalid.
        var (priv, pub) = NewKeyPair();
        const string script = "Get-Date";

        var sig = AdhocScriptSigner.Sign(Guid.NewGuid(), 1, script, priv);
        var other = Guid.NewGuid();

        AdhocScriptSigner.Verify(other, 1, script, sig, pub).IsValid.Should().BeFalse(
            "session id is part of the canonical input");
    }

    [Fact]
    public void Different_keypair_fails_verification()
    {
        var (priv1, _) = NewKeyPair();
        var (_, pub2)  = NewKeyPair();
        var sessionId = Guid.NewGuid();

        var sig = AdhocScriptSigner.Sign(sessionId, 1, "Get-Date", priv1);

        AdhocScriptSigner.Verify(sessionId, 1, "Get-Date", sig, pub2).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("@@@not-base64@@@")]
    public void Empty_or_malformed_signature_fails_closed(string sig)
    {
        var (_, pub) = NewKeyPair();
        var result = AdhocScriptSigner.Verify(Guid.NewGuid(), 1, "Get-Date", sig, pub);
        result.IsValid.Should().BeFalse();
        result.Reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Verify_rejects_non_positive_iter_number()
    {
        var (_, pub) = NewKeyPair();
        AdhocScriptSigner.Verify(Guid.NewGuid(), 0, "x", "AAAA", pub)
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void PEM_roundtrip_works_for_private_and_public_keys()
    {
        using var src = RSA.Create(2048);
        var privPem = src.ExportRSAPrivateKeyPem();
        var pubPem  = src.ExportSubjectPublicKeyInfoPem();

        using var priv = AdhocScriptSigner.ImportPrivateKeyFromPem(privPem);
        using var pub  = AdhocScriptSigner.ImportPublicKeyFromPem(pubPem);

        var sessionId = Guid.NewGuid();
        var sig = AdhocScriptSigner.Sign(sessionId, 1, "Get-Date", priv);
        AdhocScriptSigner.Verify(sessionId, 1, "Get-Date", sig, pub).IsValid.Should().BeTrue();
    }
}

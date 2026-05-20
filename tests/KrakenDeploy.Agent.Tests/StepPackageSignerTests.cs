using System.Security.Cryptography;
using FluentAssertions;
using KrakenDeploy.Contracts.StepPackages;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// Round-trip tests for <see cref="StepPackageSigner"/> (Phase D-12).
/// Verifies the canonical recipe (manifest ++ executor SHA, RSA-SHA256
/// PKCS#1 v1.5) holds end-to-end and that every tamper vector — bad
/// signature bytes, wrong key, swapped manifest, swapped executor — is
/// rejected cleanly.
/// </summary>
public sealed class StepPackageSignerTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"kraken-signer-test-{Guid.NewGuid():N}");

    public StepPackageSignerTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Sign_then_Verify_round_trip_with_matching_key_pair_is_valid()
    {
        var (executorDll, manifest) = StagePackage();
        using var rsa = RSA.Create(2048);

        var signed = StepPackageSigner.Sign(manifest, executorDll, rsa);

        signed.Signature.Should().NotBeNullOrEmpty();
        signed.Signature.Should().NotBe(manifest.Signature);

        var verify = StepPackageSigner.Verify(signed, executorDll, rsa);
        verify.IsValid.Should().BeTrue(verify.Reason);
    }

    [Fact]
    public void Verify_rejects_when_signed_with_a_different_key()
    {
        var (executorDll, manifest) = StagePackage();
        using var signerKey   = RSA.Create(2048);
        using var imposterKey = RSA.Create(2048);

        var signed = StepPackageSigner.Sign(manifest, executorDll, signerKey);

        var verify = StepPackageSigner.Verify(signed, executorDll, imposterKey);
        verify.IsValid.Should().BeFalse();
        verify.Reason.Should().Contain("Signature does not validate");
    }

    [Fact]
    public void Verify_rejects_when_executor_dll_was_tampered_after_signing()
    {
        var (executorDll, manifest) = StagePackage();
        using var rsa = RSA.Create(2048);

        var signed = StepPackageSigner.Sign(manifest, executorDll, rsa);

        // Tamper: someone swaps the DLL's bytes after the signature was created.
        File.WriteAllBytes(executorDll, [0x4D, 0x5A, 0x99, 0x99]); // "MZ" header + garbage

        var verify = StepPackageSigner.Verify(signed, executorDll, rsa);
        verify.IsValid.Should().BeFalse(
            "the executor SHA is baked into the signature input; any byte change invalidates it");
    }

    [Fact]
    public void Verify_rejects_when_manifest_was_tampered_after_signing()
    {
        var (executorDll, manifest) = StagePackage();
        using var rsa = RSA.Create(2048);
        var signed = StepPackageSigner.Sign(manifest, executorDll, rsa);

        // Tamper: the manifest's StepTypes is rewritten — same signature carried over.
        var tampered = signed with { StepTypes = ["Hijacked.StepType"] };

        var verify = StepPackageSigner.Verify(tampered, executorDll, rsa);
        verify.IsValid.Should().BeFalse(
            "the manifest's metadata is part of the signature input; any field change invalidates it");
    }

    [Fact]
    public void Verify_rejects_an_unsigned_manifest_loudly()
    {
        var (executorDll, manifest) = StagePackage();
        using var rsa = RSA.Create(2048);

        // Manifest has no signature populated.
        var verify = StepPackageSigner.Verify(manifest, executorDll, rsa);
        verify.IsValid.Should().BeFalse();
        verify.Reason.Should().Contain("no signature");
    }

    [Fact]
    public void Verify_rejects_a_malformed_base64_signature()
    {
        var (executorDll, manifest) = StagePackage();
        using var rsa = RSA.Create(2048);

        var malformed = manifest with { Signature = "this is not base64 !!!!" };

        var verify = StepPackageSigner.Verify(malformed, executorDll, rsa);
        verify.IsValid.Should().BeFalse();
        verify.Reason.Should().Contain("base64");
    }

    [Fact]
    public void Verify_rejects_when_executor_dll_is_missing_from_disk()
    {
        var (executorDll, manifest) = StagePackage();
        using var rsa = RSA.Create(2048);
        var signed = StepPackageSigner.Sign(manifest, executorDll, rsa);

        File.Delete(executorDll);

        var verify = StepPackageSigner.Verify(signed, executorDll, rsa);
        verify.IsValid.Should().BeFalse();
        verify.Reason.Should().Contain("not found");
    }

    [Fact]
    public void Sign_with_a_public_only_key_fails()
    {
        var (executorDll, manifest) = StagePackage();
        using var rsa = RSA.Create(2048);
        var publicOnly = RSA.Create();
        publicOnly.ImportRSAPublicKey(rsa.ExportRSAPublicKey(), out _);

        // RSA.SignData throws when the key has no private parameters — that's
        // exactly the failure mode we want a tampered server to hit.
        var act = () => StepPackageSigner.Sign(manifest, executorDll, publicOnly);
        act.Should().Throw<CryptographicException>();

        publicOnly.Dispose();
    }

    [Fact]
    public void Public_key_PEM_round_trip_matches_the_signing_key()
    {
        var (executorDll, manifest) = StagePackage();
        using var signerKey = RSA.Create(2048);

        var signed = StepPackageSigner.Sign(manifest, executorDll, signerKey);

        // Export the signer's public key as PEM, re-import on the verifier
        // side. Mirrors the production flow where the agent or server loads
        // StepPackages:TrustedPublicKey from configuration.
        var pem = signerKey.ExportSubjectPublicKeyInfoPem();
        using var verifierKey = StepPackageSigner.ImportPublicKeyFromPem(pem);

        var verify = StepPackageSigner.Verify(signed, executorDll, verifierKey);
        verify.IsValid.Should().BeTrue(verify.Reason);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a tiny fake "executor" file on disk + a minimal manifest
    /// pointing at it. The bytes are arbitrary; only the SHA-256 of the
    /// file matters for the signature recipe.
    /// </summary>
    private (string executorDll, StepPackageManifest manifest) StagePackage()
    {
        var dll = Path.Combine(_tempDir, "Executor.dll");
        File.WriteAllBytes(dll, [.. Enumerable.Range(0, 256).Select(b => (byte)b)]);

        var manifest = new StepPackageManifest
        {
            Id               = "kraken.test",
            Version          = "1.0.0",
            DisplayName      = "Signer test",
            TargetFramework  = "net10.0",
            StepTypes        = ["Test.Step"],
            ExecutorAssembly = "Executor.dll",
            ExecutorTypeName = "Test.Handler",
            SignedBy         = "kraken-project",
        };
        return (dll, manifest);
    }
}

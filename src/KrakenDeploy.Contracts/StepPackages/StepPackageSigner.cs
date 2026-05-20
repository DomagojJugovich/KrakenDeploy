using System.Security.Cryptography;

namespace KrakenDeploy.Contracts.StepPackages;

/// <summary>
/// One-stop RSA-SHA256 sign + verify for <c>.kdeploy-step</c> manifests
/// (Phase D-12). Both the server upload path and the agent loader use
/// these methods so the signing recipe stays in one place; authoring
/// tools (CLI, MSBuild target, GitHub Actions) call the same surface.
/// <para>
/// Recipe:
/// <list type="bullet">
///   <item>Input bytes = <see cref="StepPackageManifestJson.CanonicalSignatureInput"/>(manifest, sha256(executor.dll)).</item>
///   <item>Signature = base64( RSA-SHA256-sign( input, project-private-key ) ).</item>
///   <item>Verification re-computes the input + runs RSA-SHA256-verify with the trusted public key.</item>
/// </list>
/// </para>
/// </summary>
public static class StepPackageSigner
{
    /// <summary>
    /// Signs the manifest. Returns the signed manifest (a copy with
    /// <see cref="StepPackageManifest.Signature"/> populated). The caller
    /// is expected to serialise + write the result back to the package zip.
    /// </summary>
    /// <param name="manifest">Manifest as authored, signature field can be anything (it's blanked first).</param>
    /// <param name="executorDllPath">Path to the <c>.dll</c> that <see cref="StepPackageManifest.ExecutorAssembly"/> names.</param>
    /// <param name="privateKey">RSA key with private parameters. Disposable lifetime is the caller's.</param>
    public static StepPackageManifest Sign(
        StepPackageManifest manifest, string executorDllPath, RSA privateKey)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(executorDllPath);
        ArgumentNullException.ThrowIfNull(privateKey);

        var executorSha = ComputeSha256(executorDllPath);
        var input  = StepPackageManifestJson.CanonicalSignatureInput(manifest, executorSha);
        var sigBytes = privateKey.SignData(input, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return manifest with { Signature = Convert.ToBase64String(sigBytes) };
    }

    /// <summary>
    /// Verifies the manifest's signature against the trusted public key.
    /// Returns a <see cref="VerifyResult"/> rather than a plain bool so
    /// callers can surface the exact failure reason to logs / the user.
    /// </summary>
    /// <param name="manifest">Manifest as deserialised from the package zip.</param>
    /// <param name="executorDllPath">Path to the <c>.dll</c> the manifest names.</param>
    /// <param name="publicKey">Trusted public key. RSA with public parameters only.</param>
    public static VerifyResult Verify(
        StepPackageManifest manifest, string executorDllPath, RSA publicKey)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(executorDllPath);
        ArgumentNullException.ThrowIfNull(publicKey);

        if (string.IsNullOrEmpty(manifest.Signature))
        {
            return new VerifyResult(false, "Manifest has no signature.");
        }

        if (!File.Exists(executorDllPath))
        {
            return new VerifyResult(false,
                $"Executor DLL '{executorDllPath}' was not found.");
        }

        byte[] sigBytes;
        try
        {
            sigBytes = Convert.FromBase64String(manifest.Signature);
        }
        catch (FormatException)
        {
            return new VerifyResult(false,
                "Manifest signature is not valid base64.");
        }

        var executorSha = ComputeSha256(executorDllPath);
        var input       = StepPackageManifestJson.CanonicalSignatureInput(manifest, executorSha);

        var ok = publicKey.VerifyData(
            input, sigBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return ok
            ? new VerifyResult(true, null)
            : new VerifyResult(false,
                "Signature does not validate against the trusted public key. " +
                "The manifest, the executor DLL, or the signature has been tampered with " +
                "— or this package was signed by a different key than the one configured.");
    }

    /// <summary>
    /// Reads a PEM-encoded RSA public key. Supports both the SubjectPublicKeyInfo
    /// envelope (<c>-----BEGIN PUBLIC KEY-----</c>) and the legacy RSA-only
    /// envelope (<c>-----BEGIN RSA PUBLIC KEY-----</c>) for compatibility with
    /// older openssl outputs.
    /// </summary>
    public static RSA ImportPublicKeyFromPem(string pem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pem);
        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
        return rsa;
    }

    /// <summary>
    /// Reads a PEM-encoded RSA private key (PKCS#1 or PKCS#8 — both
    /// dispatched by <see cref="RSA.ImportFromPem(ReadOnlySpan{char})"/>).
    /// Caller disposes.
    /// </summary>
    public static RSA ImportPrivateKeyFromPem(string pem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pem);
        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
        return rsa;
    }

    private static byte[] ComputeSha256(string path)
    {
        using var fs = File.OpenRead(path);
        return SHA256.HashData(fs);
    }

    /// <summary>Outcome of <see cref="Verify"/>.</summary>
    public sealed record VerifyResult(bool IsValid, string? Reason);
}

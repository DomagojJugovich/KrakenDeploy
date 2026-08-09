using System.IO.Compression;
using Amazon.Runtime;
using AwsS3UploadStepPackage;
using FluentAssertions;

namespace AwsS3UploadStepPackage.Tests;

/// <summary>
/// Tests for <see cref="AwsSdkS3Uploader.ResolveCredentials"/> — the
/// only non-trivial chunk of the production uploader. Everything else
/// (PutObjectRequest construction, client lifetime) is exercised by
/// the handler tests via the <c>IS3Uploader</c> abstraction.
/// </summary>
public sealed class AwsSdkS3UploaderResolveCredentialsTests
{
    [Fact]
    public void Both_keys_populated_yields_BasicAWSCredentials()
    {
        var creds = AwsSdkS3Uploader.ResolveCredentials("AKIAEXAMPLE", "secret-not-real");

        creds.Should().BeOfType<BasicAWSCredentials>();
        var immutable = creds!.GetCredentials();
        immutable.AccessKey.Should().Be("AKIAEXAMPLE");
        immutable.SecretKey.Should().Be("secret-not-real");
    }

    [Fact]
    public void Both_keys_blank_returns_null_for_default_chain()
    {
        AwsSdkS3Uploader.ResolveCredentials(null, null).Should().BeNull();
        AwsSdkS3Uploader.ResolveCredentials("", "").Should().BeNull();
        AwsSdkS3Uploader.ResolveCredentials("   ", "\t").Should().BeNull();
    }

    [Fact]
    public void Only_access_key_populated_throws()
    {
        var act = () => AwsSdkS3Uploader.ResolveCredentials("AKIAEXAMPLE", null);
        act.Should().Throw<ArgumentException>()
           .WithMessage("*both*blank*");
    }

    [Fact]
    public void Only_secret_populated_throws()
    {
        var act = () => AwsSdkS3Uploader.ResolveCredentials(null, "secret-not-real");
        act.Should().Throw<ArgumentException>()
           .WithMessage("*both*blank*");
    }
}

/// <summary>
/// Pins the contract that the produced <c>.kdeploy-step</c> archive
/// carries the AWS SDK runtime DLLs. AWSSDK isn't in the agent host,
/// so the agent's <c>AssemblyDependencyResolver</c> can only resolve
/// <c>Amazon.S3.AmazonS3Client</c> if it ships inside the archive's
/// <c>executor/</c> directory.
/// </summary>
public sealed class AwsS3UploadArchiveTests
{
    [Fact]
    public void Built_archive_bundles_AWSSDK_S3_and_Core_runtime_DLLs()
    {
        var archivePath = FindBuiltArchive();
        archivePath.Should().NotBeNull(
            "the sample's pack target must produce " +
            "kraken.steps.aws-s3-upload-<version>.kdeploy-step");

        using var fs  = File.OpenRead(archivePath!);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        zip.GetEntry("executor/AwsS3UploadStepPackage.dll").Should().NotBeNull();
        zip.GetEntry("executor/AWSSDK.S3.dll").Should().NotBeNull(
            "AWSSDK.S3 must be inside the archive — the agent host does NOT " +
            "reference it, so the ALC delegation fallback can't save us " +
            "if it's missing");
        zip.GetEntry("executor/AWSSDK.Core.dll").Should().NotBeNull(
            "AWSSDK.Core is the SDK's runtime spine; missing it would " +
            "TypeLoadException on AmazonS3Client construction");
    }

    private static string? FindBuiltArchive()
    {
        var here = AppContext.BaseDirectory;
        // tests/AwsS3UploadStepPackage.Tests/bin/Debug/net10.0/
        //   → up five → solution root
        //   → examples/AwsS3UploadStepPackage/bin/Debug/net10.0/*.kdeploy-step
        var binRoot = Path.GetFullPath(Path.Combine(
            here, "..", "..", "..", "..", "..",
            "examples", "AwsS3UploadStepPackage", "bin"));
        // Configuration-agnostic: CI builds Release, local builds Debug — locate
        // the packed archive under bin/<Config>/<tfm>/ wherever it landed.
        return Directory.Exists(binRoot)
            ? Directory.EnumerateFiles(binRoot, "kraken.steps.aws-s3-upload-*.kdeploy-step",
                SearchOption.AllDirectories)
                .OrderByDescending(p => Version.Parse(ArchiveVersion(p)))
                .FirstOrDefault()
            : null;
    }

    // "<id>-<version>.kdeploy-step" -> "<version>" (the id itself may contain dashes).
    private static string ArchiveVersion(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        return stem[(stem.LastIndexOf('-') + 1)..];
    }
}

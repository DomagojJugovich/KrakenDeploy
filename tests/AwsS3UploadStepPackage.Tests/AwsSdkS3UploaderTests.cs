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

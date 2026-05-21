using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace AwsS3UploadStepPackage;

/// <summary>
/// Production <see cref="IS3Uploader"/> wrapping <c>Amazon.S3.AmazonS3Client</c>
/// from <c>AWSSDK.S3</c> v4. Holds a single client for the lifetime of the
/// step execution and disposes it via <see cref="DisposeAsync"/>.
/// <para>
/// Credential resolution (see <see cref="ResolveCredentials"/>):
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       Both <see cref="S3UploadConfig.AccessKeyId"/> + <see cref="S3UploadConfig.SecretAccessKey"/>
///       populated → <c>BasicAWSCredentials</c>. Typical when the
///       deployment runs from an on-prem agent that doesn't have an IAM
///       role and authors bind the keys to a sensitive Kraken variable.
///     </description>
///   </item>
///   <item>
///     <description>
///       Both blank → SDK's default credential chain: environment vars
///       (<c>AWS_ACCESS_KEY_ID</c> / <c>AWS_SECRET_ACCESS_KEY</c>), then
///       the shared credentials file, then the instance metadata service
///       (EC2 / ECS / EKS IAM role). Right choice for agents running on
///       AWS infrastructure.
///     </description>
///   </item>
///   <item>
///     <description>
///       Only one of the two populated → hard config error, surfaced by
///       <see cref="AwsS3UploadStepHandler"/> as a "config invalid" log
///       line. The uploader itself never reaches this case.
///     </description>
///   </item>
/// </list>
/// </summary>
public sealed class AwsSdkS3Uploader : IS3Uploader
{
    private readonly IAmazonS3 _client;

    /// <summary>
    /// Production constructor. Builds an <c>AmazonS3Client</c> from the
    /// resolved credentials + region endpoint.
    /// </summary>
    public AwsSdkS3Uploader(S3UploadConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var endpoint    = RegionEndpoint.GetBySystemName(config.Region);
        var credentials = ResolveCredentials(config.AccessKeyId, config.SecretAccessKey);

        _client = credentials is null
            ? new AmazonS3Client(endpoint)
            : new AmazonS3Client(credentials, endpoint);
    }

    /// <summary>
    /// Test seam — lets unit tests inject a hand-rolled <c>IAmazonS3</c>
    /// stub without going through the credential / region wiring.
    /// </summary>
    internal AwsSdkS3Uploader(IAmazonS3 client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>
    /// Resolves the credentials the <c>AmazonS3Client</c> should use.
    /// Public-static so tests can pin down the contract: both keys present →
    /// <c>BasicAWSCredentials</c>; both blank → <c>null</c> (i.e. use the
    /// SDK's default chain); exactly one populated → <c>ArgumentException</c>.
    /// </summary>
    public static AWSCredentials? ResolveCredentials(string? accessKeyId, string? secretAccessKey)
    {
        var hasId     = !string.IsNullOrWhiteSpace(accessKeyId);
        var hasSecret = !string.IsNullOrWhiteSpace(secretAccessKey);

        if (hasId && hasSecret)
        {
            return new BasicAWSCredentials(accessKeyId, secretAccessKey);
        }
        if (!hasId && !hasSecret)
        {
            return null;
        }
        throw new ArgumentException(
            "AccessKeyId and SecretAccessKey must be either both provided or both blank. " +
            "Set both to bind explicit credentials, or leave both blank to use the AWS SDK's " +
            "default credential chain (env vars, shared file, EC2 / ECS instance role).",
            hasId ? nameof(secretAccessKey) : nameof(accessKeyId));
    }

    public async Task<long> PutObjectAsync(
        string            bucket,
        string            objectKey,
        Stream            content,
        string?           cannedAcl,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentNullException.ThrowIfNull(content);

        var request = new PutObjectRequest
        {
            BucketName       = bucket,
            Key              = objectKey,
            InputStream      = content,
            AutoCloseStream  = false, // caller (the handler's `await using`) owns the stream
        };
        if (!string.IsNullOrWhiteSpace(cannedAcl))
        {
            request.CannedACL = new S3CannedACL(cannedAcl);
        }

        // PutObjectAsync (v4) throws on non-2xx — the handler catches and
        // either aborts or accumulates depending on ContinueOnError.
        await _client.PutObjectAsync(request, ct).ConfigureAwait(false);

        // Stream.Length isn't always available (e.g. a non-seekable network
        // stream). For file-backed uploads from the deployment package it
        // is — the handler always hands us a FileStream — so this is safe
        // here. A more defensive impl would capture bytes-uploaded via a
        // streaming wrapper.
        return content.CanSeek ? content.Length : -1;
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}

namespace AwsS3UploadStepPackage;

/// <summary>
/// Storage-facing surface the step handler talks to. Kept narrow on purpose
/// so handler tests can drop in a fake without depending on the real AWS SDK.
/// <para>
/// The default implementation is <see cref="NotImplementedAwsS3Uploader"/>;
/// step-package authors are expected to swap it for an <c>AWSSDK.S3</c>-backed
/// implementation before shipping (see <c>README.md</c> in this folder).
/// </para>
/// </summary>
public interface IS3Uploader
{
    /// <summary>
    /// Uploads <paramref name="content"/> to <c>s3://{bucket}/{objectKey}</c>.
    /// Implementations should respect <paramref name="ct"/>.
    /// </summary>
    /// <returns>The number of bytes the implementation pushed to S3.</returns>
    Task<long> PutObjectAsync(
        string                  bucket,
        string                  objectKey,
        Stream                  content,
        string?                 cannedAcl,
        CancellationToken       ct);
}

/// <summary>
/// Default uploader that fails loudly when the sample is built without
/// wiring a real AWS SDK implementation. Production authors replace this
/// with a thin wrapper around <c>Amazon.S3.AmazonS3Client.PutObjectAsync</c>
/// — see the README in this folder for a 20-line snippet.
/// </summary>
internal sealed class NotImplementedAwsS3Uploader : IS3Uploader
{
    public Task<long> PutObjectAsync(
        string bucket, string objectKey, Stream content,
        string? cannedAcl, CancellationToken ct)
        => throw new NotImplementedException(
            "AwsS3UploadStepPackage is a sample. Swap NotImplementedAwsS3Uploader " +
            "for an AWSSDK.S3-backed impl before shipping — see README.md.");
}

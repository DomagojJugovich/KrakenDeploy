namespace AwsS3UploadStepPackage;

/// <summary>
/// Storage-facing surface the step handler talks to. Kept narrow on purpose
/// so handler tests can drop in a fake without depending on the real AWS SDK.
/// <para>
/// The production implementation is <see cref="AwsSdkS3Uploader"/> — uses
/// <c>Amazon.S3.AmazonS3Client</c>. Tests inject a fake via the handler's
/// internal ctor that takes a <c>Func&lt;S3UploadConfig, IS3Uploader&gt;</c>.
/// </para>
/// </summary>
public interface IS3Uploader : IAsyncDisposable
{
    /// <summary>
    /// Uploads <paramref name="content"/> to <c>s3://{bucket}/{objectKey}</c>.
    /// Implementations must respect <paramref name="ct"/> and leave
    /// <paramref name="content"/> open (the caller owns the stream's lifetime).
    /// </summary>
    /// <returns>The number of bytes the implementation pushed to S3.</returns>
    Task<long> PutObjectAsync(
        string                  bucket,
        string                  objectKey,
        Stream                  content,
        string?                 cannedAcl,
        CancellationToken       ct);
}

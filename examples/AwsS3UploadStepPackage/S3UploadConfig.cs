namespace AwsS3UploadStepPackage;

/// <summary>
/// Parsed + validated step configuration for an <c>Kraken.Steps.AwsS3Upload</c>
/// step. Built from the per-step <c>Config</c> dictionary; see
/// <see cref="OctopusS3ConfigKeys"/> for the wire-format key names.
/// </summary>
/// <remarks>
/// Credentials are carried here so the production
/// <see cref="AwsSdkS3Uploader"/> can decide between explicit keys (when
/// both <see cref="AccessKeyId"/> + <see cref="SecretAccessKey"/> are
/// populated) and the AWS SDK's default credential chain (when both are
/// blank — typical on EC2 / ECS / EKS where an IAM role is attached).
/// Asymmetric population (only one of the two) is a hard config error.
/// </remarks>
public sealed record S3UploadConfig
{
    public required string  BucketName       { get; init; }
    public required string  Region           { get; init; }
    public required string  ObjectKeyPrefix  { get; init; }
    public required string  FileGlob         { get; init; }
    public          string? AccessKeyId      { get; init; }
    public          string? SecretAccessKey  { get; init; }
    public          string? CannedAcl        { get; init; }
    public          bool    ContinueOnError  { get; init; }

    /// <summary>
    /// <c>true</c> when both credential fields are populated — use a
    /// <c>BasicAWSCredentials</c>. <c>false</c> when both are blank —
    /// fall back to the SDK's default chain.
    /// </summary>
    public bool HasExplicitCredentials
        => !string.IsNullOrWhiteSpace(AccessKeyId)
        && !string.IsNullOrWhiteSpace(SecretAccessKey);
}

/// <summary>
/// Wire-format key names used on <c>DeploymentStepPlan.Config</c>. The
/// prefix <c>Kraken.AwsS3.*</c> deliberately mirrors Octopus's
/// <c>Octopus.Action.*</c> namespacing convention so that a Kraken process
/// listing reads naturally next to other step types.
/// <para>
/// Sensitive values (<see cref="AccessKeyId"/>, <see cref="SecretAccessKey"/>)
/// must come in via Octostache-resolved variables — <strong>never</strong>
/// hand-typed into the step config in plain text. The UI schema enforces
/// this with the <c>variable</c> widget + <c>sensitive: true</c>.
/// </para>
/// </summary>
public static class OctopusS3ConfigKeys
{
    public const string BucketName       = "Kraken.AwsS3.BucketName";
    public const string Region           = "Kraken.AwsS3.Region";
    public const string ObjectKeyPrefix  = "Kraken.AwsS3.ObjectKeyPrefix";
    public const string FileGlob         = "Kraken.AwsS3.FileGlob";
    public const string AccessKeyId      = "Kraken.AwsS3.AccessKeyId";
    public const string SecretAccessKey  = "Kraken.AwsS3.SecretAccessKey";
    public const string CannedAcl        = "Kraken.AwsS3.CannedAcl";
    public const string ContinueOnError  = "Kraken.AwsS3.ContinueOnError";
}

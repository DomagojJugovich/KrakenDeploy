using System.Globalization;
using System.Text;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Steps.Common;
using Octostache;

namespace KrakenDeploy.Steps.Aws;

public static class AwsConfigKeys
{
    private const string Prefix = "Octopus.Action.Aws.";

    public const string AccessKeyId = Prefix + "AccessKeyId";
    public const string SecretAccessKey = Prefix + "SecretAccessKey";
    public const string SessionToken = Prefix + "SessionToken";
    public const string Region = Prefix + "Region";

    public const string BucketName = Prefix + "BucketName";
    public const string TargetKeyPrefix = Prefix + "TargetKeyPrefix";
    public const string FileGlob = Prefix + "FileGlob";
    public const string CannedAcl = Prefix + "CannedAcl";
    public const string StorageClass = Prefix + "StorageClass";

    public const string StackName = Prefix + "StackName";
    public const string TemplateSource = Prefix + "TemplateSource";
    public const string TemplateBody = Prefix + "TemplateBody";
    public const string TemplateFile = Prefix + "TemplateFile";
    public const string TemplateParameters = Prefix + "TemplateParameters";
    public const string Capabilities = Prefix + "Capabilities";
    public const string ChangeSetName = Prefix + "ChangeSetName";
    public const string ChangeSetType = Prefix + "ChangeSetType";
    public const string WaitForCompletion = Prefix + "WaitForCompletion";
    public const string TerminationProtection = Prefix + "TerminationProtection";

    public const string ClusterName = Prefix + "ClusterName";
    public const string ServiceName = Prefix + "ServiceName";
    public const string TaskDefinition = Prefix + "TaskDefinition";
    public const string DesiredCount = Prefix + "DesiredCount";
    public const string ForceNewDeployment = Prefix + "ForceNewDeployment";

    public const string ScriptBody = Prefix + "ScriptBody";
    public const string ScriptSyntax = Prefix + "ScriptSyntax";
}

public sealed class AwsStepHandler : IStepHandler
{
    private static readonly string[] _handledTypes =
    [
        "Octopus.AwsUploadS3",
        "Octopus.AwsCreateS3",
        "Octopus.AwsRunCloudFormation",
        "Octopus.AwsApplyCloudFormationChangeSet",
        "Octopus.AwsDeleteCloudFormation",
        "aws-ecs",
        "aws-ecs-update-service",
        "Octopus.AwsRunScript",
    ];

    public bool CanHandle(string stepType)
        => _handledTypes.Any(t => t.Equals(stepType, StringComparison.OrdinalIgnoreCase));

    public bool RequiresPackage => false;

    public async Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct)
    {
        var creds = ResolveCredentials(context);
        var region = Get(context, AwsConfigKeys.Region);

        return context.Step.StepType.ToLowerInvariant() switch
        {
            "octopus.awsuploads3" => await HandleUploadS3Async(context, creds, region, ct).ConfigureAwait(false),
            "octopus.awscreates3" => await HandleCreateS3Async(context, creds, region, ct).ConfigureAwait(false),
            "octopus.awsruncloudformation" => await HandleRunCloudFormationAsync(context, creds, region, ct).ConfigureAwait(false),
            "octopus.awsapplycloudformationchangeset" => await HandleApplyChangeSetAsync(context, creds, region, ct).ConfigureAwait(false),
            "octopus.awsdeletecloudformation" => await HandleDeleteCloudFormationAsync(context, creds, region, ct).ConfigureAwait(false),
            "aws-ecs" => await HandleEcsDeployAsync(context, creds, region, ct).ConfigureAwait(false),
            "aws-ecs-update-service" => await HandleEcsUpdateServiceAsync(context, creds, region, ct).ConfigureAwait(false),
            "octopus.awsrunscript" => await HandleRunScriptAsync(context, creds, region, ct).ConfigureAwait(false),
            _ => false,
        };
    }

    private static async Task<bool> HandleUploadS3Async(
        StepHandlerContext context, AwsCredentials? creds, string? region, CancellationToken ct)
    {
        var bucket = Get(context, AwsConfigKeys.BucketName);
        if (string.IsNullOrWhiteSpace(bucket))
        {
            await context.LogAsync("error", "BucketName is required for AwsUploadS3.").ConfigureAwait(false);
            return false;
        }

        var prefix = Get(context, AwsConfigKeys.TargetKeyPrefix) ?? "";
        var glob = Get(context, AwsConfigKeys.FileGlob) ?? "**/*";
        var acl = Get(context, AwsConfigKeys.CannedAcl);
        var storageClass = Get(context, AwsConfigKeys.StorageClass);

        var searchDir = string.IsNullOrEmpty(context.ExtractDir) ? "." : context.ExtractDir;
        var files = CollectFiles(searchDir, glob);
        if (files.Count == 0)
        {
            await context.LogAsync("error",
                string.Create(CultureInfo.InvariantCulture, $"No files matched '{glob}' in {searchDir}."))
                .ConfigureAwait(false);
            return false;
        }

        await context.LogAsync("info",
            string.Create(CultureInfo.InvariantCulture, $"Uploading {files.Count} file(s) to s3://{bucket}/{prefix}..."))
            .ConfigureAwait(false);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(searchDir, file).Replace('\\', '/');
            var key = string.IsNullOrEmpty(prefix) ? relative : $"{prefix.TrimEnd('/')}/{relative}";

            var sb = new StringBuilder("s3 cp");
            sb.Append(CultureInfo.InvariantCulture, $" \"{file}\"");
            sb.Append(CultureInfo.InvariantCulture, $" \"s3://{bucket}/{key}\"");

            if (!string.IsNullOrWhiteSpace(acl))
            {
                sb.Append(CultureInfo.InvariantCulture, $" --acl {acl}");
            }

            if (!string.IsNullOrWhiteSpace(storageClass))
            {
                sb.Append(CultureInfo.InvariantCulture, $" --storage-class {storageClass}");
            }

            var ok = await AwsCliRunner.RunAsync(sb.ToString(), ".", context.LogAsync, ct, creds, region)
                .ConfigureAwait(false);
            if (!ok)
            {
                await context.LogAsync("error",
                    string.Create(CultureInfo.InvariantCulture, $"Failed to upload {relative}."))
                    .ConfigureAwait(false);
                return false;
            }
        }

        await context.LogAsync("info",
            string.Create(CultureInfo.InvariantCulture, $"Successfully uploaded {files.Count} file(s)."))
            .ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> HandleCreateS3Async(
        StepHandlerContext context, AwsCredentials? creds, string? region, CancellationToken ct)
    {
        var bucket = Get(context, AwsConfigKeys.BucketName);
        if (string.IsNullOrWhiteSpace(bucket))
        {
            await context.LogAsync("error", "BucketName is required for AwsCreateS3.").ConfigureAwait(false);
            return false;
        }

        var args = $"s3api create-bucket --bucket {bucket}";
        if (!string.IsNullOrWhiteSpace(region) && !region.Equals("us-east-1", StringComparison.OrdinalIgnoreCase))
        {
            args += string.Create(CultureInfo.InvariantCulture, $" --create-bucket-configuration LocationConstraint={region}");
        }

        await context.LogAsync("info",
            string.Create(CultureInfo.InvariantCulture, $"Creating S3 bucket '{bucket}'..."))
            .ConfigureAwait(false);
        return await AwsCliRunner.RunAsync(args, ".", context.LogAsync, ct, creds, region).ConfigureAwait(false);
    }

    private static async Task<bool> HandleRunCloudFormationAsync(
        StepHandlerContext context, AwsCredentials? creds, string? region, CancellationToken ct)
    {
        var stackName = Get(context, AwsConfigKeys.StackName);
        if (string.IsNullOrWhiteSpace(stackName))
        {
            await context.LogAsync("error", "StackName is required for AwsRunCloudFormation.").ConfigureAwait(false);
            return false;
        }

        var templateArgs = await BuildTemplateArgsAsync(context, ct).ConfigureAwait(false);
        if (templateArgs is null)
        {
            return false;
        }

        var changeSetName = Get(context, AwsConfigKeys.ChangeSetName) ?? $"kraken-{Guid.NewGuid():N}";
        var changeSetType = Get(context, AwsConfigKeys.ChangeSetType) ?? "CREATE";
        var waitForCompletion = Get(context, AwsConfigKeys.WaitForCompletion);

        var sb = new StringBuilder("cloudformation create-change-set");
        sb.Append(CultureInfo.InvariantCulture, $" --stack-name {stackName}");
        sb.Append(CultureInfo.InvariantCulture, $" --change-set-name {changeSetName}");
        sb.Append(CultureInfo.InvariantCulture, $" --change-set-type {changeSetType}");
        sb.Append(CultureInfo.InvariantCulture, $" {templateArgs}");

        var parameters = Get(context, AwsConfigKeys.TemplateParameters);
        if (!string.IsNullOrWhiteSpace(parameters))
        {
            sb.Append(CultureInfo.InvariantCulture, $" --parameters {BuildCfnParameters(parameters)}");
        }

        var capabilities = Get(context, AwsConfigKeys.Capabilities);
        if (!string.IsNullOrWhiteSpace(capabilities))
        {
            sb.Append(CultureInfo.InvariantCulture, $" --capabilities {capabilities}");
        }

        await context.LogAsync("info",
            string.Create(CultureInfo.InvariantCulture, $"Creating CloudFormation change set '{changeSetName}' for stack '{stackName}'..."))
            .ConfigureAwait(false);

        var ok = await AwsCliRunner.RunAsync(sb.ToString(), ".", context.LogAsync, ct, creds, region)
            .ConfigureAwait(false);
        if (!ok)
        {
            return false;
        }

        await context.LogAsync("info", "Waiting for change set creation...").ConfigureAwait(false);
        ok = await AwsCliRunner.RunAsync(
            $"cloudformation wait change-set-create-complete --stack-name {stackName} --change-set-name {changeSetName}",
            ".", context.LogAsync, ct, creds, region).ConfigureAwait(false);
        if (!ok)
        {
            await context.LogAsync("warning",
                "Change set wait returned non-zero (may be NO_CHANGES). Attempting execute anyway.")
                .ConfigureAwait(false);
        }

        await context.LogAsync("info", "Executing change set...").ConfigureAwait(false);
        ok = await AwsCliRunner.RunAsync(
            $"cloudformation execute-change-set --stack-name {stackName} --change-set-name {changeSetName}",
            ".", context.LogAsync, ct, creds, region).ConfigureAwait(false);
        if (!ok)
        {
            return false;
        }

        if (ParseBool(waitForCompletion) || waitForCompletion is null)
        {
            await context.LogAsync("info", "Waiting for stack operation to complete...").ConfigureAwait(false);
            ok = await AwsCliRunner.RunAsync(
                $"cloudformation wait stack-update-complete --stack-name {stackName}",
                ".", context.LogAsync, ct, creds, region).ConfigureAwait(false);
            if (!ok)
            {
                ok = await AwsCliRunner.RunAsync(
                    $"cloudformation wait stack-create-complete --stack-name {stackName}",
                    ".", context.LogAsync, ct, creds, region).ConfigureAwait(false);
            }
        }

        return ok;
    }

    private static async Task<bool> HandleApplyChangeSetAsync(
        StepHandlerContext context, AwsCredentials? creds, string? region, CancellationToken ct)
    {
        var stackName = Get(context, AwsConfigKeys.StackName);
        var changeSetName = Get(context, AwsConfigKeys.ChangeSetName);

        if (string.IsNullOrWhiteSpace(stackName) || string.IsNullOrWhiteSpace(changeSetName))
        {
            await context.LogAsync("error",
                "StackName and ChangeSetName are required for AwsApplyCloudFormationChangeSet.")
                .ConfigureAwait(false);
            return false;
        }

        await context.LogAsync("info",
            string.Create(CultureInfo.InvariantCulture, $"Executing change set '{changeSetName}' on stack '{stackName}'..."))
            .ConfigureAwait(false);

        var ok = await AwsCliRunner.RunAsync(
            $"cloudformation execute-change-set --stack-name {stackName} --change-set-name {changeSetName}",
            ".", context.LogAsync, ct, creds, region).ConfigureAwait(false);
        if (!ok)
        {
            return false;
        }

        await context.LogAsync("info", "Waiting for stack update...").ConfigureAwait(false);
        return await AwsCliRunner.RunAsync(
            $"cloudformation wait stack-update-complete --stack-name {stackName}",
            ".", context.LogAsync, ct, creds, region).ConfigureAwait(false);
    }

    private static async Task<bool> HandleDeleteCloudFormationAsync(
        StepHandlerContext context, AwsCredentials? creds, string? region, CancellationToken ct)
    {
        var stackName = Get(context, AwsConfigKeys.StackName);
        if (string.IsNullOrWhiteSpace(stackName))
        {
            await context.LogAsync("error", "StackName is required for AwsDeleteCloudFormation.").ConfigureAwait(false);
            return false;
        }

        await context.LogAsync("info",
            string.Create(CultureInfo.InvariantCulture, $"Deleting CloudFormation stack '{stackName}'..."))
            .ConfigureAwait(false);

        var ok = await AwsCliRunner.RunAsync(
            $"cloudformation delete-stack --stack-name {stackName}",
            ".", context.LogAsync, ct, creds, region).ConfigureAwait(false);
        if (!ok)
        {
            return false;
        }

        await context.LogAsync("info", "Waiting for stack deletion...").ConfigureAwait(false);
        return await AwsCliRunner.RunAsync(
            $"cloudformation wait stack-delete-complete --stack-name {stackName}",
            ".", context.LogAsync, ct, creds, region).ConfigureAwait(false);
    }

    private static async Task<bool> HandleEcsDeployAsync(
        StepHandlerContext context, AwsCredentials? creds, string? region, CancellationToken ct)
    {
        var cluster = Get(context, AwsConfigKeys.ClusterName);
        var service = Get(context, AwsConfigKeys.ServiceName);
        var taskDef = Get(context, AwsConfigKeys.TaskDefinition);

        if (string.IsNullOrWhiteSpace(cluster) || string.IsNullOrWhiteSpace(service))
        {
            await context.LogAsync("error",
                "ClusterName and ServiceName are required for aws-ecs.")
                .ConfigureAwait(false);
            return false;
        }

        var sb = new StringBuilder("ecs update-service");
        sb.Append(CultureInfo.InvariantCulture, $" --cluster {cluster}");
        sb.Append(CultureInfo.InvariantCulture, $" --service {service}");

        if (!string.IsNullOrWhiteSpace(taskDef))
        {
            sb.Append(CultureInfo.InvariantCulture, $" --task-definition {taskDef}");
        }

        var desiredCount = Get(context, AwsConfigKeys.DesiredCount);
        if (!string.IsNullOrWhiteSpace(desiredCount))
        {
            sb.Append(CultureInfo.InvariantCulture, $" --desired-count {desiredCount}");
        }

        if (ParseBool(Get(context, AwsConfigKeys.ForceNewDeployment)))
        {
            sb.Append(" --force-new-deployment");
        }

        await context.LogAsync("info",
            string.Create(CultureInfo.InvariantCulture, $"Updating ECS service '{service}' in cluster '{cluster}'..."))
            .ConfigureAwait(false);
        return await AwsCliRunner.RunAsync(sb.ToString(), ".", context.LogAsync, ct, creds, region)
            .ConfigureAwait(false);
    }

    private static async Task<bool> HandleEcsUpdateServiceAsync(
        StepHandlerContext context, AwsCredentials? creds, string? region, CancellationToken ct)
        => await HandleEcsDeployAsync(context, creds, region, ct).ConfigureAwait(false);

    private static async Task<bool> HandleRunScriptAsync(
        StepHandlerContext context, AwsCredentials? creds, string? region, CancellationToken ct)
    {
        var scriptBody = Get(context, AwsConfigKeys.ScriptBody);
        if (string.IsNullOrWhiteSpace(scriptBody))
        {
            await context.LogAsync("error", "ScriptBody is required for AwsRunScript.").ConfigureAwait(false);
            return false;
        }

        var syntax = Get(context, AwsConfigKeys.ScriptSyntax) ?? "Bash";
        var resolved = ResolveVariables(scriptBody, context.Plan.Variables);

        var envVars = new Dictionary<string, string>();
        if (creds is not null)
        {
            envVars["AWS_ACCESS_KEY_ID"] = creds.AccessKeyId;
            envVars["AWS_SECRET_ACCESS_KEY"] = creds.SecretAccessKey;
            if (!string.IsNullOrEmpty(creds.SessionToken))
            {
                envVars["AWS_SESSION_TOKEN"] = creds.SessionToken;
            }
        }

        if (!string.IsNullOrEmpty(region))
        {
            envVars["AWS_DEFAULT_REGION"] = region;
        }

        var runner = new ScriptRunner();
        return await runner.RunAsync(
            resolved, syntax, context.ExtractDir, envVars, context.LogAsync, ct)
            .ConfigureAwait(false);
    }

    private static async Task<string?> BuildTemplateArgsAsync(StepHandlerContext context, CancellationToken ct)
    {
        var templateSource = Get(context, AwsConfigKeys.TemplateSource) ?? "inline";
        var templateBody = Get(context, AwsConfigKeys.TemplateBody);
        var templateFile = Get(context, AwsConfigKeys.TemplateFile);

        if (templateSource.Equals("file", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(templateFile))
        {
            var path = templateFile;
            if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrEmpty(context.ExtractDir))
            {
                var candidates = Directory.EnumerateFiles(context.ExtractDir, "*.json", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.EnumerateFiles(context.ExtractDir, "*.yaml", SearchOption.TopDirectoryOnly))
                    .Concat(Directory.EnumerateFiles(context.ExtractDir, "*.yml", SearchOption.TopDirectoryOnly))
                    .Concat(Directory.EnumerateFiles(context.ExtractDir, "*.template", SearchOption.TopDirectoryOnly))
                    .ToList();
                path = candidates.FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                await context.LogAsync("error",
                    "Template file not found. Set TemplateFile or include a .json/.yaml/.template in the package.")
                    .ConfigureAwait(false);
                return null;
            }

            return $"--template-body \"file://{path.Replace('\\', '/')}\"";
        }

        if (!string.IsNullOrWhiteSpace(templateBody))
        {
            var resolved = ResolveVariables(templateBody, context.Plan.Variables);
            var tempFile = Path.Combine(Path.GetTempPath(), $"kraken-cfn-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(tempFile, resolved, ct).ConfigureAwait(false);
            return $"--template-body \"file://{tempFile.Replace('\\', '/')}\"";
        }

        await context.LogAsync("error",
            "No CloudFormation template provided. Set TemplateBody or TemplateFile.")
            .ConfigureAwait(false);
        return null;
    }

    private static string BuildCfnParameters(string raw)
    {
        var entries = raw.Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parts = entries.Select(e =>
        {
            var eqIdx = e.IndexOf('=');
            if (eqIdx <= 0)
            {
                return null;
            }

            var key = e[..eqIdx].Trim();
            var value = e[(eqIdx + 1)..].Trim();
            return $"ParameterKey={key},ParameterValue={value}";
        }).Where(p => p is not null);

        return string.Join(" ", parts);
    }

    private static AwsCredentials? ResolveCredentials(StepHandlerContext context)
    {
        var accessKey = Get(context, AwsConfigKeys.AccessKeyId);
        var secretKey = Get(context, AwsConfigKeys.SecretAccessKey);

        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
        {
            return null;
        }

        return new AwsCredentials(accessKey, secretKey, Get(context, AwsConfigKeys.SessionToken));
    }

    private static List<string> CollectFiles(string root, string pattern)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var results = new List<string>();
        foreach (var p in pattern.Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            results.AddRange(Directory.EnumerateFiles(root, p, SearchOption.AllDirectories));
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ResolveVariables(string input, IReadOnlyDictionary<string, string> variables)
    {
        var dict = new VariableDictionary();
        foreach (var (k, v) in variables)
        {
            dict.Set(k, v);
        }

        return dict.Evaluate(input);
    }

    private static bool ParseBool(string? value)
        => value is not null && (value.Equals("True", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static string? Get(StepHandlerContext context, string key)
        => context.Step.Config.GetValueOrDefault(key);
}

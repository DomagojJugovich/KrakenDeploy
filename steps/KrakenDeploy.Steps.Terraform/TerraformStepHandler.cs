using System.Globalization;
using System.Text;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Steps.Common;
using Octostache;

namespace KrakenDeploy.Steps.Terraform;

public static class TerraformConfigKeys
{
    private const string Prefix = "Octopus.Action.Terraform.";

    public const string WorkingDirectory = Prefix + "WorkingDirectory";
    public const string Workspace = Prefix + "Workspace";
    public const string VarFile = Prefix + "VarFile";
    public const string Vars = Prefix + "Vars";
    public const string AdditionalInitArgs = Prefix + "AdditionalInitArgs";
    public const string AdditionalActionArgs = Prefix + "AdditionalActionArgs";
    public const string PlanFilePath = Prefix + "PlanFilePath";
    public const string AutoApprove = Prefix + "AutoApprove";
    public const string BackendConfig = Prefix + "BackendConfig";
    public const string SkipInit = Prefix + "SkipInit";
}

public sealed class TerraformStepHandler : IStepHandler
{
    private static readonly string[] _handledTypes =
    [
        "Octopus.TerraformApply",
        "Octopus.TerraformPlan",
        "Octopus.TerraformDestroy",
        "Octopus.TerraformPlanDestroy",
    ];

    public bool CanHandle(string stepType)
        => _handledTypes.Any(t => t.Equals(stepType, StringComparison.OrdinalIgnoreCase));

    public bool RequiresPackage => false;

    public async Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct)
    {
        var workDir = ResolveWorkingDirectory(context);
        if (workDir is null)
        {
            await context.LogAsync("error",
                "No working directory found. Set WorkingDirectory or provide a package with .tf files.")
                .ConfigureAwait(false);
            return false;
        }

        var envVars = BuildEnvVars(context);

        if (!ParseBool(Get(context, TerraformConfigKeys.SkipInit)))
        {
            await context.LogAsync("info", "Running terraform init...").ConfigureAwait(false);
            var initArgs = BuildInitArgs(context);
            var initOk = await TerraformCliRunner.RunAsync(
                initArgs, workDir, context.LogAsync, ct, envVars).ConfigureAwait(false);
            if (!initOk)
            {
                await context.LogAsync("error", "terraform init failed.").ConfigureAwait(false);
                return false;
            }
        }

        var workspace = Get(context, TerraformConfigKeys.Workspace);
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            await context.LogAsync("info",
                string.Create(CultureInfo.InvariantCulture, $"Selecting workspace '{workspace}'..."))
                .ConfigureAwait(false);
            var selectOk = await TerraformCliRunner.RunAsync(
                $"workspace select -or-create \"{workspace}\"", workDir, context.LogAsync, ct, envVars)
                .ConfigureAwait(false);
            if (!selectOk)
            {
                await context.LogAsync("error",
                    string.Create(CultureInfo.InvariantCulture, $"Failed to select workspace '{workspace}'."))
                    .ConfigureAwait(false);
                return false;
            }
        }

        return context.Step.StepType.ToLowerInvariant() switch
        {
            "octopus.terraformapply" => await HandleApplyAsync(context, workDir, envVars, ct).ConfigureAwait(false),
            "octopus.terraformplan" => await HandlePlanAsync(context, workDir, envVars, ct).ConfigureAwait(false),
            "octopus.terraformdestroy" => await HandleDestroyAsync(context, workDir, envVars, ct).ConfigureAwait(false),
            "octopus.terraformplandestroy" => await HandlePlanDestroyAsync(context, workDir, envVars, ct).ConfigureAwait(false),
            _ => false,
        };
    }

    private static async Task<bool> HandleApplyAsync(
        StepHandlerContext context, string workDir,
        IReadOnlyDictionary<string, string> envVars, CancellationToken ct)
    {
        var sb = new StringBuilder("apply");

        var autoApprove = Get(context, TerraformConfigKeys.AutoApprove);
        if (ParseBool(autoApprove) || autoApprove is null)
        {
            sb.Append(" -auto-approve");
        }

        AppendVarArgs(sb, context);
        AppendAdditionalArgs(sb, context, TerraformConfigKeys.AdditionalActionArgs);

        await context.LogAsync("info", "Running terraform apply...").ConfigureAwait(false);
        return await TerraformCliRunner.RunAsync(
            sb.ToString(), workDir, context.LogAsync, ct, envVars).ConfigureAwait(false);
    }

    private static async Task<bool> HandlePlanAsync(
        StepHandlerContext context, string workDir,
        IReadOnlyDictionary<string, string> envVars, CancellationToken ct)
    {
        var sb = new StringBuilder("plan");

        var planFile = Get(context, TerraformConfigKeys.PlanFilePath);
        if (!string.IsNullOrWhiteSpace(planFile))
        {
            sb.Append(CultureInfo.InvariantCulture, $" -out={planFile}");
        }

        AppendVarArgs(sb, context);
        AppendAdditionalArgs(sb, context, TerraformConfigKeys.AdditionalActionArgs);

        await context.LogAsync("info", "Running terraform plan...").ConfigureAwait(false);
        return await TerraformCliRunner.RunAsync(
            sb.ToString(), workDir, context.LogAsync, ct, envVars).ConfigureAwait(false);
    }

    private static async Task<bool> HandleDestroyAsync(
        StepHandlerContext context, string workDir,
        IReadOnlyDictionary<string, string> envVars, CancellationToken ct)
    {
        var sb = new StringBuilder("destroy -auto-approve");

        AppendVarArgs(sb, context);
        AppendAdditionalArgs(sb, context, TerraformConfigKeys.AdditionalActionArgs);

        await context.LogAsync("info", "Running terraform destroy...").ConfigureAwait(false);
        return await TerraformCliRunner.RunAsync(
            sb.ToString(), workDir, context.LogAsync, ct, envVars).ConfigureAwait(false);
    }

    private static async Task<bool> HandlePlanDestroyAsync(
        StepHandlerContext context, string workDir,
        IReadOnlyDictionary<string, string> envVars, CancellationToken ct)
    {
        var sb = new StringBuilder("plan -destroy");

        var planFile = Get(context, TerraformConfigKeys.PlanFilePath);
        if (!string.IsNullOrWhiteSpace(planFile))
        {
            sb.Append(CultureInfo.InvariantCulture, $" -out={planFile}");
        }

        AppendVarArgs(sb, context);
        AppendAdditionalArgs(sb, context, TerraformConfigKeys.AdditionalActionArgs);

        await context.LogAsync("info", "Running terraform plan -destroy...").ConfigureAwait(false);
        return await TerraformCliRunner.RunAsync(
            sb.ToString(), workDir, context.LogAsync, ct, envVars).ConfigureAwait(false);
    }

    private static string BuildInitArgs(StepHandlerContext context)
    {
        var sb = new StringBuilder("init");

        var backendConfig = Get(context, TerraformConfigKeys.BackendConfig);
        if (!string.IsNullOrWhiteSpace(backendConfig))
        {
            foreach (var entry in SplitLines(backendConfig))
            {
                sb.Append(CultureInfo.InvariantCulture, $" -backend-config=\"{entry}\"");
            }
        }

        AppendAdditionalArgs(sb, context, TerraformConfigKeys.AdditionalInitArgs);

        return sb.ToString();
    }

    private static void AppendVarArgs(StringBuilder sb, StepHandlerContext context)
    {
        var varFile = Get(context, TerraformConfigKeys.VarFile);
        if (!string.IsNullOrWhiteSpace(varFile))
        {
            foreach (var file in SplitLines(varFile))
            {
                sb.Append(CultureInfo.InvariantCulture, $" -var-file=\"{file}\"");
            }
        }

        var vars = Get(context, TerraformConfigKeys.Vars);
        if (!string.IsNullOrWhiteSpace(vars))
        {
            var resolved = ResolveVariables(vars, context.Plan.Variables);
            foreach (var entry in SplitLines(resolved))
            {
                sb.Append(CultureInfo.InvariantCulture, $" -var \"{entry}\"");
            }
        }
    }

    private static void AppendAdditionalArgs(StringBuilder sb, StepHandlerContext context, string key)
    {
        var additional = Get(context, key);
        if (!string.IsNullOrWhiteSpace(additional))
        {
            sb.Append(CultureInfo.InvariantCulture, $" {additional}");
        }
    }

    private static string? ResolveWorkingDirectory(StepHandlerContext context)
    {
        var configured = Get(context, TerraformConfigKeys.WorkingDirectory);
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            if (Directory.EnumerateFiles(configured, "*.tf", SearchOption.AllDirectories).Any())
            {
                return configured;
            }
        }

        if (!string.IsNullOrEmpty(context.ExtractDir) && Directory.Exists(context.ExtractDir))
        {
            if (Directory.EnumerateFiles(context.ExtractDir, "*.tf", SearchOption.AllDirectories).Any())
            {
                return context.ExtractDir;
            }
        }

        var cwd = Directory.GetCurrentDirectory();
        if (Directory.EnumerateFiles(cwd, "*.tf", SearchOption.TopDirectoryOnly).Any())
        {
            return cwd;
        }

        return null;
    }

    private static Dictionary<string, string> BuildEnvVars(StepHandlerContext context)
    {
        var envVars = new Dictionary<string, string>();

        foreach (var (key, value) in context.Plan.Variables)
        {
            if (key.StartsWith("TF_VAR_", StringComparison.OrdinalIgnoreCase))
            {
                envVars[key] = value;
            }
        }

        return envVars;
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

    private static string[] SplitLines(string raw)
        => raw.Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool ParseBool(string? value)
        => value is not null && (value.Equals("True", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static string? Get(StepHandlerContext context, string key)
        => context.Step.Config.GetValueOrDefault(key);
}

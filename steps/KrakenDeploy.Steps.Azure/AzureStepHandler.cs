using System.Globalization;
using System.Text;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Steps.Common;
using Octostache;

namespace KrakenDeploy.Steps.Azure;

public static class AzureConfigKeys
{
    private const string Prefix = "Octopus.Action.Azure.";

    public const string ServicePrincipalAppId = Prefix + "ServicePrincipalAppId";
    public const string ServicePrincipalPassword = Prefix + "ServicePrincipalPassword";
    public const string TenantId = Prefix + "TenantId";
    public const string SubscriptionId = Prefix + "SubscriptionId";
    public const string ResourceGroupName = Prefix + "ResourceGroupName";

    public const string WebAppName = Prefix + "WebAppName";
    public const string WebAppSlot = Prefix + "WebAppSlot";
    public const string PackageUri = Prefix + "PackageUri";
    public const string PublishProfile = Prefix + "PublishProfile";

    public const string ScriptBody = Prefix + "ScriptBody";
    public const string ScriptSyntax = Prefix + "ScriptSyntax";

    public const string TemplateFile = Prefix + "TemplateFile";
    public const string TemplateBody = Prefix + "TemplateBody";
    public const string TemplateParameters = Prefix + "TemplateParameters";
    public const string DeploymentName = Prefix + "DeploymentName";
    public const string DeploymentMode = Prefix + "DeploymentMode";

    public const string BicepFile = Prefix + "BicepFile";
}

public sealed class AzureStepHandler : IStepHandler
{
    private static readonly string[] _handledTypes =
    [
        "Octopus.AzureWebApp",
        "Octopus.AzureAppService",
        "Octopus.AzurePowerShell",
        "Octopus.AzureResourceGroup",
        "deploy-a-bicep-template",
    ];

    public bool CanHandle(string stepType)
        => _handledTypes.Any(t => t.Equals(stepType, StringComparison.OrdinalIgnoreCase));

    public bool RequiresPackage => false;

    public async Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct)
    {
        var appId = Get(context, AzureConfigKeys.ServicePrincipalAppId);
        var password = Get(context, AzureConfigKeys.ServicePrincipalPassword);
        var tenantId = Get(context, AzureConfigKeys.TenantId);

        if (!await AzureCliRunner.LoginAsync(appId, password, tenantId, context.LogAsync, ct)
            .ConfigureAwait(false))
        {
            return false;
        }

        var subscriptionId = Get(context, AzureConfigKeys.SubscriptionId);
        if (!string.IsNullOrWhiteSpace(subscriptionId))
        {
            var ok = await AzureCliRunner.RunAsync(
                $"account set --subscription {subscriptionId}",
                ".", context.LogAsync, ct).ConfigureAwait(false);
            if (!ok)
            {
                await context.LogAsync("error",
                    string.Create(CultureInfo.InvariantCulture, $"Failed to set subscription '{subscriptionId}'."))
                    .ConfigureAwait(false);
                return false;
            }
        }

        return context.Step.StepType.ToLowerInvariant() switch
        {
            "octopus.azurewebapp" => await HandleWebAppDeployAsync(context, ct).ConfigureAwait(false),
            "octopus.azureappservice" => await HandleWebAppDeployAsync(context, ct).ConfigureAwait(false),
            "octopus.azurepowershell" => await HandlePowerShellAsync(context, ct).ConfigureAwait(false),
            "octopus.azureresourcegroup" => await HandleResourceGroupAsync(context, ct).ConfigureAwait(false),
            "deploy-a-bicep-template" => await HandleBicepAsync(context, ct).ConfigureAwait(false),
            _ => false,
        };
    }

    private static async Task<bool> HandleWebAppDeployAsync(
        StepHandlerContext context, CancellationToken ct)
    {
        var webAppName = Get(context, AzureConfigKeys.WebAppName);
        if (string.IsNullOrWhiteSpace(webAppName))
        {
            await context.LogAsync("error", "WebAppName is required for Azure Web App deployment.")
                .ConfigureAwait(false);
            return false;
        }

        var resourceGroup = Get(context, AzureConfigKeys.ResourceGroupName);
        if (string.IsNullOrWhiteSpace(resourceGroup))
        {
            await context.LogAsync("error", "ResourceGroupName is required.")
                .ConfigureAwait(false);
            return false;
        }

        var slot = Get(context, AzureConfigKeys.WebAppSlot);
        var packageUri = Get(context, AzureConfigKeys.PackageUri);

        string sourcePath;
        string? tempZip = null;

        if (!string.IsNullOrWhiteSpace(packageUri))
        {
            sourcePath = packageUri;
        }
        else if (!string.IsNullOrEmpty(context.ExtractDir) && Directory.Exists(context.ExtractDir))
        {
            tempZip = Path.Combine(Path.GetTempPath(), $"kraken-azure-{Guid.NewGuid():N}.zip");
            System.IO.Compression.ZipFile.CreateFromDirectory(context.ExtractDir, tempZip);
            sourcePath = tempZip;
        }
        else
        {
            await context.LogAsync("error",
                "No package source. Provide PackageUri or a deployment package.")
                .ConfigureAwait(false);
            return false;
        }

        try
        {
            var sb = new StringBuilder("webapp deployment source config-zip");
            sb.Append(CultureInfo.InvariantCulture, $" --name {webAppName}");
            sb.Append(CultureInfo.InvariantCulture, $" --resource-group {resourceGroup}");
            sb.Append(CultureInfo.InvariantCulture, $" --src \"{sourcePath}\"");

            if (!string.IsNullOrWhiteSpace(slot))
            {
                sb.Append(CultureInfo.InvariantCulture, $" --slot {slot}");
            }

            await context.LogAsync("info",
                string.Create(CultureInfo.InvariantCulture,
                    $"Deploying to Azure Web App '{webAppName}'{(string.IsNullOrWhiteSpace(slot) ? "" : $" (slot: {slot})")}..."))
                .ConfigureAwait(false);

            return await AzureCliRunner.RunAsync(sb.ToString(), ".", context.LogAsync, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            if (tempZip is not null && File.Exists(tempZip))
            {
                File.Delete(tempZip);
            }
        }
    }

    private static async Task<bool> HandlePowerShellAsync(
        StepHandlerContext context, CancellationToken ct)
    {
        var scriptBody = Get(context, AzureConfigKeys.ScriptBody);
        if (string.IsNullOrWhiteSpace(scriptBody))
        {
            await context.LogAsync("error", "ScriptBody is required for AzurePowerShell.")
                .ConfigureAwait(false);
            return false;
        }

        var resolved = ResolveVariables(scriptBody, context.Plan.Variables);

        var envVars = BuildAzureEnvVars(context);

        var runner = new ScriptRunner();
        return await runner.RunAsync(
            resolved, "PowerShell", context.ExtractDir, envVars, context.LogAsync, ct)
            .ConfigureAwait(false);
    }

    private static async Task<bool> HandleResourceGroupAsync(
        StepHandlerContext context, CancellationToken ct)
    {
        var resourceGroup = Get(context, AzureConfigKeys.ResourceGroupName);
        if (string.IsNullOrWhiteSpace(resourceGroup))
        {
            await context.LogAsync("error", "ResourceGroupName is required for AzureResourceGroup.")
                .ConfigureAwait(false);
            return false;
        }

        var templateArgs = await BuildTemplateArgsAsync(context, ct).ConfigureAwait(false);
        if (templateArgs is null)
        {
            return false;
        }

        var deploymentName = Get(context, AzureConfigKeys.DeploymentName)
            ?? $"kraken-{Guid.NewGuid():N}";
        var mode = Get(context, AzureConfigKeys.DeploymentMode) ?? "Incremental";

        var sb = new StringBuilder("deployment group create");
        sb.Append(CultureInfo.InvariantCulture, $" --resource-group {resourceGroup}");
        sb.Append(CultureInfo.InvariantCulture, $" --name {deploymentName}");
        sb.Append(CultureInfo.InvariantCulture, $" --mode {mode}");
        sb.Append(CultureInfo.InvariantCulture, $" {templateArgs}");

        var parameters = Get(context, AzureConfigKeys.TemplateParameters);
        if (!string.IsNullOrWhiteSpace(parameters))
        {
            var tempParams = Path.Combine(Path.GetTempPath(), $"kraken-arm-params-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(tempParams, BuildArmParametersJson(parameters), ct)
                .ConfigureAwait(false);
            try
            {
                sb.Append(CultureInfo.InvariantCulture, $" --parameters \"{tempParams}\"");

                await context.LogAsync("info",
                    string.Create(CultureInfo.InvariantCulture,
                        $"Deploying ARM template to resource group '{resourceGroup}' (mode: {mode})..."))
                    .ConfigureAwait(false);

                return await AzureCliRunner.RunAsync(sb.ToString(), ".", context.LogAsync, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                File.Delete(tempParams);
            }
        }

        await context.LogAsync("info",
            string.Create(CultureInfo.InvariantCulture,
                $"Deploying ARM template to resource group '{resourceGroup}' (mode: {mode})..."))
            .ConfigureAwait(false);

        return await AzureCliRunner.RunAsync(sb.ToString(), ".", context.LogAsync, ct)
            .ConfigureAwait(false);
    }

    private static async Task<bool> HandleBicepAsync(
        StepHandlerContext context, CancellationToken ct)
    {
        var resourceGroup = Get(context, AzureConfigKeys.ResourceGroupName);
        if (string.IsNullOrWhiteSpace(resourceGroup))
        {
            await context.LogAsync("error", "ResourceGroupName is required for Bicep deployment.")
                .ConfigureAwait(false);
            return false;
        }

        var bicepFile = Get(context, AzureConfigKeys.BicepFile);
        if (string.IsNullOrWhiteSpace(bicepFile) && !string.IsNullOrEmpty(context.ExtractDir))
        {
            var candidates = Directory.EnumerateFiles(context.ExtractDir, "*.bicep", SearchOption.AllDirectories)
                .ToList();
            bicepFile = candidates.FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(bicepFile) || !File.Exists(bicepFile))
        {
            await context.LogAsync("error",
                "No Bicep file found. Set BicepFile or include a .bicep file in the package.")
                .ConfigureAwait(false);
            return false;
        }

        var deploymentName = Get(context, AzureConfigKeys.DeploymentName)
            ?? $"kraken-bicep-{Guid.NewGuid():N}";
        var mode = Get(context, AzureConfigKeys.DeploymentMode) ?? "Incremental";

        var sb = new StringBuilder("deployment group create");
        sb.Append(CultureInfo.InvariantCulture, $" --resource-group {resourceGroup}");
        sb.Append(CultureInfo.InvariantCulture, $" --name {deploymentName}");
        sb.Append(CultureInfo.InvariantCulture, $" --mode {mode}");
        sb.Append(CultureInfo.InvariantCulture, $" --template-file \"{bicepFile}\"");

        var parameters = Get(context, AzureConfigKeys.TemplateParameters);
        if (!string.IsNullOrWhiteSpace(parameters))
        {
            var tempParams = Path.Combine(Path.GetTempPath(), $"kraken-bicep-params-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(tempParams, BuildArmParametersJson(parameters), ct)
                .ConfigureAwait(false);
            try
            {
                sb.Append(CultureInfo.InvariantCulture, $" --parameters \"{tempParams}\"");

                await context.LogAsync("info",
                    string.Create(CultureInfo.InvariantCulture,
                        $"Deploying Bicep template '{Path.GetFileName(bicepFile)}' to resource group '{resourceGroup}'..."))
                    .ConfigureAwait(false);

                return await AzureCliRunner.RunAsync(sb.ToString(), ".", context.LogAsync, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                File.Delete(tempParams);
            }
        }

        await context.LogAsync("info",
            string.Create(CultureInfo.InvariantCulture,
                $"Deploying Bicep template '{Path.GetFileName(bicepFile)}' to resource group '{resourceGroup}'..."))
            .ConfigureAwait(false);

        return await AzureCliRunner.RunAsync(sb.ToString(), ".", context.LogAsync, ct)
            .ConfigureAwait(false);
    }

    private static async Task<string?> BuildTemplateArgsAsync(StepHandlerContext context, CancellationToken ct)
    {
        var templateFile = Get(context, AzureConfigKeys.TemplateFile);
        var templateBody = Get(context, AzureConfigKeys.TemplateBody);

        if (!string.IsNullOrWhiteSpace(templateFile) && File.Exists(templateFile))
        {
            return $"--template-file \"{templateFile}\"";
        }

        if (string.IsNullOrWhiteSpace(templateFile) && !string.IsNullOrEmpty(context.ExtractDir))
        {
            var candidates = Directory.EnumerateFiles(context.ExtractDir, "*.json", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(context.ExtractDir, "azuredeploy.json", SearchOption.AllDirectories))
                .ToList();
            var found = candidates.FirstOrDefault();
            if (found is not null)
            {
                return $"--template-file \"{found}\"";
            }
        }

        if (!string.IsNullOrWhiteSpace(templateBody))
        {
            var resolved = ResolveVariables(templateBody, context.Plan.Variables);
            var tempFile = Path.Combine(Path.GetTempPath(), $"kraken-arm-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(tempFile, resolved, ct).ConfigureAwait(false);
            return $"--template-file \"{tempFile}\"";
        }

        await context.LogAsync("error",
            "No ARM template provided. Set TemplateFile, TemplateBody, or include azuredeploy.json in the package.")
            .ConfigureAwait(false);
        return null;
    }

    private static string BuildArmParametersJson(string raw)
    {
        var entries = raw.Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"$schema\": \"https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#\",");
        sb.AppendLine("  \"contentVersion\": \"1.0.0.0\",");
        sb.AppendLine("  \"parameters\": {");

        var first = true;
        foreach (var entry in entries)
        {
            var eqIdx = entry.IndexOf('=');
            if (eqIdx <= 0)
            {
                continue;
            }

            var key = entry[..eqIdx].Trim();
            var value = entry[(eqIdx + 1)..].Trim();

            if (!first)
            {
                sb.AppendLine(",");
            }

            sb.Append(CultureInfo.InvariantCulture, $"    \"{key}\": {{ \"value\": \"{value}\" }}");
            first = false;
        }

        sb.AppendLine();
        sb.AppendLine("  }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static Dictionary<string, string> BuildAzureEnvVars(StepHandlerContext context)
    {
        var envVars = new Dictionary<string, string>();

        var appId = Get(context, AzureConfigKeys.ServicePrincipalAppId);
        var password = Get(context, AzureConfigKeys.ServicePrincipalPassword);
        var tenantId = Get(context, AzureConfigKeys.TenantId);
        var subscriptionId = Get(context, AzureConfigKeys.SubscriptionId);

        if (!string.IsNullOrWhiteSpace(appId))
        {
            envVars["AZURE_CLIENT_ID"] = appId;
        }

        if (!string.IsNullOrWhiteSpace(password))
        {
            envVars["AZURE_CLIENT_SECRET"] = password;
        }

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            envVars["AZURE_TENANT_ID"] = tenantId;
        }

        if (!string.IsNullOrWhiteSpace(subscriptionId))
        {
            envVars["AZURE_SUBSCRIPTION_ID"] = subscriptionId;
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

    private static string? Get(StepHandlerContext context, string key)
        => context.Step.Config.GetValueOrDefault(key);
}

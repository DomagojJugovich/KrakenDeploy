using System.Globalization;
using System.Text;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Steps.Common;
using Octostache;

namespace KrakenDeploy.Steps.Java;

public static class JavaConfigKeys
{
    private const string Prefix = "Octopus.Action.Java.";

    public const string ArchiveType = Prefix + "ArchiveType";
    public const string DeployPath = Prefix + "DeployPath";
    public const string JavaHome = Prefix + "JavaHome";

    public const string TomcatHome = Prefix + "TomcatHome";
    public const string TomcatUser = Prefix + "TomcatUser";
    public const string TomcatPassword = Prefix + "TomcatPassword";
    public const string TomcatUrl = Prefix + "TomcatUrl";
    public const string TomcatServiceName = Prefix + "TomcatServiceName";
    public const string TomcatAction = Prefix + "TomcatAction";
    public const string TomcatKeystorePath = Prefix + "TomcatKeystorePath";
    public const string TomcatKeystorePassword = Prefix + "TomcatKeystorePassword";

    public const string WildFlyHome = Prefix + "WildFlyHome";
    public const string WildFlyUser = Prefix + "WildFlyUser";
    public const string WildFlyPassword = Prefix + "WildFlyPassword";
    public const string WildFlyHost = Prefix + "WildFlyHost";
    public const string WildFlyPort = Prefix + "WildFlyPort";
    public const string WildFlyAction = Prefix + "WildFlyAction";
    public const string WildFlyServerGroupName = Prefix + "WildFlyServerGroupName";

    public const string CertificatePath = Prefix + "CertificatePath";
    public const string CertificatePassword = Prefix + "CertificatePassword";
    public const string KeystorePath = Prefix + "KeystorePath";
    public const string KeystorePassword = Prefix + "KeystorePassword";
    public const string KeystoreType = Prefix + "KeystoreType";
    public const string KeystoreAlias = Prefix + "KeystoreAlias";

    public const string DeploymentName = Prefix + "DeploymentName";
    public const string ForceDeploy = Prefix + "ForceDeploy";
}

public sealed class JavaStepHandler : IStepHandler
{
    private static readonly string[] _handledTypes =
    [
        "Octopus.JavaArchive",
        "Octopus.TomcatDeploy",
        "Octopus.TomcatState",
        "Octopus.TomcatDeployCertificate",
        "Octopus.WildFlyDeploy",
        "Octopus.WildFlyState",
        "Octopus.WildFlyCertificateDeploy",
        "Octopus.JavaDeployCertificate",
    ];

    public bool CanHandle(string stepType)
        => _handledTypes.Any(t => t.Equals(stepType, StringComparison.OrdinalIgnoreCase));

    public bool RequiresPackage => false;

    public async Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct)
    {
        return context.Step.StepType.ToLowerInvariant() switch
        {
            "octopus.javaarchive" => await HandleJavaArchiveAsync(context, ct).ConfigureAwait(false),
            "octopus.tomcatdeploy" => await HandleTomcatDeployAsync(context, ct).ConfigureAwait(false),
            "octopus.tomcatstate" => await HandleTomcatStateAsync(context, ct).ConfigureAwait(false),
            "octopus.tomcatdeploycertificate" => await HandleTomcatCertificateAsync(context, ct).ConfigureAwait(false),
            "octopus.wildflydeploy" => await HandleWildFlyDeployAsync(context, ct).ConfigureAwait(false),
            "octopus.wildflystate" => await HandleWildFlyStateAsync(context, ct).ConfigureAwait(false),
            "octopus.wildflycertificatedeploy" => await HandleWildFlyCertificateAsync(context, ct).ConfigureAwait(false),
            "octopus.javadeploycertificate" => await HandleJavaDeployCertificateAsync(context, ct).ConfigureAwait(false),
            _ => false,
        };
    }

    private static async Task<bool> HandleJavaArchiveAsync(
        StepHandlerContext context, CancellationToken ct)
    {
        var deployPath = Get(context, JavaConfigKeys.DeployPath);
        if (string.IsNullOrWhiteSpace(deployPath))
        {
            await context.LogAsync("error", "DeployPath is required for JavaArchive.").ConfigureAwait(false);
            return false;
        }

        var sourceDir = context.ExtractDir;
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
        {
            await context.LogAsync("error",
                "JavaArchive requires a deployment package with the archive file.")
                .ConfigureAwait(false);
            return false;
        }

        var archiveFiles = Directory.EnumerateFiles(sourceDir, "*.war", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(sourceDir, "*.jar", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(sourceDir, "*.ear", SearchOption.AllDirectories))
            .ToList();

        if (archiveFiles.Count == 0)
        {
            await context.LogAsync("error",
                "No .war, .jar, or .ear files found in the deployment package.")
                .ConfigureAwait(false);
            return false;
        }

        Directory.CreateDirectory(deployPath);

        foreach (var file in archiveFiles)
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(file);
            var target = Path.Combine(deployPath, fileName);

            await context.LogAsync("info",
                string.Create(CultureInfo.InvariantCulture, $"Deploying {fileName} → {target}"))
                .ConfigureAwait(false);

            File.Copy(file, target, overwrite: true);
        }

        await context.LogAsync("info",
            string.Create(CultureInfo.InvariantCulture, $"Deployed {archiveFiles.Count} archive(s) to {deployPath}."))
            .ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> HandleTomcatDeployAsync(
        StepHandlerContext context, CancellationToken ct)
    {
        var tomcatHome = Get(context, JavaConfigKeys.TomcatHome);
        var deployPath = Get(context, JavaConfigKeys.DeployPath);

        if (string.IsNullOrWhiteSpace(tomcatHome) && string.IsNullOrWhiteSpace(deployPath))
        {
            await context.LogAsync("error",
                "TomcatHome or DeployPath is required for TomcatDeploy.")
                .ConfigureAwait(false);
            return false;
        }

        var webappsDir = deployPath
            ?? Path.Combine(tomcatHome!, "webapps");

        var sourceDir = context.ExtractDir;
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
        {
            await context.LogAsync("error",
                "TomcatDeploy requires a deployment package with a .war file.")
                .ConfigureAwait(false);
            return false;
        }

        var warFiles = Directory.EnumerateFiles(sourceDir, "*.war", SearchOption.AllDirectories).ToList();
        if (warFiles.Count == 0)
        {
            await context.LogAsync("error", "No .war file found in the deployment package.")
                .ConfigureAwait(false);
            return false;
        }

        Directory.CreateDirectory(webappsDir);

        foreach (var war in warFiles)
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(war);
            var target = Path.Combine(webappsDir, fileName);

            await context.LogAsync("info",
                string.Create(CultureInfo.InvariantCulture, $"Deploying {fileName} → {target}"))
                .ConfigureAwait(false);

            File.Copy(war, target, overwrite: true);
        }

        await context.LogAsync("info",
            string.Create(CultureInfo.InvariantCulture, $"Deployed {warFiles.Count} WAR(s) to Tomcat webapps."))
            .ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> HandleTomcatStateAsync(
        StepHandlerContext context, CancellationToken ct)
    {
        var action = Get(context, JavaConfigKeys.TomcatAction) ?? "restart";
        var tomcatHome = Get(context, JavaConfigKeys.TomcatHome);
        var serviceName = Get(context, JavaConfigKeys.TomcatServiceName);

        var runner = new ScriptRunner();
        string script;

        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            script = OperatingSystem.IsWindows()
                ? BuildWindowsServiceScript(serviceName, action)
                : BuildLinuxServiceScript(serviceName, action);
        }
        else if (!string.IsNullOrWhiteSpace(tomcatHome))
        {
            script = BuildCatalinaScript(tomcatHome, action);
        }
        else
        {
            await context.LogAsync("error",
                "TomcatHome or TomcatServiceName is required for TomcatState.")
                .ConfigureAwait(false);
            return false;
        }

        var syntax = OperatingSystem.IsWindows() ? "PowerShell" : "Bash";
        await context.LogAsync("info",
            string.Create(CultureInfo.InvariantCulture, $"Tomcat {action}..."))
            .ConfigureAwait(false);

        return await runner.RunAsync(
            script, syntax, ".", new Dictionary<string, string>(), context.LogAsync, ct)
            .ConfigureAwait(false);
    }

    private static async Task<bool> HandleTomcatCertificateAsync(
        StepHandlerContext context, CancellationToken ct)
    {
        var keystorePath = Get(context, JavaConfigKeys.TomcatKeystorePath)
            ?? Get(context, JavaConfigKeys.KeystorePath);
        var keystorePassword = Get(context, JavaConfigKeys.TomcatKeystorePassword)
            ?? Get(context, JavaConfigKeys.KeystorePassword);
        var certPath = Get(context, JavaConfigKeys.CertificatePath);
        var alias = Get(context, JavaConfigKeys.KeystoreAlias) ?? "tomcat";
        var javaHome = Get(context, JavaConfigKeys.JavaHome);

        if (string.IsNullOrWhiteSpace(keystorePath))
        {
            await context.LogAsync("error", "KeystorePath is required for TomcatDeployCertificate.")
                .ConfigureAwait(false);
            return false;
        }

        if (string.IsNullOrWhiteSpace(certPath) && string.IsNullOrEmpty(context.ExtractDir))
        {
            await context.LogAsync("error",
                "CertificatePath or a deployment package with a certificate is required.")
                .ConfigureAwait(false);
            return false;
        }

        var actualCertPath = certPath;
        if (string.IsNullOrWhiteSpace(actualCertPath) && !string.IsNullOrEmpty(context.ExtractDir))
        {
            actualCertPath = Directory.EnumerateFiles(context.ExtractDir, "*.pfx", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(context.ExtractDir, "*.p12", SearchOption.AllDirectories))
                .Concat(Directory.EnumerateFiles(context.ExtractDir, "*.jks", SearchOption.AllDirectories))
                .FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(actualCertPath) || !File.Exists(actualCertPath))
        {
            await context.LogAsync("error", "Certificate file not found.").ConfigureAwait(false);
            return false;
        }

        var keytoolPath = string.IsNullOrWhiteSpace(javaHome)
            ? "keytool"
            : Path.Combine(javaHome, "bin", "keytool");

        var keystoreType = actualCertPath.EndsWith(".jks", StringComparison.OrdinalIgnoreCase)
            ? "JKS" : "PKCS12";

        var script = new StringBuilder();
        script.AppendLine(CultureInfo.InvariantCulture,
            $"& \"{keytoolPath}\" -importkeystore -srckeystore \"{actualCertPath}\" -srcstoretype PKCS12 -srcstorepass \"{Get(context, JavaConfigKeys.CertificatePassword) ?? ""}\" -destkeystore \"{keystorePath}\" -deststoretype {keystoreType} -deststorepass \"{keystorePassword ?? ""}\" -destkeypass \"{keystorePassword ?? ""}\" -alias {alias} -noprompt");

        await context.LogAsync("info",
            string.Create(CultureInfo.InvariantCulture, $"Importing certificate into keystore '{keystorePath}'..."))
            .ConfigureAwait(false);

        var runner = new ScriptRunner();
        return await runner.RunAsync(
            script.ToString(), "PowerShell", ".", new Dictionary<string, string>(), context.LogAsync, ct)
            .ConfigureAwait(false);
    }

    private static async Task<bool> HandleWildFlyDeployAsync(
        StepHandlerContext context, CancellationToken ct)
    {
        var wildflyHome = Get(context, JavaConfigKeys.WildFlyHome);
        var host = Get(context, JavaConfigKeys.WildFlyHost) ?? "localhost";
        var port = Get(context, JavaConfigKeys.WildFlyPort) ?? "9990";
        var user = Get(context, JavaConfigKeys.WildFlyUser);
        var password = Get(context, JavaConfigKeys.WildFlyPassword);
        var deploymentName = Get(context, JavaConfigKeys.DeploymentName);
        var force = ParseBool(Get(context, JavaConfigKeys.ForceDeploy));
        var serverGroup = Get(context, JavaConfigKeys.WildFlyServerGroupName);

        var sourceDir = context.ExtractDir;
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
        {
            await context.LogAsync("error",
                "WildFlyDeploy requires a deployment package with a .war/.ear file.")
                .ConfigureAwait(false);
            return false;
        }

        var archiveFile = Directory.EnumerateFiles(sourceDir, "*.war", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(sourceDir, "*.ear", SearchOption.AllDirectories))
            .FirstOrDefault();

        if (archiveFile is null)
        {
            await context.LogAsync("error", "No .war or .ear file found in the deployment package.")
                .ConfigureAwait(false);
            return false;
        }

        deploymentName ??= Path.GetFileName(archiveFile);

        var cliPath = string.IsNullOrWhiteSpace(wildflyHome)
            ? "jboss-cli.sh"
            : Path.Combine(wildflyHome, "bin", OperatingSystem.IsWindows() ? "jboss-cli.bat" : "jboss-cli.sh");

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"deploy \"{archiveFile}\" --name={deploymentName}");
        sb.Append(CultureInfo.InvariantCulture, $" --controller={host}:{port}");

        if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(password))
        {
            sb.Append(CultureInfo.InvariantCulture, $" --user={user} --password={password}");
        }

        if (force)
        {
            sb.Append(" --force");
        }

        if (!string.IsNullOrWhiteSpace(serverGroup))
        {
            sb.Append(CultureInfo.InvariantCulture, $" --server-groups={serverGroup}");
        }

        await context.LogAsync("info",
            string.Create(CultureInfo.InvariantCulture, $"Deploying {deploymentName} to WildFly at {host}:{port}..."))
            .ConfigureAwait(false);

        var runner = new ScriptRunner();
        var syntax = OperatingSystem.IsWindows() ? "PowerShell" : "Bash";
        var script = OperatingSystem.IsWindows()
            ? $"& \"{cliPath}\" --connect --command=\"{sb}\""
            : $"\"{cliPath}\" --connect --command=\"{sb}\"";

        return await runner.RunAsync(
            script, syntax, ".", new Dictionary<string, string>(), context.LogAsync, ct)
            .ConfigureAwait(false);
    }

    private static async Task<bool> HandleWildFlyStateAsync(
        StepHandlerContext context, CancellationToken ct)
    {
        var action = Get(context, JavaConfigKeys.WildFlyAction) ?? "restart";
        var wildflyHome = Get(context, JavaConfigKeys.WildFlyHome);
        var host = Get(context, JavaConfigKeys.WildFlyHost) ?? "localhost";
        var port = Get(context, JavaConfigKeys.WildFlyPort) ?? "9990";
        var user = Get(context, JavaConfigKeys.WildFlyUser);
        var password = Get(context, JavaConfigKeys.WildFlyPassword);

        var cliPath = string.IsNullOrWhiteSpace(wildflyHome)
            ? "jboss-cli.sh"
            : Path.Combine(wildflyHome, "bin", OperatingSystem.IsWindows() ? "jboss-cli.bat" : "jboss-cli.sh");

        var cliCommand = action.ToLowerInvariant() switch
        {
            "start" => ":launch",
            "stop" or "shutdown" => ":shutdown",
            "restart" => ":shutdown(restart=true)",
            "reload" => ":reload",
            _ => ":reload",
        };

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"--connect --controller={host}:{port}");

        if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(password))
        {
            sb.Append(CultureInfo.InvariantCulture, $" --user={user} --password={password}");
        }

        sb.Append(CultureInfo.InvariantCulture, $" --command=\"{cliCommand}\"");

        await context.LogAsync("info",
            string.Create(CultureInfo.InvariantCulture, $"WildFly {action} at {host}:{port}..."))
            .ConfigureAwait(false);

        var runner = new ScriptRunner();
        var syntax = OperatingSystem.IsWindows() ? "PowerShell" : "Bash";
        var script = OperatingSystem.IsWindows()
            ? $"& \"{cliPath}\" {sb}"
            : $"\"{cliPath}\" {sb}";

        return await runner.RunAsync(
            script, syntax, ".", new Dictionary<string, string>(), context.LogAsync, ct)
            .ConfigureAwait(false);
    }

    private static async Task<bool> HandleWildFlyCertificateAsync(
        StepHandlerContext context, CancellationToken ct)
    {
        var keystorePath = Get(context, JavaConfigKeys.KeystorePath);
        var keystorePassword = Get(context, JavaConfigKeys.KeystorePassword);
        var certPath = Get(context, JavaConfigKeys.CertificatePath);
        var alias = Get(context, JavaConfigKeys.KeystoreAlias) ?? "wildfly";
        var javaHome = Get(context, JavaConfigKeys.JavaHome);

        if (string.IsNullOrWhiteSpace(keystorePath))
        {
            await context.LogAsync("error", "KeystorePath is required for WildFlyCertificateDeploy.")
                .ConfigureAwait(false);
            return false;
        }

        var actualCertPath = certPath;
        if (string.IsNullOrWhiteSpace(actualCertPath) && !string.IsNullOrEmpty(context.ExtractDir))
        {
            actualCertPath = Directory.EnumerateFiles(context.ExtractDir, "*.pfx", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(context.ExtractDir, "*.p12", SearchOption.AllDirectories))
                .FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(actualCertPath) || !File.Exists(actualCertPath))
        {
            await context.LogAsync("error", "Certificate file not found.").ConfigureAwait(false);
            return false;
        }

        var keytoolPath = string.IsNullOrWhiteSpace(javaHome)
            ? "keytool"
            : Path.Combine(javaHome, "bin", "keytool");

        var script = new StringBuilder();
        script.AppendLine(CultureInfo.InvariantCulture,
            $"& \"{keytoolPath}\" -importkeystore -srckeystore \"{actualCertPath}\" -srcstoretype PKCS12 -srcstorepass \"{Get(context, JavaConfigKeys.CertificatePassword) ?? ""}\" -destkeystore \"{keystorePath}\" -deststoretype PKCS12 -deststorepass \"{keystorePassword ?? ""}\" -destkeypass \"{keystorePassword ?? ""}\" -alias {alias} -noprompt");

        await context.LogAsync("info",
            string.Create(CultureInfo.InvariantCulture, $"Importing certificate into WildFly keystore '{keystorePath}'..."))
            .ConfigureAwait(false);

        var runner = new ScriptRunner();
        return await runner.RunAsync(
            script.ToString(), "PowerShell", ".", new Dictionary<string, string>(), context.LogAsync, ct)
            .ConfigureAwait(false);
    }

    private static async Task<bool> HandleJavaDeployCertificateAsync(
        StepHandlerContext context, CancellationToken ct)
    {
        var keystorePath = Get(context, JavaConfigKeys.KeystorePath);
        var keystorePassword = Get(context, JavaConfigKeys.KeystorePassword);
        var keystoreType = Get(context, JavaConfigKeys.KeystoreType) ?? "PKCS12";
        var certPath = Get(context, JavaConfigKeys.CertificatePath);
        var alias = Get(context, JavaConfigKeys.KeystoreAlias) ?? "kraken";
        var javaHome = Get(context, JavaConfigKeys.JavaHome);

        if (string.IsNullOrWhiteSpace(keystorePath))
        {
            await context.LogAsync("error", "KeystorePath is required for JavaDeployCertificate.")
                .ConfigureAwait(false);
            return false;
        }

        var actualCertPath = certPath;
        if (string.IsNullOrWhiteSpace(actualCertPath) && !string.IsNullOrEmpty(context.ExtractDir))
        {
            actualCertPath = Directory.EnumerateFiles(context.ExtractDir, "*.cer", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(context.ExtractDir, "*.crt", SearchOption.AllDirectories))
                .Concat(Directory.EnumerateFiles(context.ExtractDir, "*.pem", SearchOption.AllDirectories))
                .FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(actualCertPath) || !File.Exists(actualCertPath))
        {
            await context.LogAsync("error", "Certificate file not found.").ConfigureAwait(false);
            return false;
        }

        var keytoolPath = string.IsNullOrWhiteSpace(javaHome)
            ? "keytool"
            : Path.Combine(javaHome, "bin", "keytool");

        var script = new StringBuilder();
        script.AppendLine(CultureInfo.InvariantCulture,
            $"& \"{keytoolPath}\" -importcert -file \"{actualCertPath}\" -keystore \"{keystorePath}\" -storetype {keystoreType} -storepass \"{keystorePassword ?? ""}\" -alias {alias} -noprompt");

        await context.LogAsync("info",
            string.Create(CultureInfo.InvariantCulture, $"Importing certificate '{Path.GetFileName(actualCertPath)}' into keystore '{keystorePath}'..."))
            .ConfigureAwait(false);

        var runner = new ScriptRunner();
        return await runner.RunAsync(
            script.ToString(), "PowerShell", ".", new Dictionary<string, string>(), context.LogAsync, ct)
            .ConfigureAwait(false);
    }

    private static string BuildWindowsServiceScript(string serviceName, string action)
    {
        return action.ToLowerInvariant() switch
        {
            "start" => $"Start-Service -Name '{serviceName}'",
            "stop" => $"Stop-Service -Name '{serviceName}' -Force",
            "restart" => $"Restart-Service -Name '{serviceName}' -Force",
            _ => $"Restart-Service -Name '{serviceName}' -Force",
        };
    }

    private static string BuildLinuxServiceScript(string serviceName, string action)
    {
        return $"systemctl {action.ToLowerInvariant()} {serviceName}";
    }

    private static string BuildCatalinaScript(string tomcatHome, string action)
    {
        var catalinaScript = OperatingSystem.IsWindows()
            ? Path.Combine(tomcatHome, "bin", "catalina.bat")
            : Path.Combine(tomcatHome, "bin", "catalina.sh");

        return action.ToLowerInvariant() switch
        {
            "start" => OperatingSystem.IsWindows()
                ? $"& \"{catalinaScript}\" start"
                : $"\"{catalinaScript}\" start",
            "stop" => OperatingSystem.IsWindows()
                ? $"& \"{catalinaScript}\" stop"
                : $"\"{catalinaScript}\" stop",
            "restart" => OperatingSystem.IsWindows()
                ? $"& \"{catalinaScript}\" stop; Start-Sleep -Seconds 5; & \"{catalinaScript}\" start"
                : $"\"{catalinaScript}\" stop && sleep 5 && \"{catalinaScript}\" start",
            _ => OperatingSystem.IsWindows()
                ? $"& \"{catalinaScript}\" stop; Start-Sleep -Seconds 5; & \"{catalinaScript}\" start"
                : $"\"{catalinaScript}\" stop && sleep 5 && \"{catalinaScript}\" start",
        };
    }

    private static bool ParseBool(string? value)
        => value is not null && (value.Equals("True", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static string? Get(StepHandlerContext context, string key)
        => context.Step.Config.GetValueOrDefault(key);
}

using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Steps.Common;
using Octostache;

namespace KrakenDeploy.Steps.Misc;

public static class MiscConfigKeys
{
    private const string Prefix = "Octopus.Action.";

    public const string SmtpHost = Prefix + "Email.SmtpHost";
    public const string SmtpPort = Prefix + "Email.SmtpPort";
    public const string SmtpUsername = Prefix + "Email.SmtpUsername";
    public const string SmtpPassword = Prefix + "Email.SmtpPassword";
    public const string SmtpUseSsl = Prefix + "Email.SmtpUseSsl";
    public const string EmailFrom = Prefix + "Email.From";
    public const string EmailTo = Prefix + "Email.To";
    public const string EmailCc = Prefix + "Email.Cc";
    public const string EmailBcc = Prefix + "Email.Bcc";
    public const string EmailSubject = Prefix + "Email.Subject";
    public const string EmailBody = Prefix + "Email.Body";
    public const string EmailIsHtml = Prefix + "Email.IsHtml";

    public const string NginxAction = Prefix + "Nginx.Action";
    public const string NginxConfigPath = Prefix + "Nginx.ConfigPath";
    public const string NginxConfigBody = Prefix + "Nginx.ConfigBody";
    public const string NginxServiceName = Prefix + "Nginx.ServiceName";
    public const string NginxTestConfig = Prefix + "Nginx.TestConfig";

    public const string CertFilePath = Prefix + "Certificate.FilePath";
    public const string CertPassword = Prefix + "Certificate.Password";
    public const string CertStoreName = Prefix + "Certificate.StoreName";
    public const string CertStoreLocation = Prefix + "Certificate.StoreLocation";
    public const string CertThumbprint = Prefix + "Certificate.Thumbprint";

    public const string VhdSourcePath = Prefix + "Vhd.SourcePath";
    public const string VhdDestinationPath = Prefix + "Vhd.DestinationPath";
    public const string VhdAction = Prefix + "Vhd.Action";
}

public sealed class MiscStepHandler : IStepHandler
{
    private static readonly string[] _handledTypes =
    [
        "Octopus.Email",
        "Octopus.Nginx",
        "Octopus.Certificate.Import",
        "Octopus.Vhd",
    ];

    public bool CanHandle(string stepType)
        => _handledTypes.Any(t => t.Equals(stepType, StringComparison.OrdinalIgnoreCase));

    public bool RequiresPackage => false;

    public async Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct)
    {
        return context.Step.StepType.ToLowerInvariant() switch
        {
            "octopus.email" => await HandleEmailAsync(context, ct).ConfigureAwait(false),
            "octopus.nginx" => await HandleNginxAsync(context, ct).ConfigureAwait(false),
            "octopus.certificate.import" => await HandleCertificateImportAsync(context, ct).ConfigureAwait(false),
            "octopus.vhd" => await HandleVhdAsync(context, ct).ConfigureAwait(false),
            _ => false,
        };
    }

    private static async Task<bool> HandleEmailAsync(StepHandlerContext context, CancellationToken ct)
    {
        var smtpHost = Get(context, MiscConfigKeys.SmtpHost);
        var to = Get(context, MiscConfigKeys.EmailTo);
        var subject = Get(context, MiscConfigKeys.EmailSubject);
        var body = Get(context, MiscConfigKeys.EmailBody);

        if (string.IsNullOrWhiteSpace(smtpHost))
        {
            await context.LogAsync("error", "SmtpHost is required for Email step.").ConfigureAwait(false);
            return false;
        }

        if (string.IsNullOrWhiteSpace(to))
        {
            await context.LogAsync("error", "Email.To is required.").ConfigureAwait(false);
            return false;
        }

        var resolvedSubject = ResolveVariables(subject ?? "(no subject)", context.Plan.Variables);
        var resolvedBody = ResolveVariables(body ?? "", context.Plan.Variables);
        var from = Get(context, MiscConfigKeys.EmailFrom) ?? "kraken@localhost";
        var port = int.TryParse(Get(context, MiscConfigKeys.SmtpPort), out var p) ? p : 25;
        var useSsl = ParseBool(Get(context, MiscConfigKeys.SmtpUseSsl));
        var isHtml = ParseBool(Get(context, MiscConfigKeys.EmailIsHtml));

        await context.LogAsync("info",
            string.Create(CultureInfo.InvariantCulture, $"Sending email to {to} via {smtpHost}:{port}..."))
            .ConfigureAwait(false);

        try
        {
            using var client = new SmtpClient(smtpHost, port)
            {
                EnableSsl = useSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
            };

            var username = Get(context, MiscConfigKeys.SmtpUsername);
            var password = Get(context, MiscConfigKeys.SmtpPassword);
            if (!string.IsNullOrWhiteSpace(username))
            {
                client.Credentials = new NetworkCredential(username, password);
            }

            using var message = new MailMessage
            {
                From = new MailAddress(from),
                Subject = resolvedSubject,
                Body = resolvedBody,
                IsBodyHtml = isHtml,
            };

            foreach (var recipient in SplitAddresses(to))
            {
                message.To.Add(recipient);
            }

            var cc = Get(context, MiscConfigKeys.EmailCc);
            if (!string.IsNullOrWhiteSpace(cc))
            {
                foreach (var recipient in SplitAddresses(cc))
                {
                    message.CC.Add(recipient);
                }
            }

            var bcc = Get(context, MiscConfigKeys.EmailBcc);
            if (!string.IsNullOrWhiteSpace(bcc))
            {
                foreach (var recipient in SplitAddresses(bcc))
                {
                    message.Bcc.Add(recipient);
                }
            }

            await client.SendMailAsync(message, ct).ConfigureAwait(false);

            await context.LogAsync("info", "Email sent successfully.").ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            await context.LogAsync("error",
                string.Create(CultureInfo.InvariantCulture, $"Failed to send email: {ex.Message}"))
                .ConfigureAwait(false);
            return false;
        }
    }

    private static async Task<bool> HandleNginxAsync(StepHandlerContext context, CancellationToken ct)
    {
        var action = Get(context, MiscConfigKeys.NginxAction) ?? "reload";
        var configPath = Get(context, MiscConfigKeys.NginxConfigPath);
        var configBody = Get(context, MiscConfigKeys.NginxConfigBody);
        var serviceName = Get(context, MiscConfigKeys.NginxServiceName) ?? "nginx";
        var testConfig = Get(context, MiscConfigKeys.NginxTestConfig);

        if (!string.IsNullOrWhiteSpace(configBody) && !string.IsNullOrWhiteSpace(configPath))
        {
            var resolved = ResolveVariables(configBody, context.Plan.Variables);
            var dir = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(configPath, resolved, ct).ConfigureAwait(false);
            await context.LogAsync("info",
                string.Create(CultureInfo.InvariantCulture, $"Wrote nginx config to {configPath}."))
                .ConfigureAwait(false);
        }

        if (ParseBool(testConfig) || testConfig is null)
        {
            await context.LogAsync("info", "Testing nginx configuration...").ConfigureAwait(false);
            var testOk = await RunShellCommandAsync(
                "nginx -t", context.LogAsync, ct).ConfigureAwait(false);
            if (!testOk)
            {
                await context.LogAsync("error", "nginx configuration test failed.").ConfigureAwait(false);
                return false;
            }
        }

        await context.LogAsync("info",
            string.Create(CultureInfo.InvariantCulture, $"Nginx {action}..."))
            .ConfigureAwait(false);

        var command = action.ToLowerInvariant() switch
        {
            "reload" => OperatingSystem.IsWindows()
                ? $"Restart-Service -Name '{serviceName}'"
                : $"systemctl reload {serviceName}",
            "restart" => OperatingSystem.IsWindows()
                ? $"Restart-Service -Name '{serviceName}' -Force"
                : $"systemctl restart {serviceName}",
            "start" => OperatingSystem.IsWindows()
                ? $"Start-Service -Name '{serviceName}'"
                : $"systemctl start {serviceName}",
            "stop" => OperatingSystem.IsWindows()
                ? $"Stop-Service -Name '{serviceName}' -Force"
                : $"systemctl stop {serviceName}",
            _ => OperatingSystem.IsWindows()
                ? $"Restart-Service -Name '{serviceName}'"
                : $"systemctl reload {serviceName}",
        };

        return await RunShellCommandAsync(command, context.LogAsync, ct).ConfigureAwait(false);
    }

    private static async Task<bool> HandleCertificateImportAsync(
        StepHandlerContext context, CancellationToken ct)
    {
        var certPath = Get(context, MiscConfigKeys.CertFilePath);
        var storeName = Get(context, MiscConfigKeys.CertStoreName) ?? "My";
        var storeLocation = Get(context, MiscConfigKeys.CertStoreLocation) ?? "LocalMachine";
        var password = Get(context, MiscConfigKeys.CertPassword);

        if (string.IsNullOrWhiteSpace(certPath) && string.IsNullOrEmpty(context.ExtractDir))
        {
            await context.LogAsync("error",
                "Certificate.FilePath or a deployment package with a certificate is required.")
                .ConfigureAwait(false);
            return false;
        }

        var actualPath = certPath;
        if (string.IsNullOrWhiteSpace(actualPath) && !string.IsNullOrEmpty(context.ExtractDir))
        {
            actualPath = Directory.EnumerateFiles(context.ExtractDir, "*.pfx", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(context.ExtractDir, "*.p12", SearchOption.AllDirectories))
                .Concat(Directory.EnumerateFiles(context.ExtractDir, "*.cer", SearchOption.AllDirectories))
                .FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(actualPath) || !File.Exists(actualPath))
        {
            await context.LogAsync("error", "Certificate file not found.").ConfigureAwait(false);
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            await context.LogAsync("warning",
                "Certificate.Import to the Windows certificate store is only supported on Windows. " +
                "On Linux, consider using a script step with openssl.")
                .ConfigureAwait(false);
            return false;
        }

        var script = new StringBuilder();
        script.AppendLine(CultureInfo.InvariantCulture,
            $"$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(\"{actualPath}\", \"{password ?? ""}\", [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::MachineKeySet)");
        script.AppendLine(CultureInfo.InvariantCulture,
            $"$store = New-Object System.Security.Cryptography.X509Certificates.X509Store(\"{storeName}\", [System.Security.Cryptography.X509Certificates.StoreLocation]::{storeLocation})");
        script.AppendLine("$store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)");
        script.AppendLine("$store.Add($cert)");
        script.AppendLine("$store.Close()");
        script.AppendLine("Write-Host \"Certificate imported: $($cert.Thumbprint)\"");

        await context.LogAsync("info",
            string.Create(CultureInfo.InvariantCulture,
                $"Importing certificate '{Path.GetFileName(actualPath)}' into {storeLocation}\\{storeName}..."))
            .ConfigureAwait(false);

        var runner = new ScriptRunner();
        return await runner.RunAsync(
            script.ToString(), "PowerShell", ".", new Dictionary<string, string>(), context.LogAsync, ct)
            .ConfigureAwait(false);
    }

    private static async Task<bool> HandleVhdAsync(StepHandlerContext context, CancellationToken ct)
    {
        var action = Get(context, MiscConfigKeys.VhdAction) ?? "copy";
        var sourcePath = Get(context, MiscConfigKeys.VhdSourcePath);
        var destPath = Get(context, MiscConfigKeys.VhdDestinationPath);

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            if (!string.IsNullOrEmpty(context.ExtractDir))
            {
                sourcePath = Directory.EnumerateFiles(context.ExtractDir, "*.vhd", SearchOption.AllDirectories)
                    .Concat(Directory.EnumerateFiles(context.ExtractDir, "*.vhdx", SearchOption.AllDirectories))
                    .FirstOrDefault();
            }
        }

        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            await context.LogAsync("error", "VHD source file not found.").ConfigureAwait(false);
            return false;
        }

        if (string.IsNullOrWhiteSpace(destPath))
        {
            await context.LogAsync("error", "Vhd.DestinationPath is required.").ConfigureAwait(false);
            return false;
        }

        var destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        await context.LogAsync("info",
            string.Create(CultureInfo.InvariantCulture,
                $"VHD {action}: {Path.GetFileName(sourcePath)} → {destPath}"))
            .ConfigureAwait(false);

        if (action.Equals("copy", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourcePath, destPath, overwrite: true);
            await context.LogAsync("info", "VHD copied successfully.").ConfigureAwait(false);
            return true;
        }

        if (action.Equals("expand", StringComparison.OrdinalIgnoreCase) && OperatingSystem.IsWindows())
        {
            var script = $"Expand-VHD -Path \"{sourcePath}\" -DestinationPath \"{destPath}\"";
            var runner = new ScriptRunner();
            return await runner.RunAsync(
                script, "PowerShell", ".", new Dictionary<string, string>(), context.LogAsync, ct)
                .ConfigureAwait(false);
        }

        File.Copy(sourcePath, destPath, overwrite: true);
        await context.LogAsync("info", "VHD copied (expand not supported on this platform).").ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> RunShellCommandAsync(
        string command, Func<string, string, Task> onOutput, CancellationToken ct)
    {
        var runner = new ScriptRunner();
        var syntax = OperatingSystem.IsWindows() ? "PowerShell" : "Bash";
        return await runner.RunAsync(
            command, syntax, ".", new Dictionary<string, string>(), onOutput, ct)
            .ConfigureAwait(false);
    }

    private static string[] SplitAddresses(string raw)
        => raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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

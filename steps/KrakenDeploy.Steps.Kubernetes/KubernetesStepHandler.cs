using System.Globalization;
using System.Text;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Steps.Common;
using Octostache;

namespace KrakenDeploy.Steps.Kubernetes;

public static class KubernetesConfigKeys
{
    private const string Prefix = "Octopus.Action.Kubernetes.";

    public const string ClusterUrl = Prefix + "ClusterUrl";
    public const string Token = Prefix + "Token";
    public const string ClientCertificate = Prefix + "ClientCertificate";
    public const string ClientKey = Prefix + "ClientKey";
    public const string CACertificate = Prefix + "CACertificate";
    public const string Namespace = Prefix + "Namespace";
    public const string Context = Prefix + "Context";
    public const string KubeconfigPath = Prefix + "KubeconfigPath";

    public const string Yaml = Prefix + "Yaml";
    public const string YamlFiles = Prefix + "YamlFiles";

    public const string ResourceType = Prefix + "ResourceType";
    public const string ResourceName = Prefix + "ResourceName";
    public const string Image = Prefix + "Image";
    public const string Replicas = Prefix + "Replicas";
    public const string Ports = Prefix + "Ports";
    public const string EnvVars = Prefix + "EnvVars";
    public const string Selector = Prefix + "Selector";
    public const string Labels = Prefix + "Labels";
    public const string ServiceType = Prefix + "ServiceType";
    public const string Rules = Prefix + "Rules";
    public const string TlsSecretName = Prefix + "TlsSecretName";
    public const string DataEntries = Prefix + "DataEntries";
    public const string SecretType = Prefix + "SecretType";
    public const string Containers = Prefix + "Containers";

    public const string KustomizationDir = Prefix + "KustomizationDir";

    public const string HelmReleaseName = Prefix + "HelmReleaseName";
    public const string HelmChartPath = Prefix + "HelmChartPath";
    public const string HelmValues = Prefix + "HelmValues";
    public const string HelmAdditionalArgs = Prefix + "HelmAdditionalArgs";

    public const string ScriptBody = Prefix + "ScriptBody";
    public const string ScriptSyntax = Prefix + "ScriptSyntax";
}

public sealed class KubernetesStepHandler : IStepHandler
{
    private static readonly string[] _handledTypes =
    [
        "Octopus.KubernetesDeployRawYaml",
        "Octopus.KubernetesDeployContainers",
        "Octopus.KubernetesDeployService",
        "Octopus.KubernetesDeployIngress",
        "Octopus.KubernetesDeployConfigMap",
        "Octopus.KubernetesDeploySecret",
        "Octopus.Kubernetes.Kustomize",
        "Octopus.HelmChartUpgrade",
        "Octopus.KubernetesRunScript",
    ];

    public bool CanHandle(string stepType)
        => _handledTypes.Any(t => t.Equals(stepType, StringComparison.OrdinalIgnoreCase));

    public bool RequiresPackage => false;

    public async Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct)
    {
        var conn = KubernetesConnection.Resolve(context);
        if (conn.Error is not null)
        {
            await context.LogAsync("error", conn.Error).ConfigureAwait(false);
            return false;
        }

        return context.Step.StepType.ToLowerInvariant() switch
        {
            "octopus.kubernetesdeployrawyaml" => await HandleRawYamlAsync(context, conn, ct).ConfigureAwait(false),
            "octopus.kubernetesdeploycontainers" => await HandleDeployContainersAsync(context, conn, ct).ConfigureAwait(false),
            "octopus.kubernetesdeployservice" => await HandleDeployServiceAsync(context, conn, ct).ConfigureAwait(false),
            "octopus.kubernetesdeployingress" => await HandleDeployIngressAsync(context, conn, ct).ConfigureAwait(false),
            "octopus.kubernetesdeployconfigmap" => await HandleDeployConfigMapAsync(context, conn, ct).ConfigureAwait(false),
            "octopus.kubernetesdeploysecret" => await HandleDeploySecretAsync(context, conn, ct).ConfigureAwait(false),
            "octopus.kubernetes.kustomize" => await HandleKustomizeAsync(context, conn, ct).ConfigureAwait(false),
            "octopus.helmchartupgrade" => await HandleHelmAsync(context, conn, ct).ConfigureAwait(false),
            "octopus.kubernetesrunscript" => await HandleRunScriptAsync(context, conn, ct).ConfigureAwait(false),
            _ => false,
        };
    }

    private static async Task<bool> HandleRawYamlAsync(
        StepHandlerContext context, KubernetesConnection conn, CancellationToken ct)
    {
        var yaml = Get(context, KubernetesConfigKeys.Yaml);
        var yamlFiles = Get(context, KubernetesConfigKeys.YamlFiles);

        if (string.IsNullOrWhiteSpace(yaml) && string.IsNullOrWhiteSpace(yamlFiles)
            && string.IsNullOrEmpty(context.ExtractDir))
        {
            await context.LogAsync("error",
                "Provide inline YAML (Octopus.Action.Kubernetes.Yaml), file globs " +
                "(Octopus.Action.Kubernetes.YamlFiles), or a package containing YAML files.")
                .ConfigureAwait(false);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(yaml))
        {
            var resolved = ResolveVariables(yaml, context.Plan.Variables);
            var tempFile = Path.Combine(Path.GetTempPath(), $"kraken-k8s-{Guid.NewGuid():N}.yaml");
            await File.WriteAllTextAsync(tempFile, resolved, ct).ConfigureAwait(false);
            try
            {
                await context.LogAsync("info", "Applying inline YAML...").ConfigureAwait(false);
                return await KubectlCliRunner.RunAsync(
                    $"apply -f \"{tempFile}\"", ".", context.LogAsync, ct,
                    conn.KubeconfigPath, conn.Context, conn.Namespace).ConfigureAwait(false);
            }
            finally
            {
                File.Delete(tempFile);
            }
        }

        var searchDir = context.ExtractDir;
        var patterns = string.IsNullOrWhiteSpace(yamlFiles)
            ? ["*.yaml", "*.yml"]
            : yamlFiles.Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var files = new List<string>();
        foreach (var pattern in patterns)
        {
            files.AddRange(Directory.EnumerateFiles(searchDir, pattern, SearchOption.AllDirectories));
        }

        if (files.Count == 0)
        {
            await context.LogAsync("error", "No YAML files found to apply.").ConfigureAwait(false);
            return false;
        }

        await context.LogAsync("info",
            string.Create(CultureInfo.InvariantCulture, $"Applying {files.Count} YAML file(s)..."))
            .ConfigureAwait(false);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var ok = await KubectlCliRunner.RunAsync(
                $"apply -f \"{file}\"", ".", context.LogAsync, ct,
                conn.KubeconfigPath, conn.Context, conn.Namespace).ConfigureAwait(false);
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<bool> HandleDeployContainersAsync(
        StepHandlerContext context, KubernetesConnection conn, CancellationToken ct)
    {
        var name = Get(context, KubernetesConfigKeys.ResourceName);
        var image = Get(context, KubernetesConfigKeys.Image);

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(image))
        {
            await context.LogAsync("error",
                "ResourceName and Image are required for KubernetesDeployContainers.")
                .ConfigureAwait(false);
            return false;
        }

        var replicas = Get(context, KubernetesConfigKeys.Replicas) ?? "1";
        var ports = Get(context, KubernetesConfigKeys.Ports);
        var envVars = Get(context, KubernetesConfigKeys.EnvVars);
        var labels = Get(context, KubernetesConfigKeys.Labels) ?? $"app={name}";

        var yaml = new StringBuilder();
        yaml.AppendLine("apiVersion: apps/v1");
        yaml.AppendLine("kind: Deployment");
        yaml.AppendLine("metadata:");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"  name: {name}");
        yaml.AppendLine("  labels:");
        foreach (var label in SplitEntries(labels))
        {
            yaml.AppendLine(CultureInfo.InvariantCulture, $"    {label}");
        }

        yaml.AppendLine("spec:");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"  replicas: {replicas}");
        yaml.AppendLine("  selector:");
        yaml.AppendLine("    matchLabels:");
        foreach (var label in SplitEntries(labels))
        {
            yaml.AppendLine(CultureInfo.InvariantCulture, $"      {label}");
        }

        yaml.AppendLine("  template:");
        yaml.AppendLine("    metadata:");
        yaml.AppendLine("      labels:");
        foreach (var label in SplitEntries(labels))
        {
            yaml.AppendLine(CultureInfo.InvariantCulture, $"        {label}");
        }

        yaml.AppendLine("    spec:");
        yaml.AppendLine("      containers:");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"      - name: {name}");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"        image: {image}");

        if (!string.IsNullOrWhiteSpace(ports))
        {
            yaml.AppendLine("        ports:");
            foreach (var port in SplitEntries(ports))
            {
                yaml.AppendLine(CultureInfo.InvariantCulture, $"        - containerPort: {port}");
            }
        }

        if (!string.IsNullOrWhiteSpace(envVars))
        {
            yaml.AppendLine("        env:");
            foreach (var env in SplitEntries(envVars))
            {
                var eqIdx = env.IndexOf('=');
                if (eqIdx > 0)
                {
                    yaml.AppendLine(CultureInfo.InvariantCulture, $"        - name: {env[..eqIdx]}");
                    yaml.AppendLine(CultureInfo.InvariantCulture, $"          value: \"{env[(eqIdx + 1)..]}\"");
                }
            }
        }

        return await ApplyYamlAsync(yaml.ToString(), context, conn, ct).ConfigureAwait(false);
    }

    private static async Task<bool> HandleDeployServiceAsync(
        StepHandlerContext context, KubernetesConnection conn, CancellationToken ct)
    {
        var name = Get(context, KubernetesConfigKeys.ResourceName);
        if (string.IsNullOrWhiteSpace(name))
        {
            await context.LogAsync("error", "ResourceName is required for KubernetesDeployService.")
                .ConfigureAwait(false);
            return false;
        }

        var serviceType = Get(context, KubernetesConfigKeys.ServiceType) ?? "ClusterIP";
        var ports = Get(context, KubernetesConfigKeys.Ports);
        var selector = Get(context, KubernetesConfigKeys.Selector) ?? $"app={name}";

        var yaml = new StringBuilder();
        yaml.AppendLine("apiVersion: v1");
        yaml.AppendLine("kind: Service");
        yaml.AppendLine("metadata:");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"  name: {name}");
        yaml.AppendLine("spec:");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"  type: {serviceType}");
        yaml.AppendLine("  selector:");
        foreach (var sel in SplitEntries(selector))
        {
            var eqIdx = sel.IndexOf('=');
            if (eqIdx > 0)
            {
                yaml.AppendLine(CultureInfo.InvariantCulture, $"    {sel[..eqIdx]}: {sel[(eqIdx + 1)..]}");
            }
        }

        if (!string.IsNullOrWhiteSpace(ports))
        {
            yaml.AppendLine("  ports:");
            foreach (var port in SplitEntries(ports))
            {
                var parts = port.Split(':');
                yaml.AppendLine(CultureInfo.InvariantCulture, $"  - port: {parts[0]}");
                if (parts.Length > 1)
                {
                    yaml.AppendLine(CultureInfo.InvariantCulture, $"    targetPort: {parts[1]}");
                }
            }
        }

        return await ApplyYamlAsync(yaml.ToString(), context, conn, ct).ConfigureAwait(false);
    }

    private static async Task<bool> HandleDeployIngressAsync(
        StepHandlerContext context, KubernetesConnection conn, CancellationToken ct)
    {
        var name = Get(context, KubernetesConfigKeys.ResourceName);
        if (string.IsNullOrWhiteSpace(name))
        {
            await context.LogAsync("error", "ResourceName is required for KubernetesDeployIngress.")
                .ConfigureAwait(false);
            return false;
        }

        var rules = Get(context, KubernetesConfigKeys.Rules);
        var tlsSecret = Get(context, KubernetesConfigKeys.TlsSecretName);

        var yaml = new StringBuilder();
        yaml.AppendLine("apiVersion: networking.k8s.io/v1");
        yaml.AppendLine("kind: Ingress");
        yaml.AppendLine("metadata:");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"  name: {name}");
        yaml.AppendLine("spec:");

        if (!string.IsNullOrWhiteSpace(tlsSecret) && !string.IsNullOrWhiteSpace(rules))
        {
            var firstHost = SplitEntries(rules).FirstOrDefault()?.Split('|')[0];
            if (!string.IsNullOrEmpty(firstHost))
            {
                yaml.AppendLine("  tls:");
                yaml.AppendLine("  - hosts:");
                yaml.AppendLine(CultureInfo.InvariantCulture, $"    - {firstHost}");
                yaml.AppendLine(CultureInfo.InvariantCulture, $"    secretName: {tlsSecret}");
            }
        }

        yaml.AppendLine("  rules:");
        if (!string.IsNullOrWhiteSpace(rules))
        {
            foreach (var rule in SplitEntries(rules))
            {
                var parts = rule.Split('|');
                var host = parts[0];
                var path = parts.Length > 1 ? parts[1] : "/";
                var backend = parts.Length > 2 ? parts[2] : name;
                var backendPort = parts.Length > 3 ? parts[3] : "80";

                yaml.AppendLine(CultureInfo.InvariantCulture, $"  - host: {host}");
                yaml.AppendLine("    http:");
                yaml.AppendLine("      paths:");
                yaml.AppendLine(CultureInfo.InvariantCulture, $"      - path: {path}");
                yaml.AppendLine("        pathType: Prefix");
                yaml.AppendLine("        backend:");
                yaml.AppendLine("          service:");
                yaml.AppendLine(CultureInfo.InvariantCulture, $"            name: {backend}");
                yaml.AppendLine("            port:");
                yaml.AppendLine(CultureInfo.InvariantCulture, $"              number: {backendPort}");
            }
        }

        return await ApplyYamlAsync(yaml.ToString(), context, conn, ct).ConfigureAwait(false);
    }

    private static async Task<bool> HandleDeployConfigMapAsync(
        StepHandlerContext context, KubernetesConnection conn, CancellationToken ct)
    {
        var name = Get(context, KubernetesConfigKeys.ResourceName);
        if (string.IsNullOrWhiteSpace(name))
        {
            await context.LogAsync("error", "ResourceName is required for KubernetesDeployConfigMap.")
                .ConfigureAwait(false);
            return false;
        }

        var data = Get(context, KubernetesConfigKeys.DataEntries);

        var yaml = new StringBuilder();
        yaml.AppendLine("apiVersion: v1");
        yaml.AppendLine("kind: ConfigMap");
        yaml.AppendLine("metadata:");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"  name: {name}");
        yaml.AppendLine("data:");

        if (!string.IsNullOrWhiteSpace(data))
        {
            foreach (var entry in SplitEntries(data))
            {
                var eqIdx = entry.IndexOf('=');
                if (eqIdx > 0)
                {
                    yaml.AppendLine(CultureInfo.InvariantCulture, $"  {entry[..eqIdx]}: \"{entry[(eqIdx + 1)..]}\"");
                }
            }
        }

        return await ApplyYamlAsync(yaml.ToString(), context, conn, ct).ConfigureAwait(false);
    }

    private static async Task<bool> HandleDeploySecretAsync(
        StepHandlerContext context, KubernetesConnection conn, CancellationToken ct)
    {
        var name = Get(context, KubernetesConfigKeys.ResourceName);
        if (string.IsNullOrWhiteSpace(name))
        {
            await context.LogAsync("error", "ResourceName is required for KubernetesDeploySecret.")
                .ConfigureAwait(false);
            return false;
        }

        var secretType = Get(context, KubernetesConfigKeys.SecretType) ?? "Opaque";
        var data = Get(context, KubernetesConfigKeys.DataEntries);

        var yaml = new StringBuilder();
        yaml.AppendLine("apiVersion: v1");
        yaml.AppendLine("kind: Secret");
        yaml.AppendLine("metadata:");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"  name: {name}");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"type: {secretType}");
        yaml.AppendLine("stringData:");

        if (!string.IsNullOrWhiteSpace(data))
        {
            foreach (var entry in SplitEntries(data))
            {
                var eqIdx = entry.IndexOf('=');
                if (eqIdx > 0)
                {
                    yaml.AppendLine(CultureInfo.InvariantCulture, $"  {entry[..eqIdx]}: \"{entry[(eqIdx + 1)..]}\"");
                }
            }
        }

        return await ApplyYamlAsync(yaml.ToString(), context, conn, ct).ConfigureAwait(false);
    }

    private static async Task<bool> HandleKustomizeAsync(
        StepHandlerContext context, KubernetesConnection conn, CancellationToken ct)
    {
        var kustomizationDir = Get(context, KubernetesConfigKeys.KustomizationDir);
        if (string.IsNullOrWhiteSpace(kustomizationDir))
        {
            kustomizationDir = context.ExtractDir;
        }

        if (string.IsNullOrWhiteSpace(kustomizationDir) || !Directory.Exists(kustomizationDir))
        {
            await context.LogAsync("error",
                "KustomizationDir must point to a directory containing a kustomization.yaml.")
                .ConfigureAwait(false);
            return false;
        }

        await context.LogAsync("info",
            string.Create(CultureInfo.InvariantCulture, $"Applying kustomization from {kustomizationDir}..."))
            .ConfigureAwait(false);

        return await KubectlCliRunner.RunAsync(
            $"apply -k \"{kustomizationDir}\"", ".", context.LogAsync, ct,
            conn.KubeconfigPath, conn.Context, conn.Namespace).ConfigureAwait(false);
    }

    private static async Task<bool> HandleHelmAsync(
        StepHandlerContext context, KubernetesConnection conn, CancellationToken ct)
    {
        var releaseName = Get(context, KubernetesConfigKeys.HelmReleaseName);
        var chartPath = Get(context, KubernetesConfigKeys.HelmChartPath);

        if (string.IsNullOrWhiteSpace(releaseName) || string.IsNullOrWhiteSpace(chartPath))
        {
            await context.LogAsync("error",
                "HelmReleaseName and HelmChartPath are required for HelmChartUpgrade.")
                .ConfigureAwait(false);
            return false;
        }

        var sb = new StringBuilder("upgrade --install");
        sb.Append(CultureInfo.InvariantCulture, $" {releaseName}");
        sb.Append(CultureInfo.InvariantCulture, $" {chartPath}");

        if (!string.IsNullOrEmpty(conn.Namespace))
        {
            sb.Append(CultureInfo.InvariantCulture, $" --namespace {conn.Namespace}");
        }

        var values = Get(context, KubernetesConfigKeys.HelmValues);
        if (!string.IsNullOrWhiteSpace(values))
        {
            var tempValues = Path.Combine(Path.GetTempPath(), $"kraken-helm-values-{Guid.NewGuid():N}.yaml");
            await File.WriteAllTextAsync(tempValues, ResolveVariables(values, context.Plan.Variables), ct)
                .ConfigureAwait(false);
            try
            {
                sb.Append(CultureInfo.InvariantCulture, $" -f \"{tempValues}\"");
                await context.LogAsync("info",
                    string.Create(CultureInfo.InvariantCulture, $"Helm upgrade --install {releaseName} {chartPath}..."))
                    .ConfigureAwait(false);
                return await KubectlCliRunner.RunHelmAsync(
                    sb.ToString(), ".", context.LogAsync, ct,
                    conn.KubeconfigPath, conn.Context).ConfigureAwait(false);
            }
            finally
            {
                File.Delete(tempValues);
            }
        }

        var additional = Get(context, KubernetesConfigKeys.HelmAdditionalArgs);
        if (!string.IsNullOrWhiteSpace(additional))
        {
            sb.Append(CultureInfo.InvariantCulture, $" {additional}");
        }

        await context.LogAsync("info",
            string.Create(CultureInfo.InvariantCulture, $"Helm upgrade --install {releaseName} {chartPath}..."))
            .ConfigureAwait(false);
        return await KubectlCliRunner.RunHelmAsync(
            sb.ToString(), ".", context.LogAsync, ct,
            conn.KubeconfigPath, conn.Context).ConfigureAwait(false);
    }

    private static async Task<bool> HandleRunScriptAsync(
        StepHandlerContext context, KubernetesConnection conn, CancellationToken ct)
    {
        var scriptBody = Get(context, KubernetesConfigKeys.ScriptBody);
        if (string.IsNullOrWhiteSpace(scriptBody))
        {
            await context.LogAsync("error", "ScriptBody is required for KubernetesRunScript.")
                .ConfigureAwait(false);
            return false;
        }

        var syntax = Get(context, KubernetesConfigKeys.ScriptSyntax) ?? "Bash";
        var resolved = ResolveVariables(scriptBody, context.Plan.Variables);

        var envVars = new Dictionary<string, string>
        {
            ["KUBECONFIG"] = conn.KubeconfigPath ?? "",
        };

        if (!string.IsNullOrEmpty(conn.Context))
        {
            envVars["KUBECTL_CONTEXT"] = conn.Context;
        }

        if (!string.IsNullOrEmpty(conn.Namespace))
        {
            envVars["KUBECTL_NAMESPACE"] = conn.Namespace;
        }

        var runner = new ScriptRunner();
        return await runner.RunAsync(
            resolved, syntax, context.ExtractDir, envVars, context.LogAsync, ct)
            .ConfigureAwait(false);
    }

    private static async Task<bool> ApplyYamlAsync(
        string yaml, StepHandlerContext context, KubernetesConnection conn, CancellationToken ct)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"kraken-k8s-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(tempFile, yaml, ct).ConfigureAwait(false);
        try
        {
            return await KubectlCliRunner.RunAsync(
                $"apply -f \"{tempFile}\"", ".", context.LogAsync, ct,
                conn.KubeconfigPath, conn.Context, conn.Namespace).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(tempFile);
        }
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

    private static string[] SplitEntries(string raw)
        => raw.Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? Get(StepHandlerContext context, string key)
        => context.Step.Config.GetValueOrDefault(key);
}

internal sealed class KubernetesConnection
{
    public string? KubeconfigPath { get; init; }
    public string? Context { get; init; }
    public string? Namespace { get; init; }
    public string? Error { get; init; }

    public static KubernetesConnection Resolve(StepHandlerContext context)
    {
        var config = context.Step.Config;
        var explicitPath = config.GetValueOrDefault(KubernetesConfigKeys.KubeconfigPath);
        var clusterUrl = config.GetValueOrDefault(KubernetesConfigKeys.ClusterUrl);

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return new KubernetesConnection
            {
                KubeconfigPath = explicitPath,
                Context = config.GetValueOrDefault(KubernetesConfigKeys.Context),
                Namespace = config.GetValueOrDefault(KubernetesConfigKeys.Namespace),
            };
        }

        if (!string.IsNullOrWhiteSpace(clusterUrl))
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"kraken-kube-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var kubeconfig = KubectlCliRunner.WriteTemporaryKubeconfig(
                clusterUrl,
                config.GetValueOrDefault(KubernetesConfigKeys.Token),
                config.GetValueOrDefault(KubernetesConfigKeys.ClientCertificate),
                config.GetValueOrDefault(KubernetesConfigKeys.ClientKey),
                config.GetValueOrDefault(KubernetesConfigKeys.CACertificate),
                tempDir);

            return new KubernetesConnection
            {
                KubeconfigPath = kubeconfig,
                Context = "kraken-context",
                Namespace = config.GetValueOrDefault(KubernetesConfigKeys.Namespace),
            };
        }

        var envKubeconfig = Environment.GetEnvironmentVariable("KUBECONFIG");
        if (!string.IsNullOrWhiteSpace(envKubeconfig))
        {
            return new KubernetesConnection
            {
                KubeconfigPath = envKubeconfig,
                Context = config.GetValueOrDefault(KubernetesConfigKeys.Context),
                Namespace = config.GetValueOrDefault(KubernetesConfigKeys.Namespace),
            };
        }

        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kube", "config");
        if (File.Exists(defaultPath))
        {
            return new KubernetesConnection
            {
                KubeconfigPath = defaultPath,
                Context = config.GetValueOrDefault(KubernetesConfigKeys.Context),
                Namespace = config.GetValueOrDefault(KubernetesConfigKeys.Namespace),
            };
        }

        return new KubernetesConnection
        {
            Error = "No Kubernetes connection configured. Set ClusterUrl + credentials, " +
                "KubeconfigPath, or ensure ~/.kube/config exists on the agent.",
        };
    }
}

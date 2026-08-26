namespace KrakenDeploy.Server.Core.Domain.Settings;

/// <summary>Where an effective setting value originated.</summary>
public enum SettingValueSource
{
    Default,
    ConfigurationFile,
    Database,
}

/// <summary>A resolved value together with its winning configuration layer.</summary>
public sealed record EffectiveSetting<T>(T Value, SettingValueSource Source);

/// <summary>Server-wide orchestration limits.</summary>
public sealed class EngineSettings : ISettingsDocument
{
    public static string Key => "engine";
    public static SettingsScope Scope => SettingsScope.System;

    public int MaxConcurrentTasks { get; set; } = 20;
    public int DefaultTargetWaveMaxParallelism { get; set; } = 10;
    public TimeSpan MaxTargetWaveDuration { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan MaxTargetQueueWait { get; set; } = TimeSpan.FromHours(2);
    public TimeSpan AgentDisconnectWaveGrace { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan MaxDeployReleaseWaitDuration { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan DefaultInterventionTimeout { get; set; } = TimeSpan.FromHours(72);
    public TimeSpan MaxDeployReleaseGatedWaitDuration { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>Server-wide authentication, logging, and telemetry settings.</summary>
public sealed class OperationalSettings : ISettingsDocument
{
    public static string Key => "operational";
    public static SettingsScope Scope => SettingsScope.System;

    public int AuthSessionRevalidationMinutes { get; set; } = 15;
    public string ServerBaseUrl { get; set; } = "";
    public int AgentTokenLifetimeDays { get; set; } = 90;
    public string SerilogMinimumLevel { get; set; } = "Information";
    public Dictionary<string, string> SerilogCategoryOverrides { get; set; } = new(StringComparer.Ordinal)
    {
        ["Microsoft"] = "Warning",
        ["Microsoft.AspNetCore"] = "Warning",
        ["Microsoft.EntityFrameworkCore"] = "Warning",
        ["System.Net.Http.HttpClient"] = "Warning",
    };
    public bool OtelEnabled { get; set; }
    public string OtelEndpoint { get; set; } = "";
    public string OtelProtocol { get; set; } = "grpc";
    public string SeqServerUrl { get; set; } = "";
}

/// <summary>One editable GitHub step-template feed.</summary>
public sealed class CatalogFeedSettings
{
    public string Owner { get; set; } = "";
    public string Repo { get; set; } = "";
    public string Branch { get; set; } = "main";
    public string SubDir { get; set; } = "step-templates";
}

/// <summary>Server-wide package and template catalog settings.</summary>
public sealed class CatalogSettings : ISettingsDocument
{
    public static string Key => "catalog";
    public static SettingsScope Scope => SettingsScope.System;

    public bool PackageCatalogEnabled { get; set; } = true;
    public string PackageCatalogOwner { get; set; } = "DomagojJugovich";
    public string PackageCatalogRepo { get; set; } = "kraken-steps";
    public bool TemplateCatalogEnabled { get; set; } = true;
    public List<CatalogFeedSettings> TemplateCatalogFeeds { get; set; } =
    [
        new() { Owner = "OctopusDeploy", Repo = "Library", Branch = "master" },
        new() { Owner = "DomagojJugovich", Repo = "kraken-steps", Branch = "main" },
    ];

    /// <summary>AES-256-GCM ciphertext. It must never be returned to UI callers.</summary>
    public string? GitHubTokenEncrypted { get; set; }
}

/// <summary>Persistable policy shape compatible with the outbound SSRF guard.</summary>
public sealed class SsrfPolicySettings
{
    public bool AllowLoopback { get; set; }
    public bool AllowPrivate { get; set; }
    public string[] AllowedHosts { get; set; } = [];
}

/// <summary>Server-wide policies for each outbound integration.</summary>
public sealed class SsrfSettings : ISettingsDocument
{
    public static string Key => "ssrf";
    public static SettingsScope Scope => SettingsScope.System;

    public SsrfPolicySettings Webhook { get; set; } = new();
    public SsrfPolicySettings StepCatalog { get; set; } = new();
    public SsrfPolicySettings Oidc { get; set; } = new();
    public SsrfPolicySettings Ai { get; set; } = new() { AllowLoopback = true };
}

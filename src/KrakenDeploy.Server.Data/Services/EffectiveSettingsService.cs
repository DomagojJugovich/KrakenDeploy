using System.Net;
using KrakenDeploy.Server.Core.Domain.Platform;
using KrakenDeploy.Server.Core.Domain.Settings;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Resolves editable system settings. Ordinary settings use database, then
/// configuration, then document defaults. SSRF reverses the first two layers so
/// every present configuration property is an immutable safety pin.
/// </summary>
public sealed class EffectiveSettingsService(
    SettingsService settings,
    IConfiguration configuration,
    IEncryptionService encryption,
    DeploymentOptions deploymentOptions)
{
    public async Task<EffectiveEngineSettings> GetEngineAsync(CancellationToken ct = default)
    {
        var defaults = new EngineSettings();
        var database = IsMultiAccount
            ? null
            : await settings.TryGetAsync<EngineSettings>(ct: ct).ConfigureAwait(false);
        return new EffectiveEngineSettings
        {
            MaxConcurrentTasks = Resolve(database, d => d.MaxConcurrentTasks, "Engine:MaxConcurrentTasks", defaults.MaxConcurrentTasks),
            DefaultTargetWaveMaxParallelism = Resolve(database, d => d.DefaultTargetWaveMaxParallelism, "Engine:DefaultTargetWaveMaxParallelism", defaults.DefaultTargetWaveMaxParallelism),
            MaxTargetWaveDuration = Resolve(database, d => d.MaxTargetWaveDuration, "Engine:MaxTargetWaveDuration", defaults.MaxTargetWaveDuration),
            MaxTargetQueueWait = Resolve(database, d => d.MaxTargetQueueWait, "Engine:MaxTargetQueueWait", defaults.MaxTargetQueueWait),
            AgentDisconnectWaveGrace = Resolve(database, d => d.AgentDisconnectWaveGrace, "Engine:AgentDisconnectWaveGrace", defaults.AgentDisconnectWaveGrace),
            MaxDeployReleaseWaitDuration = Resolve(database, d => d.MaxDeployReleaseWaitDuration, "Engine:MaxDeployReleaseWaitDuration", defaults.MaxDeployReleaseWaitDuration),
            DefaultInterventionTimeout = Resolve(database, d => d.DefaultInterventionTimeout, "Engine:DefaultInterventionTimeout", defaults.DefaultInterventionTimeout),
            MaxDeployReleaseGatedWaitDuration = Resolve(database, d => d.MaxDeployReleaseGatedWaitDuration, "Engine:MaxDeployReleaseGatedWaitDuration", defaults.MaxDeployReleaseGatedWaitDuration),
        };
    }

    public async Task SaveEngineAsync(EngineSettings document, CancellationToken ct = default)
    {
        EnsureHostSettingsEditable();
        ValidateEngine(document);
        await settings.SaveAsync(document, ct: ct).ConfigureAwait(false);
    }

    public async Task<EffectiveOperationalSettings> GetOperationalAsync(CancellationToken ct = default)
    {
        var defaults = new OperationalSettings();
        var database = IsMultiAccount
            ? null
            : await settings.TryGetAsync<OperationalSettings>(ct: ct).ConfigureAwait(false);
        return new EffectiveOperationalSettings
        {
            AuthSessionRevalidationMinutes = Resolve(database, d => d.AuthSessionRevalidationMinutes, "Auth:SessionRevalidationMinutes", defaults.AuthSessionRevalidationMinutes),
            ServerBaseUrl = Resolve(database, d => d.ServerBaseUrl, "Server:BaseUrl", defaults.ServerBaseUrl),
            AgentTokenLifetimeDays = Resolve(database, d => d.AgentTokenLifetimeDays, "Agent:TokenLifetimeDays", defaults.AgentTokenLifetimeDays),
            SerilogMinimumLevel = Resolve(database, d => d.SerilogMinimumLevel, "Serilog:MinimumLevel:Default", defaults.SerilogMinimumLevel),
            SerilogCategoryOverrides = ResolveSection(database, d => d.SerilogCategoryOverrides, "Serilog:MinimumLevel:Override", defaults.SerilogCategoryOverrides),
            OtelEnabled = Resolve(database, d => d.OtelEnabled, "Otel:Enabled", defaults.OtelEnabled),
            OtelEndpoint = Resolve(database, d => d.OtelEndpoint, "Otel:OtlpEndpoint", defaults.OtelEndpoint),
            OtelProtocol = Resolve(database, d => d.OtelProtocol, "Otel:Protocol", defaults.OtelProtocol),
            SeqServerUrl = Resolve(database, d => d.SeqServerUrl, "Otel:SeqServerUrl", defaults.SeqServerUrl),
        };
    }

    public async Task SaveOperationalAsync(OperationalSettings document, CancellationToken ct = default)
    {
        EnsureHostSettingsEditable();
        ValidateOperational(document);
        await settings.SaveAsync(document, ct: ct).ConfigureAwait(false);
    }

    public async Task<EffectiveCatalogSettings> GetCatalogAsync(CancellationToken ct = default)
    {
        var defaults = new CatalogSettings();
        var database = await settings.TryGetAsync<CatalogSettings>(ct: ct).ConfigureAwait(false);
        var tokenFromConfig = configuration["GitHub:Token"];
        return new EffectiveCatalogSettings
        {
            PackageCatalogEnabled = Resolve(database, d => d.PackageCatalogEnabled, "StepPackages:Catalog:Enabled", defaults.PackageCatalogEnabled),
            PackageCatalogOwner = Resolve(database, d => d.PackageCatalogOwner, "StepPackages:Catalog:Owner", defaults.PackageCatalogOwner),
            PackageCatalogRepo = Resolve(database, d => d.PackageCatalogRepo, "StepPackages:Catalog:Repo", defaults.PackageCatalogRepo),
            TemplateCatalogEnabled = Resolve(database, d => d.TemplateCatalogEnabled, "StepTemplates:Catalog:Enabled", defaults.TemplateCatalogEnabled),
            TemplateCatalogFeeds = ResolveSection(database, d => d.TemplateCatalogFeeds, "StepTemplates:Catalog:Feeds", defaults.TemplateCatalogFeeds),
            HasGitHubToken = database?.GitHubTokenEncrypted is not null
                ? new(database.GitHubTokenEncrypted.Length > 0, SettingValueSource.Database)
                : IsConfigured("GitHub:Token")
                    ? new(!string.IsNullOrWhiteSpace(tokenFromConfig), SettingValueSource.ConfigurationFile)
                    : new(false, SettingValueSource.Default),
        };
    }

    /// <summary>Returns plaintext only to an internal catalog consumer; never ciphertext.</summary>
    public async Task<string?> GetGitHubTokenAsync(CancellationToken ct = default)
    {
        var database = await settings.TryGetAsync<CatalogSettings>(ct: ct).ConfigureAwait(false);
        if (database?.GitHubTokenEncrypted is not null)
        {
            return database.GitHubTokenEncrypted.Length == 0
                ? null
                : encryption.Decrypt(database.GitHubTokenEncrypted);
        }
        return configuration["GitHub:Token"];
    }

    public async Task SaveCatalogAsync(CatalogSettingsUpdate input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateCatalog(input);
        await settings.MutateAsync<CatalogSettings>(null, current =>
        {
            current.PackageCatalogEnabled = input.PackageCatalogEnabled;
            current.PackageCatalogOwner = input.PackageCatalogOwner.Trim();
            current.PackageCatalogRepo = input.PackageCatalogRepo.Trim();
            current.TemplateCatalogEnabled = input.TemplateCatalogEnabled;
            current.TemplateCatalogFeeds = input.TemplateCatalogFeeds.Select(CloneAndTrim).ToList();
            if (input.ClearGitHubToken)
            {
                current.GitHubTokenEncrypted = "";
            }
            else if (!string.IsNullOrWhiteSpace(input.GitHubToken))
            {
                current.GitHubTokenEncrypted = encryption.Encrypt(input.GitHubToken);
            }
            return current;
        }, ct).ConfigureAwait(false);
    }

    public async Task<EffectiveSsrfSettings> GetSsrfAsync(CancellationToken ct = default)
    {
        var defaults = new SsrfSettings();
        var database = IsMultiAccount
            ? null
            : await settings.TryGetAsync<SsrfSettings>(ct: ct).ConfigureAwait(false);
        return new EffectiveSsrfSettings
        {
            Webhook = ResolveSsrfPolicy("Webhook", database?.Webhook, defaults.Webhook),
            StepCatalog = ResolveSsrfPolicy("StepCatalog", database?.StepCatalog, defaults.StepCatalog),
            Oidc = ResolveSsrfPolicy("Oidc", database?.Oidc, defaults.Oidc),
            Ai = ResolveSsrfPolicy("Ai", database?.Ai, defaults.Ai),
        };
    }

    /// <summary>
    /// Resolves the immutable SSRF options snapshot used by startup-built HTTP
    /// handlers and OIDC backchannels.
    /// </summary>
    public async Task<IOptions<SsrfOptions>> GetSsrfOptionsSnapshotAsync(CancellationToken ct = default)
        => Options.Create((await GetSsrfAsync(ct).ConfigureAwait(false)).ToSsrfOptions());

    public async Task SaveSsrfAsync(SsrfSettings document, CancellationToken ct = default)
    {
        EnsureHostSettingsEditable();
        ArgumentNullException.ThrowIfNull(document);
        ValidateSsrfPolicy(nameof(document.Webhook), document.Webhook);
        ValidateSsrfPolicy(nameof(document.StepCatalog), document.StepCatalog);
        ValidateSsrfPolicy(nameof(document.Oidc), document.Oidc);
        ValidateSsrfPolicy(nameof(document.Ai), document.Ai);
        await settings.MutateAsync<SsrfSettings>(null, current =>
        {
            CopyUnpinnedPolicy("Webhook", document.Webhook, current.Webhook);
            CopyUnpinnedPolicy("StepCatalog", document.StepCatalog, current.StepCatalog);
            CopyUnpinnedPolicy("Oidc", document.Oidc, current.Oidc);
            CopyUnpinnedPolicy("Ai", document.Ai, current.Ai);
            return current;
        }, ct).ConfigureAwait(false);
    }

    private void CopyUnpinnedPolicy(
        string name, SsrfPolicySettings source, SsrfPolicySettings destination)
    {
        var prefix = $"Ssrf:{name}";
        if (!IsConfigured($"{prefix}:AllowLoopback"))
        {
            destination.AllowLoopback = source.AllowLoopback;
        }
        if (!IsConfigured($"{prefix}:AllowPrivate"))
        {
            destination.AllowPrivate = source.AllowPrivate;
        }
        if (!IsConfigured($"{prefix}:AllowedHosts"))
        {
            destination.AllowedHosts = [.. source.AllowedHosts];
        }
    }

    private EffectiveSsrfPolicy ResolveSsrfPolicy(string name, SsrfPolicySettings? database, SsrfPolicySettings defaults)
    {
        var prefix = $"Ssrf:{name}";
        return new EffectiveSsrfPolicy
        {
            AllowLoopback = ResolvePinned(database, d => d.AllowLoopback, $"{prefix}:AllowLoopback", defaults.AllowLoopback),
            AllowPrivate = ResolvePinned(database, d => d.AllowPrivate, $"{prefix}:AllowPrivate", defaults.AllowPrivate),
            AllowedHosts = ResolvePinnedSection(database, d => d.AllowedHosts, $"{prefix}:AllowedHosts", defaults.AllowedHosts),
        };
    }

    private EffectiveSetting<TValue> Resolve<TDocument, TValue>(TDocument? database, Func<TDocument, TValue> select, string key, TValue defaultValue)
        where TDocument : class
    {
        if (database is not null)
        {
            return new(select(database), SettingValueSource.Database);
        }
        if (IsConfigured(key))
        {
            return new(ReadScalar<TValue>(key), SettingValueSource.ConfigurationFile);
        }
        return new(defaultValue, SettingValueSource.Default);
    }

    private EffectiveSetting<TValue> ResolveSection<TDocument, TValue>(TDocument? database, Func<TDocument, TValue> select, string key, TValue defaultValue)
        where TDocument : class
    {
        if (database is not null)
        {
            return new(select(database), SettingValueSource.Database);
        }
        var section = configuration.GetSection(key);
        return IsConfigured(key)
            ? new(ReadSection<TValue>(section), SettingValueSource.ConfigurationFile)
            : new(defaultValue, SettingValueSource.Default);
    }

    private EffectiveSetting<TValue> ResolvePinned<TDocument, TValue>(TDocument? database, Func<TDocument, TValue> select, string key, TValue defaultValue)
        where TDocument : class
    {
        if (IsConfigured(key))
        {
            return new(ReadScalar<TValue>(key), SettingValueSource.ConfigurationFile);
        }
        return database is not null
            ? new(select(database), SettingValueSource.Database)
            : new(defaultValue, SettingValueSource.Default);
    }

    private EffectiveSetting<TValue> ResolvePinnedSection<TDocument, TValue>(TDocument? database, Func<TDocument, TValue> select, string key, TValue defaultValue)
        where TDocument : class
    {
        var section = configuration.GetSection(key);
        if (IsConfigured(key))
        {
            return new(ReadSection<TValue>(section), SettingValueSource.ConfigurationFile);
        }
        return database is not null
            ? new(select(database), SettingValueSource.Database)
            : new(defaultValue, SettingValueSource.Default);
    }

    private bool IsConfigured(string key)
    {
        if (configuration.GetSection(key).GetChildren().Any())
        {
            return true;
        }
        return configuration is IConfigurationRoot root
            ? root.Providers.Any(provider => provider.TryGet(key, out _))
            : configuration[key] is not null;
    }

    // BG1/T2: keyed on the topology, not the removed MultiAccount:Enabled config
    // key (a config still carrying that key fails boot). The concern is TENANCY —
    // under Saas one tenant must not change process-wide policy — so only Saas
    // makes host-wide settings configuration-only.
    private bool IsMultiAccount => deploymentOptions.Topology == DeploymentTopology.Saas;

    private void EnsureHostSettingsEditable()
    {
        if (IsMultiAccount)
        {
            throw new InvalidOperationException(
                "Host-wide Engine, operational, and SSRF settings are configuration-only " +
                "in multi-account mode; one tenant cannot change process-wide policy.");
        }
    }

    private TValue ReadScalar<TValue>(string key)
    {
        _ = configuration[key]
            ?? throw new InvalidOperationException($"Configuration key '{key}' is present but has no value.");
        return configuration.GetValue<TValue>(key)!;
    }

    private static TValue ReadSection<TValue>(IConfigurationSection section)
    {
        var value = section.Get<TValue>();
        if (value is not null)
        {
            return value;
        }
        if (typeof(TValue).IsArray)
        {
            return (TValue)(object)Array.CreateInstance(typeof(TValue).GetElementType()!, 0);
        }
        if (Activator.CreateInstance<TValue>() is { } empty)
        {
            return empty;
        }
        throw new InvalidOperationException($"Configuration section '{section.Path}' could not be bound.");
    }

    public static void ValidateEngine(EngineSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.MaxConcurrentTasks <= 0)
        {
            throw new ArgumentException("MaxConcurrentTasks must be positive.");
        }
        if (value.DefaultTargetWaveMaxParallelism <= 0)
        {
            throw new ArgumentException("DefaultTargetWaveMaxParallelism must be positive.");
        }
        CheckDuration(value.MaxTargetWaveDuration, nameof(value.MaxTargetWaveDuration));
        CheckDuration(value.MaxTargetQueueWait, nameof(value.MaxTargetQueueWait));
        CheckDuration(value.AgentDisconnectWaveGrace, nameof(value.AgentDisconnectWaveGrace));
        CheckDuration(value.MaxDeployReleaseWaitDuration, nameof(value.MaxDeployReleaseWaitDuration));
        CheckDuration(value.DefaultInterventionTimeout, nameof(value.DefaultInterventionTimeout));
        CheckDuration(value.MaxDeployReleaseGatedWaitDuration, nameof(value.MaxDeployReleaseGatedWaitDuration));
        if (value.AgentDisconnectWaveGrace <= TimeSpan.FromSeconds(30))
        {
            throw new ArgumentException("AgentDisconnectWaveGrace must be greater than 30 seconds.");
        }
        if (value.AgentDisconnectWaveGrace >= value.MaxTargetWaveDuration + value.MaxTargetQueueWait)
        {
            throw new ArgumentException("AgentDisconnectWaveGrace must be less than the wave duration plus queue wait.");
        }
        if (value.MaxDeployReleaseGatedWaitDuration <= value.DefaultInterventionTimeout)
        {
            throw new ArgumentException("MaxDeployReleaseGatedWaitDuration must exceed DefaultInterventionTimeout.");
        }

        static void CheckDuration(TimeSpan duration, string name)
        {
            if (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(7))
            {
                throw new ArgumentException($"{name} must be positive and no greater than 7 days.");
            }
        }
    }

    public static void ValidateOperational(OperationalSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.AuthSessionRevalidationMinutes <= 0)
        {
            throw new ArgumentException("AuthSessionRevalidationMinutes must be positive.");
        }
        if (value.AgentTokenLifetimeDays is <= 0 or > 3650)
        {
            throw new ArgumentException("AgentTokenLifetimeDays must be between 1 and 3650.");
        }
        ValidateHttpUrlOrEmpty(value.ServerBaseUrl, nameof(value.ServerBaseUrl));
        ValidateHttpUrlOrEmpty(value.OtelEndpoint, nameof(value.OtelEndpoint));
        ValidateHttpUrlOrEmpty(value.SeqServerUrl, nameof(value.SeqServerUrl));
        ValidateLevel(value.SerilogMinimumLevel, nameof(value.SerilogMinimumLevel));
        if (value.SerilogCategoryOverrides is null)
        {
            throw new ArgumentException("SerilogCategoryOverrides is required.");
        }
        foreach (var (category, level) in value.SerilogCategoryOverrides)
        {
            if (string.IsNullOrWhiteSpace(category))
            {
                throw new ArgumentException("Serilog override categories cannot be blank.");
            }
            ValidateLevel(level, $"SerilogCategoryOverrides[{category}]");
        }
        if (!string.Equals(value.OtelProtocol, "grpc", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(value.OtelProtocol, "http/protobuf", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("OtelProtocol must be 'grpc' or 'http/protobuf'.");
        }
    }

    private static void ValidateCatalog(CatalogSettingsUpdate value)
    {
        if (value.ClearGitHubToken && !string.IsNullOrWhiteSpace(value.GitHubToken))
        {
            throw new ArgumentException("Enter a new GitHub token or clear it, not both.");
        }
        if (string.IsNullOrWhiteSpace(value.PackageCatalogOwner) || string.IsNullOrWhiteSpace(value.PackageCatalogRepo))
        {
            throw new ArgumentException("Package catalog owner and repo are required.");
        }
        if (value.TemplateCatalogFeeds is null || value.TemplateCatalogFeeds.Count == 0)
        {
            throw new ArgumentException("At least one template catalog feed is required.");
        }
        foreach (var feed in value.TemplateCatalogFeeds)
        {
            if (feed is null || string.IsNullOrWhiteSpace(feed.Owner) || string.IsNullOrWhiteSpace(feed.Repo)
                || string.IsNullOrWhiteSpace(feed.Branch) || string.IsNullOrWhiteSpace(feed.SubDir))
            {
                throw new ArgumentException("Template feed owner, repo, branch, and subdirectory are required.");
            }
        }
    }

    private static CatalogFeedSettings CloneAndTrim(CatalogFeedSettings feed) => new()
    {
        Owner = feed.Owner.Trim(), Repo = feed.Repo.Trim(), Branch = feed.Branch.Trim(), SubDir = feed.SubDir.Trim(),
    };

    public static void ValidateSsrfPolicy(string name, SsrfPolicySettings policy)
    {
        if (policy is null)
        {
            throw new ArgumentException($"SSRF policy {name} is required.");
        }
        if (policy.AllowedHosts is null)
        {
            throw new ArgumentException($"SSRF policy {name} AllowedHosts is required.");
        }
        foreach (var raw in policy.AllowedHosts)
        {
            if (raw is null)
            {
                throw new ArgumentException($"SSRF policy {name} contains a null host.");
            }
            var entry = raw.Trim();
            if (entry.Length == 0)
            {
                throw new ArgumentException($"SSRF policy {name} contains a blank host.");
            }
            var slash = entry.IndexOf('/');
            if (slash >= 0)
            {
                if (entry.LastIndexOf('/') != slash || !IPAddress.TryParse(entry[..slash], out var network)
                    || !int.TryParse(entry[(slash + 1)..], out var prefix)
                    || prefix < 0 || prefix > network.GetAddressBytes().Length * 8)
                {
                    throw new ArgumentException($"SSRF policy {name} contains invalid CIDR '{entry}'.");
                }
                if (SsrfGuard.IsHardBlocked(network))
                {
                    throw new ArgumentException($"SSRF policy {name} cannot allowlist hard-blocked CIDR '{entry}'.");
                }
            }
            else if (IPAddress.TryParse(entry, out var address))
            {
                if (SsrfGuard.IsHardBlocked(address))
                {
                    throw new ArgumentException($"SSRF policy {name} cannot allowlist hard-blocked address '{entry}'.");
                }
            }
            else if (Uri.CheckHostName(entry) == UriHostNameType.Unknown)
            {
                throw new ArgumentException($"SSRF policy {name} contains invalid hostname '{entry}'.");
            }
        }
    }

    private static void ValidateHttpUrlOrEmpty(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"{name} must be empty or an absolute http(s) URL.");
        }
    }

    private static void ValidateLevel(string value, string name)
    {
        string[] levels = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];
        if (!levels.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{name} is not a valid Serilog level.");
        }
    }
}

public sealed class EffectiveEngineSettings
{
    public required EffectiveSetting<int> MaxConcurrentTasks { get; init; }
    public required EffectiveSetting<int> DefaultTargetWaveMaxParallelism { get; init; }
    public required EffectiveSetting<TimeSpan> MaxTargetWaveDuration { get; init; }
    public required EffectiveSetting<TimeSpan> MaxTargetQueueWait { get; init; }
    public required EffectiveSetting<TimeSpan> AgentDisconnectWaveGrace { get; init; }
    public required EffectiveSetting<TimeSpan> MaxDeployReleaseWaitDuration { get; init; }
    public required EffectiveSetting<TimeSpan> DefaultInterventionTimeout { get; init; }
    public required EffectiveSetting<TimeSpan> MaxDeployReleaseGatedWaitDuration { get; init; }
}

public sealed class EffectiveOperationalSettings
{
    public required EffectiveSetting<int> AuthSessionRevalidationMinutes { get; init; }
    public required EffectiveSetting<string> ServerBaseUrl { get; init; }
    public required EffectiveSetting<int> AgentTokenLifetimeDays { get; init; }
    public required EffectiveSetting<string> SerilogMinimumLevel { get; init; }
    public required EffectiveSetting<Dictionary<string, string>> SerilogCategoryOverrides { get; init; }
    public required EffectiveSetting<bool> OtelEnabled { get; init; }
    public required EffectiveSetting<string> OtelEndpoint { get; init; }
    public required EffectiveSetting<string> OtelProtocol { get; init; }
    public required EffectiveSetting<string> SeqServerUrl { get; init; }
}

public sealed class EffectiveCatalogSettings
{
    public required EffectiveSetting<bool> PackageCatalogEnabled { get; init; }
    public required EffectiveSetting<string> PackageCatalogOwner { get; init; }
    public required EffectiveSetting<string> PackageCatalogRepo { get; init; }
    public required EffectiveSetting<bool> TemplateCatalogEnabled { get; init; }
    public required EffectiveSetting<List<CatalogFeedSettings>> TemplateCatalogFeeds { get; init; }
    public required EffectiveSetting<bool> HasGitHubToken { get; init; }
}

/// <summary>UI edit shape. Blank token means preserve the stored encrypted token.</summary>
public sealed class CatalogSettingsUpdate
{
    public bool PackageCatalogEnabled { get; set; } = true;
    public string PackageCatalogOwner { get; set; } = "DomagojJugovich";
    public string PackageCatalogRepo { get; set; } = "kraken-steps";
    public bool TemplateCatalogEnabled { get; set; } = true;
    public List<CatalogFeedSettings> TemplateCatalogFeeds { get; set; } = [];
    public string GitHubToken { get; set; } = "";
    public bool ClearGitHubToken { get; set; }
}

public sealed class EffectiveSsrfPolicy
{
    public required EffectiveSetting<bool> AllowLoopback { get; init; }
    public required EffectiveSetting<bool> AllowPrivate { get; init; }
    public required EffectiveSetting<string[]> AllowedHosts { get; init; }
}

public sealed class EffectiveSsrfSettings
{
    public required EffectiveSsrfPolicy Webhook { get; init; }
    public required EffectiveSsrfPolicy StepCatalog { get; init; }
    public required EffectiveSsrfPolicy Oidc { get; init; }
    public required EffectiveSsrfPolicy Ai { get; init; }
}

using System.Text.Json;
using KrakenDeploy.Contracts.Steps;

namespace KrakenDeploy.Agent.Deployment.Iis;

/// <summary>
/// Step config keys for an <c>Octopus.IIS</c> step, mirroring Octopus's
/// <c>Octopus.Action.IISWebSite.*</c> namespace exactly. Used both to detect
/// shape (Octopus property bag vs Kraken property bag) and to read individual
/// values during the map to <see cref="KrakenIisConfig"/>.
/// </summary>
public static class OctopusIisConfigKeys
{
    private const string IisPrefix = "Octopus.Action.IISWebSite.";
    private const string PkgPrefix = "Octopus.Action.Package.";

    public const string DeploymentType                  = IisPrefix + "DeploymentType";
    public const string CreateOrUpdateWebSite           = IisPrefix + "CreateOrUpdateWebSite";
    public const string WebSiteName                     = IisPrefix + "WebSiteName";
    public const string WebRootType                     = IisPrefix + "WebRootType";
    public const string Bindings                        = IisPrefix + "Bindings";
    public const string ApplicationPoolName             = IisPrefix + "ApplicationPoolName";
    public const string ApplicationPoolFrameworkVersion = IisPrefix + "ApplicationPoolFrameworkVersion";
    public const string ApplicationPoolIdentityType     = IisPrefix + "ApplicationPoolIdentityType";
    public const string ApplicationPoolUsername         = IisPrefix + "ApplicationPoolUsername";
    public const string ApplicationPoolPassword         = IisPrefix + "ApplicationPoolPassword";
    public const string EnableAnonymousAuth             = IisPrefix + "EnableAnonymousAuthentication";
    public const string EnableBasicAuth                 = IisPrefix + "EnableBasicAuthentication";
    public const string EnableWindowsAuth               = IisPrefix + "EnableWindowsAuthentication";
    public const string StartWebSite                    = IisPrefix + "StartWebSite";
    public const string StartApplicationPool            = IisPrefix + "StartApplicationPool";

    // ── WebApplication branch (DeploymentType=webApplication) ─────────────────
    public const string WebApplicationCreateOrUpdate    = IisPrefix + "WebApplication.CreateOrUpdate";
    public const string WebApplicationWebSiteName       = IisPrefix + "WebApplication.WebSiteName";
    public const string WebApplicationVirtualPath       = IisPrefix + "WebApplication.VirtualPath";
    public const string WebApplicationApplicationPoolName             = IisPrefix + "WebApplication.ApplicationPoolName";
    public const string WebApplicationApplicationPoolFrameworkVersion = IisPrefix + "WebApplication.ApplicationPoolFrameworkVersion";
    public const string WebApplicationApplicationPoolIdentityType     = IisPrefix + "WebApplication.ApplicationPoolIdentityType";
    public const string WebApplicationApplicationPoolUsername         = IisPrefix + "WebApplication.ApplicationPoolUsername";
    public const string WebApplicationApplicationPoolPassword         = IisPrefix + "WebApplication.ApplicationPoolPassword";

    // ── VirtualDirectory branch (DeploymentType=virtualDirectory) ─────────────
    public const string VirtualDirectoryCreateOrUpdate  = IisPrefix + "VirtualDirectory.CreateOrUpdate";
    public const string VirtualDirectoryWebSiteName     = IisPrefix + "VirtualDirectory.WebSiteName";
    public const string VirtualDirectoryVirtualPath     = IisPrefix + "VirtualDirectory.VirtualPath";

    public const string PackageCustomInstallationDirectory = PkgPrefix + "CustomInstallationDirectory";
}

/// <summary>
/// Translates an <c>Octopus.IIS</c> step config (Octopus property bag) into a
/// strongly-typed <see cref="KrakenIisConfig"/> so the existing
/// <c>IisScriptGenerator</c> emits the same PowerShell for both shapes.
/// Octostache substitution is applied to user-facing string values via the
/// supplied callback before the values reach the script generator.
/// </summary>
public static class OctopusIisConfig
{
    /// <summary>
    /// Returns <c>true</c> when the given config carries any <c>Octopus.Action.IISWebSite.*</c>
    /// key recognised by this mapper — i.e. the step config came from an
    /// Octopus-shape import rather than a Kraken-shape author.
    /// </summary>
    public static bool IsOctopusShape(IReadOnlyDictionary<string, string> config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.ContainsKey(OctopusIisConfigKeys.WebSiteName)
            || config.ContainsKey(OctopusIisConfigKeys.WebApplicationCreateOrUpdate)
            || config.ContainsKey(OctopusIisConfigKeys.VirtualDirectoryCreateOrUpdate);
    }

    /// <summary>
    /// Maps the Octopus property bag into a <see cref="KrakenIisConfig"/>.
    /// Returns the mapped config alongside any per-value warnings (auth toggles
    /// not yet supported, sensitive-password envelope detected, etc.).
    /// Throws <see cref="InvalidOperationException"/> when the source is missing
    /// the required <see cref="OctopusIisConfigKeys.WebSiteName"/> key, or when
    /// <see cref="OctopusIisConfigKeys.DeploymentType"/> is set to a value other
    /// than <c>webSite</c> (the only branch this version supports).
    /// </summary>
    /// <param name="config">Step config — typically <c>step.Config</c> from the snapshot.</param>
    /// <param name="octostache">
    /// Octostache evaluator. Used to substitute <c>#{...}</c> placeholders in
    /// site name, paths, app-pool username/password, and the bindings JSON string.
    /// </param>
    /// <param name="fallbackWebRoot">
    /// Directory to use as <c>WebRoot</c> when no <c>Octopus.Action.Package.CustomInstallationDirectory</c>
    /// is configured — typically the package <c>ExtractDir</c>.
    /// </param>
    public static MappingResult MapToKrakenIisConfig(
        IReadOnlyDictionary<string, string> config,
        Func<string, string> octostache,
        string fallbackWebRoot)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(octostache);

        var deploymentType = config.GetValueOrDefault(OctopusIisConfigKeys.DeploymentType, "webSite");

        if (deploymentType.Equals("webApplication", StringComparison.OrdinalIgnoreCase))
        {
            return MapWebApplication(config, octostache, fallbackWebRoot);
        }
        if (deploymentType.Equals("virtualDirectory", StringComparison.OrdinalIgnoreCase))
        {
            return MapVirtualDirectory(config, octostache, fallbackWebRoot);
        }

        // Default branch — webSite (or any unknown value, which we treat as webSite
        // with a defensive warning so the operator notices).
        return MapWebSite(config, octostache, fallbackWebRoot, deploymentType);
    }

    // ── webSite ──────────────────────────────────────────────────────────────

    private static MappingResult MapWebSite(
        IReadOnlyDictionary<string, string> config,
        Func<string, string> octostache,
        string fallbackWebRoot,
        string sourceDeploymentType)
    {
        var warnings = new List<string>();

        if (!sourceDeploymentType.Equals("webSite", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(
                $"Octopus.IIS DeploymentType '{sourceDeploymentType}' is not recognised — " +
                "treating as 'webSite'. Known values: webSite / webApplication / virtualDirectory.");
        }

        var siteName = SubstituteOrEmpty(config, OctopusIisConfigKeys.WebSiteName, octostache);
        if (string.IsNullOrWhiteSpace(siteName))
        {
            throw new InvalidOperationException(
                $"Octopus.IIS config is missing required key '{OctopusIisConfigKeys.WebSiteName}'.");
        }

        // WebRoot: CustomInstallationDirectory if set, else the fallback (extracted package).
        var customDir = SubstituteOrEmpty(config,
            OctopusIisConfigKeys.PackageCustomInstallationDirectory, octostache);
        var webRoot = string.IsNullOrWhiteSpace(customDir) ? fallbackWebRoot : customDir;

        var poolName = SubstituteOrEmpty(config, OctopusIisConfigKeys.ApplicationPoolName, octostache);
        var runtimeVersion = config.GetValueOrDefault(
            OctopusIisConfigKeys.ApplicationPoolFrameworkVersion, "v4.0");
        var identityType = config.GetValueOrDefault(
            OctopusIisConfigKeys.ApplicationPoolIdentityType, "ApplicationPoolIdentity");
        var username = SubstituteOrEmpty(config, OctopusIisConfigKeys.ApplicationPoolUsername, octostache);
        var password = SubstituteOrEmpty(config, OctopusIisConfigKeys.ApplicationPoolPassword, octostache);

        // Octopus emits sensitive properties as {HasValue,NewValue,Hint} JSON
        // envelopes; the B-2 importer preserves that envelope as JSON text. The
        // actual password value is *not* in the export, so we can't recover it.
        if (LooksLikeSensitiveEnvelope(password))
        {
            warnings.Add(
                "Octopus.Action.IISWebSite.ApplicationPoolPassword is a sensitive-value envelope; " +
                "the actual password is not present in the export. Bind it to a Kraken deployment variable.");
            password = string.Empty;
        }

        // Octopus emits the auth toggles even when the user didn't touch them, so we
        // only forward them when explicitly present in the bag. Anything absent means
        // "use Kraken's default for that module" (anonymous on, basic + windows off).
        var hasAnon    = config.ContainsKey(OctopusIisConfigKeys.EnableAnonymousAuth);
        var hasBasic   = config.ContainsKey(OctopusIisConfigKeys.EnableBasicAuth);
        var hasWindows = config.ContainsKey(OctopusIisConfigKeys.EnableWindowsAuth);

        var bindingsJson = SubstituteOrEmpty(config, OctopusIisConfigKeys.Bindings, octostache);
        var bindingLines = TranslateBindings(bindingsJson, warnings);

        var krakenConfig = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [KrakenIisConfigKeys.SiteName]              = siteName,
            [KrakenIisConfigKeys.WebRoot]               = webRoot,
            [KrakenIisConfigKeys.AppPoolName]           = string.IsNullOrWhiteSpace(poolName) ? siteName : poolName,
            [KrakenIisConfigKeys.AppPoolRuntimeVersion] = runtimeVersion,
            [KrakenIisConfigKeys.AppPoolIdentityType]   = identityType,
            // Octopus.IIS shape always deploys in-place — atomic-swap is a Kraken-only extra
            // and would require a versioned-subdir layout that Octopus doesn't model.
            [KrakenIisConfigKeys.DeployMode]            = "InPlace",
        };

        if (hasAnon)
        {
            krakenConfig[KrakenIisConfigKeys.AuthenticationAnonymousEnabled] =
                IsOn(config, OctopusIisConfigKeys.EnableAnonymousAuth) ? "true" : "false";
        }
        if (hasBasic)
        {
            krakenConfig[KrakenIisConfigKeys.AuthenticationBasicEnabled] =
                IsOn(config, OctopusIisConfigKeys.EnableBasicAuth) ? "true" : "false";
        }
        if (hasWindows)
        {
            krakenConfig[KrakenIisConfigKeys.AuthenticationWindowsEnabled] =
                IsOn(config, OctopusIisConfigKeys.EnableWindowsAuth) ? "true" : "false";
        }
        if (!string.IsNullOrEmpty(username))
        {
            krakenConfig[KrakenIisConfigKeys.AppPoolUsername] = username;
        }
        if (!string.IsNullOrEmpty(password))
        {
            krakenConfig[KrakenIisConfigKeys.AppPoolPassword] = password;
        }
        if (!string.IsNullOrEmpty(bindingLines))
        {
            krakenConfig[KrakenIisConfigKeys.Bindings] = bindingLines;
        }

        return MappingResult.ForWebSite(KrakenIisConfig.Parse(krakenConfig), warnings);
    }

    // ── webApplication ───────────────────────────────────────────────────────

    private static MappingResult MapWebApplication(
        IReadOnlyDictionary<string, string> config,
        Func<string, string> octostache,
        string fallbackInstallRoot)
    {
        var warnings = new List<string>();

        var parentSite = SubstituteOrEmpty(config, OctopusIisConfigKeys.WebApplicationWebSiteName, octostache);
        if (string.IsNullOrWhiteSpace(parentSite))
        {
            throw new InvalidOperationException(
                $"Octopus.IIS DeploymentType=webApplication is missing required key " +
                $"'{OctopusIisConfigKeys.WebApplicationWebSiteName}'.");
        }

        var virtualPath = SubstituteOrEmpty(config, OctopusIisConfigKeys.WebApplicationVirtualPath, octostache);
        if (string.IsNullOrWhiteSpace(virtualPath))
        {
            throw new InvalidOperationException(
                $"Octopus.IIS DeploymentType=webApplication is missing required key " +
                $"'{OctopusIisConfigKeys.WebApplicationVirtualPath}'.");
        }

        var customDir = SubstituteOrEmpty(config,
            OctopusIisConfigKeys.PackageCustomInstallationDirectory, octostache);
        var physicalPath = string.IsNullOrWhiteSpace(customDir) ? fallbackInstallRoot : customDir;

        // App pool — webApplication has its own under WebApplication.* keys.
        var poolName = SubstituteOrEmpty(config, OctopusIisConfigKeys.WebApplicationApplicationPoolName, octostache);
        if (string.IsNullOrWhiteSpace(poolName))
        {
            // Fall back to a derived name so we always have something to create.
            poolName = $"{parentSite}_{virtualPath.Trim('/')}";
        }
        var runtimeVersion = config.GetValueOrDefault(
            OctopusIisConfigKeys.WebApplicationApplicationPoolFrameworkVersion, "v4.0");
        var identityType = config.GetValueOrDefault(
            OctopusIisConfigKeys.WebApplicationApplicationPoolIdentityType, "ApplicationPoolIdentity");
        var username = SubstituteOrEmpty(config, OctopusIisConfigKeys.WebApplicationApplicationPoolUsername, octostache);
        var password = SubstituteOrEmpty(config, OctopusIisConfigKeys.WebApplicationApplicationPoolPassword, octostache);

        if (LooksLikeSensitiveEnvelope(password))
        {
            warnings.Add(
                "Octopus.Action.IISWebSite.WebApplication.ApplicationPoolPassword is a sensitive-value envelope; "
                + "the actual password is not present in the export. Bind it to a Kraken deployment variable.");
            password = string.Empty;
        }

        var webApp = new KrakenIisWebApplicationConfig
        {
            ParentSiteName = parentSite,
            VirtualPath    = virtualPath,
            PhysicalPath   = physicalPath,
            AppPool = new KrakenIisAppPool
            {
                Name            = poolName,
                RuntimeVersion  = runtimeVersion,
                IdentityType    = identityType,
                Username        = string.IsNullOrWhiteSpace(username) ? null : username,
                Password        = string.IsNullOrWhiteSpace(password) ? null : password,
            },
        };

        return MappingResult.ForWebApplication(webApp, warnings);
    }

    // ── virtualDirectory ─────────────────────────────────────────────────────

    private static MappingResult MapVirtualDirectory(
        IReadOnlyDictionary<string, string> config,
        Func<string, string> octostache,
        string fallbackInstallRoot)
    {
        var warnings = new List<string>();

        // Per the real WebArgosy export, virtualDirectory steps reuse the
        // VirtualDirectory.* sub-namespace mirroring the WebApplication.* keys.
        var parentSite = SubstituteOrEmpty(config, OctopusIisConfigKeys.VirtualDirectoryWebSiteName, octostache);
        if (string.IsNullOrWhiteSpace(parentSite))
        {
            throw new InvalidOperationException(
                $"Octopus.IIS DeploymentType=virtualDirectory is missing required key " +
                $"'{OctopusIisConfigKeys.VirtualDirectoryWebSiteName}'.");
        }

        var virtualPath = SubstituteOrEmpty(config, OctopusIisConfigKeys.VirtualDirectoryVirtualPath, octostache);
        if (string.IsNullOrWhiteSpace(virtualPath))
        {
            throw new InvalidOperationException(
                $"Octopus.IIS DeploymentType=virtualDirectory is missing required key " +
                $"'{OctopusIisConfigKeys.VirtualDirectoryVirtualPath}'.");
        }

        var customDir = SubstituteOrEmpty(config,
            OctopusIisConfigKeys.PackageCustomInstallationDirectory, octostache);
        var physicalPath = string.IsNullOrWhiteSpace(customDir) ? fallbackInstallRoot : customDir;

        var vdir = new KrakenIisVirtualDirectoryConfig
        {
            ParentSiteName = parentSite,
            VirtualPath    = virtualPath,
            PhysicalPath   = physicalPath,
        };

        return MappingResult.ForVirtualDirectory(vdir, warnings);
    }

    /// <summary>
    /// Discriminated result of the Octopus → Kraken mapping. Exactly one of
    /// <see cref="WebSite"/> / <see cref="WebApplication"/> /
    /// <see cref="VirtualDirectory"/> is non-null, matching the source's
    /// <c>Octopus.Action.IISWebSite.DeploymentType</c>.
    /// </summary>
    public sealed record MappingResult
    {
        public KrakenIisConfig?                  WebSite          { get; init; }
        public KrakenIisWebApplicationConfig?    WebApplication   { get; init; }
        public KrakenIisVirtualDirectoryConfig?  VirtualDirectory { get; init; }
        public required IReadOnlyList<string>    Warnings         { get; init; }

        internal static MappingResult ForWebSite(KrakenIisConfig cfg, IReadOnlyList<string> warnings)
            => new() { WebSite = cfg, Warnings = warnings };

        internal static MappingResult ForWebApplication(KrakenIisWebApplicationConfig cfg, IReadOnlyList<string> warnings)
            => new() { WebApplication = cfg, Warnings = warnings };

        internal static MappingResult ForVirtualDirectory(KrakenIisVirtualDirectoryConfig cfg, IReadOnlyList<string> warnings)
            => new() { VirtualDirectory = cfg, Warnings = warnings };

        /// <summary>For back-compat with B-3a's two-arg constructor pattern.</summary>
        [Obsolete("Use ForWebSite/ForWebApplication/ForVirtualDirectory factories.")]
        public MappingResult(KrakenIisConfig config, IReadOnlyList<string> warnings)
        {
            WebSite  = config;
            Warnings = warnings;
        }

        public MappingResult() { Warnings = []; }

        /// <summary>
        /// Back-compat alias for B-3 tests that read <c>result.Config</c>.
        /// Equivalent to <see cref="WebSite"/>.
        /// </summary>
        public KrakenIisConfig? Config => WebSite;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string SubstituteOrEmpty(
        IReadOnlyDictionary<string, string> config, string key, Func<string, string> octostache)
    {
        if (!config.TryGetValue(key, out var raw) || string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }
        return octostache(raw);
    }

    private static bool IsOn(IReadOnlyDictionary<string, string> config, string key)
        => config.TryGetValue(key, out var v)
        && v.Equals("True", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeSensitiveEnvelope(string value)
        => !string.IsNullOrEmpty(value)
        && value.Contains("\"HasValue\"", StringComparison.Ordinal)
        && value.Contains("\"NewValue\"", StringComparison.Ordinal);

    /// <summary>
    /// Translates Octopus's <c>Bindings</c> JSON array
    /// (<c>[{protocol,ipAddress,port,host,thumbprint,certificateVariable,requireSni,enabled}…]</c>)
    /// into Kraken's newline-separated pipe-delimited bindings format
    /// (<c>protocol|ip|port|host|thumbprint|store|sniRequired|sslFlags</c>).
    /// Disabled bindings (<c>enabled=false</c>) are dropped — including when the
    /// flag has come from an Octostache conditional that evaluated to a string
    /// like <c>"False"</c>. Defaults: <c>store="My"</c> when a thumbprint is
    /// present; <c>sslFlags="1"</c> when <c>requireSni</c> is true, else <c>"0"</c>.
    /// </summary>
    private static string TranslateBindings(string bindingsJson, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(bindingsJson))
        {
            return string.Empty;
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(bindingsJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            warnings.Add($"Bindings JSON could not be parsed: {ex.Message}");
            return string.Empty;
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            warnings.Add("Octopus.Action.IISWebSite.Bindings is not a JSON array — ignored.");
            return string.Empty;
        }

        var lines = new List<string>();
        foreach (var b in root.EnumerateArray())
        {
            if (!AsBool(b, "enabled", defaultValue: true))
            {
                continue;
            }
            var protocol   = AsString(b, "protocol");
            var ipAddress  = AsString(b, "ipAddress");
            var port       = AsString(b, "port");
            var host       = AsString(b, "host");
            var thumbprint = AsString(b, "thumbprint");
            var requireSni = AsBool(b, "requireSni", defaultValue: false);

            var store    = string.IsNullOrEmpty(thumbprint) ? string.Empty : "My";
            var sslFlags = requireSni ? "1" : "0";
            lines.Add(string.Join('|',
                protocol, ipAddress, port, host, thumbprint, store,
                requireSni ? "true" : "false", sslFlags));
        }
        return string.Join('\n', lines);
    }

    private static string AsString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? string.Empty,
            JsonValueKind.True   => "true",
            JsonValueKind.False  => "false",
            JsonValueKind.Number => v.GetRawText(),
            _                    => string.Empty,
        };
    }

    private static bool AsBool(JsonElement obj, string name, bool defaultValue)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
        {
            return defaultValue;
        }
        return v.ValueKind switch
        {
            JsonValueKind.True  => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            _ => defaultValue,
        };
    }
}

using System.Globalization;
using System.Text;
using KrakenDeploy.Contracts.Steps;

namespace KrakenDeploy.Steps.KrakenIis;

/// <summary>
/// Generates the PowerShell script that drives the <c>Kraken.IIS</c> step on the
/// agent. The script uses the built-in <c>WebAdministration</c> module to
/// ensure-or-create the app pool, ensure-or-create the site, lay out bindings,
/// optionally perform an atomic-swap deploy of the package contents, and
/// optionally run a post-deploy HTTP health probe.
/// <para>
/// The generated script is idempotent — running it twice with the same
/// configuration produces the same end state.
/// </para>
/// </summary>
public static class IisScriptGenerator
{
    /// <summary>
    /// Builds the full PowerShell script.
    /// <paramref name="extractDir"/> is the directory the package was extracted to;
    /// in atomic-swap mode it is copied into a versioned subfolder under
    /// <see cref="KrakenIisConfig.WebRoot"/>.
    /// </summary>
    public static string Generate(KrakenIisConfig cfg, string extractDir, Guid deploymentId)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# ── KrakenDeploy Kraken.IIS step ──");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Deployment: {deploymentId}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Site:       {cfg.SiteName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# AppPool:    {cfg.AppPool.Name}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# DeployMode: {cfg.Deploy.Mode}");
        sb.AppendLine();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("Set-StrictMode -Version Latest");
        sb.AppendLine("Import-Module WebAdministration -ErrorAction Stop");
        sb.AppendLine();

        WriteVariables(sb, cfg, extractDir);
        WriteAppPoolBlock(sb, cfg.AppPool, cfg.Recycle, cfg.RapidFail, cfg.AlwaysRunning);
        WriteSiteBlock(sb, cfg);
        WriteBindingsBlock(sb, cfg);
        WriteAuthenticationBlock(sb, cfg.Authentication);
        WriteDeployBlock(sb, cfg);
        WriteRecycleBlock(sb, cfg);

        if (cfg.HealthCheck is not null)
        {
            WriteHealthCheckBlock(sb, cfg.HealthCheck);
        }

        sb.AppendLine();
        sb.AppendLine("Write-Host '[Kraken.IIS] Step complete.'");
        return sb.ToString();
    }

    /// <summary>
    /// Generates the PowerShell script for an IIS <strong>web application</strong>
    /// (sub-application beneath an existing site). Asserts the parent site exists,
    /// creates and configures the application's app pool (reusing
    /// <see cref="KrakenIisAppPool"/>), creates or updates the web application
    /// at <c>IIS:\Sites\&lt;parent&gt;\&lt;virtualPath&gt;</c>, then copies the
    /// extracted package content to the application's physical path.
    /// </summary>
    public static string GenerateWebApplication(
        KrakenIisWebApplicationConfig cfg, string extractDir, Guid deploymentId)
    {
        ArgumentNullException.ThrowIfNull(cfg);

        var sb = new StringBuilder();
        sb.AppendLine("# ── KrakenDeploy IIS Web Application step ──");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Deployment:   {deploymentId}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Parent site:  {cfg.ParentSiteName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Virtual path: {cfg.VirtualPath}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# App pool:     {cfg.AppPool.Name}");
        sb.AppendLine();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("Set-StrictMode -Version Latest");
        sb.AppendLine("Import-Module WebAdministration -ErrorAction Stop");
        sb.AppendLine();

        // Variable namespace shared with the WriteAppPoolBlock helper.
        sb.AppendLine("# ── Inputs ──");
        sb.AppendLine(CultureInfo.InvariantCulture, $"$siteName     = '{Esc(cfg.ParentSiteName)}'");
        sb.AppendLine(CultureInfo.InvariantCulture, $"$appPoolName  = '{Esc(cfg.AppPool.Name)}'");
        sb.AppendLine(CultureInfo.InvariantCulture, $"$virtualPath  = '{Esc(NormaliseVirtualPath(cfg.VirtualPath))}'");
        sb.AppendLine(CultureInfo.InvariantCulture, $"$physicalPath = '{Esc(cfg.PhysicalPath)}'");
        sb.AppendLine(CultureInfo.InvariantCulture, $"$extractDir   = '{Esc(extractDir)}'");
        sb.AppendLine();

        sb.AppendLine("# Assert parent site exists — web applications cannot bootstrap one.");
        sb.AppendLine("if (-not (Test-Path \"IIS:\\Sites\\$siteName\")) {");
        sb.AppendLine("    throw \"[Kraken.IIS] Parent site '$siteName' does not exist. " +
                      "Deploy the web site first (Octopus.IIS DeploymentType=webSite).\"");
        sb.AppendLine("}");
        sb.AppendLine();

        // Default values that WriteAppPoolBlock reads — for a sub-app these features
        // are dormant by design (the parent site owns them).
        WriteAppPoolBlock(sb, cfg.AppPool, new KrakenIisRecycle(), new KrakenIisRapidFail(), alwaysRunning: false);

        sb.AppendLine("# ── Ensure physical path + copy package payload ──");
        sb.AppendLine("if (-not (Test-Path $physicalPath)) {");
        sb.AppendLine("    New-Item -ItemType Directory -Path $physicalPath -Force | Out-Null");
        sb.AppendLine("}");
        sb.AppendLine("if (Test-Path -LiteralPath $extractDir) {");
        sb.AppendLine("    $items = Get-ChildItem -LiteralPath $extractDir -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("    if ($items) {");
        sb.AppendLine("        Write-Host '[Kraken.IIS] Copying extracted contents to web application…'");
        sb.AppendLine("        Copy-Item -Path (Join-Path $extractDir '*') -Destination $physicalPath -Recurse -Force");
        sb.AppendLine("    } else {");
        sb.AppendLine("        Write-Host '[Kraken.IIS] Extract dir is empty (configure-only deploy) — skipping copy.'");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();

        sb.AppendLine("# ── Create or update the web application ──");
        sb.AppendLine("$appPath = \"IIS:\\Sites\\$siteName$virtualPath\"");
        sb.AppendLine("if (-not (Test-Path $appPath)) {");
        sb.AppendLine("    Write-Host \"[Kraken.IIS] Creating web application at '$appPath'…\"");
        sb.AppendLine("    New-WebApplication -Site $siteName -Name $virtualPath.TrimStart('/') " +
                      "-PhysicalPath $physicalPath -ApplicationPool $appPoolName -Force | Out-Null");
        sb.AppendLine("} else {");
        sb.AppendLine("    Write-Host \"[Kraken.IIS] Updating web application at '$appPath'…\"");
        sb.AppendLine("    Set-ItemProperty $appPath -Name 'physicalPath' -Value $physicalPath");
        sb.AppendLine("    Set-ItemProperty $appPath -Name 'applicationPool' -Value $appPoolName");
        sb.AppendLine("}");
        sb.AppendLine();

        sb.AppendLine("# ── Start app pool ──");
        sb.AppendLine("Start-WebAppPool -Name $appPoolName -ErrorAction SilentlyContinue");
        sb.AppendLine();

        sb.AppendLine("Write-Host '[Kraken.IIS] Web application step complete.'");
        return sb.ToString();
    }

    /// <summary>
    /// Generates the PowerShell script for an IIS <strong>virtual directory</strong>
    /// (path-to-disk alias beneath an existing site). Asserts the parent site
    /// exists, creates or updates the virtual directory at
    /// <c>IIS:\Sites\&lt;parent&gt;\&lt;virtualPath&gt;</c>, then copies the
    /// extracted package content to the directory's physical path. A virtual
    /// directory does not have its own application pool — it inherits the
    /// parent site/application's pool.
    /// </summary>
    public static string GenerateVirtualDirectory(
        KrakenIisVirtualDirectoryConfig cfg, string extractDir, Guid deploymentId)
    {
        ArgumentNullException.ThrowIfNull(cfg);

        var sb = new StringBuilder();
        sb.AppendLine("# ── KrakenDeploy IIS Virtual Directory step ──");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Deployment:   {deploymentId}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Parent site:  {cfg.ParentSiteName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Virtual path: {cfg.VirtualPath}");
        sb.AppendLine();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("Set-StrictMode -Version Latest");
        sb.AppendLine("Import-Module WebAdministration -ErrorAction Stop");
        sb.AppendLine();

        sb.AppendLine("# ── Inputs ──");
        sb.AppendLine(CultureInfo.InvariantCulture, $"$siteName     = '{Esc(cfg.ParentSiteName)}'");
        sb.AppendLine(CultureInfo.InvariantCulture, $"$virtualPath  = '{Esc(NormaliseVirtualPath(cfg.VirtualPath))}'");
        sb.AppendLine(CultureInfo.InvariantCulture, $"$physicalPath = '{Esc(cfg.PhysicalPath)}'");
        sb.AppendLine(CultureInfo.InvariantCulture, $"$extractDir   = '{Esc(extractDir)}'");
        sb.AppendLine();

        sb.AppendLine("# Assert parent site exists — virtual directories cannot bootstrap one.");
        sb.AppendLine("if (-not (Test-Path \"IIS:\\Sites\\$siteName\")) {");
        sb.AppendLine("    throw \"[Kraken.IIS] Parent site '$siteName' does not exist. " +
                      "Deploy the web site first (Octopus.IIS DeploymentType=webSite).\"");
        sb.AppendLine("}");
        sb.AppendLine();

        sb.AppendLine("# ── Ensure physical path + copy package payload ──");
        sb.AppendLine("if (-not (Test-Path $physicalPath)) {");
        sb.AppendLine("    New-Item -ItemType Directory -Path $physicalPath -Force | Out-Null");
        sb.AppendLine("}");
        sb.AppendLine("if (Test-Path -LiteralPath $extractDir) {");
        sb.AppendLine("    $items = Get-ChildItem -LiteralPath $extractDir -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("    if ($items) {");
        sb.AppendLine("        Write-Host '[Kraken.IIS] Copying extracted contents to virtual directory…'");
        sb.AppendLine("        Copy-Item -Path (Join-Path $extractDir '*') -Destination $physicalPath -Recurse -Force");
        sb.AppendLine("    } else {");
        sb.AppendLine("        Write-Host '[Kraken.IIS] Extract dir is empty (configure-only deploy) — skipping copy.'");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();

        sb.AppendLine("# ── Create or update the virtual directory ──");
        sb.AppendLine("$vdirPath = \"IIS:\\Sites\\$siteName$virtualPath\"");
        sb.AppendLine("if (-not (Test-Path $vdirPath)) {");
        sb.AppendLine("    Write-Host \"[Kraken.IIS] Creating virtual directory at '$vdirPath'…\"");
        sb.AppendLine("    New-WebVirtualDirectory -Site $siteName -Name $virtualPath.TrimStart('/') " +
                      "-PhysicalPath $physicalPath -Force | Out-Null");
        sb.AppendLine("} else {");
        sb.AppendLine("    Write-Host \"[Kraken.IIS] Updating virtual directory at '$vdirPath'…\"");
        sb.AppendLine("    Set-ItemProperty $vdirPath -Name 'physicalPath' -Value $physicalPath");
        sb.AppendLine("}");
        sb.AppendLine();

        sb.AppendLine("Write-Host '[Kraken.IIS] Virtual directory step complete.'");
        return sb.ToString();
    }

    /// <summary>
    /// Normalises a virtual path to a leading-slash form (<c>arr</c> → <c>/arr</c>,
    /// <c>/arr</c> → <c>/arr</c>, <c>arr/sub</c> → <c>/arr/sub</c>). Empty input
    /// becomes <c>/</c> — the IIS root.
    /// </summary>
    private static string NormaliseVirtualPath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "/";
        }
        var trimmed = raw.Trim();
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    // ── Variables ──────────────────────────────────────────────────────────────

    private static void WriteVariables(StringBuilder sb, KrakenIisConfig cfg, string extractDir)
    {
        sb.AppendLine("# ── Inputs ──");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"$siteName    = '{Esc(cfg.SiteName)}'");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"$appPoolName = '{Esc(cfg.AppPool.Name)}'");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"$webRoot     = '{Esc(cfg.WebRoot)}'");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"$appPath     = '{Esc(cfg.AppPath)}'");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"$extractDir  = '{Esc(extractDir)}'");
        sb.AppendLine();
    }

    // ── App Pool ───────────────────────────────────────────────────────────────

    private static void WriteAppPoolBlock(
        StringBuilder sb,
        KrakenIisAppPool ap,
        KrakenIisRecycle recycle,
        KrakenIisRapidFail rf,
        bool alwaysRunning)
    {
        sb.AppendLine("# ── App Pool: ensure & configure ──");
        sb.AppendLine("if (-not (Test-Path \"IIS:\\AppPools\\$appPoolName\")) {");
        sb.AppendLine("    Write-Host \"[Kraken.IIS] Creating app pool '$appPoolName'…\"");
        sb.AppendLine("    New-WebAppPool -Name $appPoolName | Out-Null");
        sb.AppendLine("} else {");
        sb.AppendLine("    Write-Host \"[Kraken.IIS] App pool '$appPoolName' exists.\"");
        sb.AppendLine("}");
        sb.AppendLine();

        var startMode = alwaysRunning ? "AlwaysRunning" : ap.StartMode;

        sb.AppendLine("# Process model & runtime");
        SetAppPoolProp(sb, "managedRuntimeVersion", $"'{Esc(ap.RuntimeVersion)}'");
        SetAppPoolProp(sb, "managedPipelineMode", $"'{Esc(ap.PipelineMode)}'");
        SetAppPoolProp(sb, "enable32BitAppOnWin64", PsBool(ap.Enable32Bit));
        SetAppPoolProp(sb, "startMode", $"'{Esc(startMode)}'");
        SetAppPoolProp(sb, "queueLength", ap.QueueLength.ToString(CultureInfo.InvariantCulture));
        SetAppPoolProp(sb, "processModel.loadUserProfile", PsBool(ap.LoadUserProfile));
        SetAppPoolProp(sb, "processModel.idleTimeout",
            $"([TimeSpan]::FromMinutes({ap.IdleTimeoutMinutes.ToString(CultureInfo.InvariantCulture)}))");

        sb.AppendLine();
        sb.AppendLine("# Identity");
        if (ap.IdentityType.Equals("SpecificUser", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(ap.Username))
            {
                sb.AppendLine("throw 'IdentityType=SpecificUser requires a Username.'");
            }
            else
            {
                SetAppPoolProp(sb, "processModel.identityType", "'SpecificUser'");
                SetAppPoolProp(sb, "processModel.userName", $"'{Esc(ap.Username)}'");
                SetAppPoolProp(sb, "processModel.password",
                    $"'{Esc(ap.Password ?? string.Empty)}'");
            }
        }
        else
        {
            SetAppPoolProp(sb, "processModel.identityType", $"'{Esc(ap.IdentityType)}'");
        }

        sb.AppendLine();
        sb.AppendLine("# Rapid-fail protection");
        SetAppPoolProp(sb, "failure.rapidFailProtection", PsBool(rf.Enabled));
        SetAppPoolProp(sb, "failure.rapidFailProtectionMaxCrashes",
            rf.MaxCrashesPerInterval.ToString(CultureInfo.InvariantCulture));
        SetAppPoolProp(sb, "failure.rapidFailProtectionInterval",
            $"([TimeSpan]::FromMinutes({rf.IntervalMinutes.ToString(CultureInfo.InvariantCulture)}))");

        sb.AppendLine();
    }

    private static void SetAppPoolProp(StringBuilder sb, string property, string psValue)
    {
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Set-ItemProperty \"IIS:\\AppPools\\$appPoolName\" -Name '{property}' -Value {psValue}");
    }

    // ── Site ───────────────────────────────────────────────────────────────────

    private static void WriteSiteBlock(StringBuilder sb, KrakenIisConfig cfg)
    {
        sb.AppendLine("# ── Site: ensure ──");
        sb.AppendLine("if (-not (Test-Path \"IIS:\\Sites\\$siteName\")) {");
        sb.AppendLine("    Write-Host \"[Kraken.IIS] Creating site '$siteName'…\"");
        sb.AppendLine("    if (-not (Test-Path $webRoot)) {");
        sb.AppendLine("        New-Item -ItemType Directory -Path $webRoot -Force | Out-Null");
        sb.AppendLine("    }");
        sb.AppendLine("    # Bind a placeholder physicalPath; deploy block sets the real one.");
        sb.AppendLine("    New-Website -Name $siteName -Port 80 -PhysicalPath $webRoot " +
                      "-ApplicationPool $appPoolName -Force | Out-Null");
        sb.AppendLine("} else {");
        sb.AppendLine("    Write-Host \"[Kraken.IIS] Site '$siteName' exists.\"");
        sb.AppendLine("    Set-ItemProperty \"IIS:\\Sites\\$siteName\" -Name 'applicationPool' -Value $appPoolName");
        sb.AppendLine("}");
        sb.AppendLine();

        if (cfg.PreloadEnabled)
        {
            sb.AppendLine("# Preload");
            sb.AppendLine("Set-ItemProperty \"IIS:\\Sites\\$siteName\" -Name 'applicationDefaults.preloadEnabled' -Value $true");
            sb.AppendLine();
        }
    }

    // ── Bindings ───────────────────────────────────────────────────────────────

    private static void WriteBindingsBlock(StringBuilder sb, KrakenIisConfig cfg)
    {
        if (cfg.Bindings.Count == 0)
        {
            return;
        }

        sb.AppendLine("# ── Bindings: replace ──");
        sb.AppendLine("$existing = Get-WebBinding -Name $siteName -ErrorAction SilentlyContinue");
        sb.AppendLine("if ($existing) {");
        sb.AppendLine("    foreach ($b in @($existing)) {");
        sb.AppendLine("        Remove-WebBinding -Name $siteName -BindingInformation $b.bindingInformation " +
                      "-Protocol $b.protocol -ErrorAction SilentlyContinue");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();

        foreach (var b in cfg.Bindings)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"Write-Host '[Kraken.IIS] Adding {b.Protocol} binding {b.BindingInformation}…'");

            if (b.IsHttps)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"New-WebBinding -Name $siteName -Protocol 'https' -IPAddress '{Esc(b.IpAddress)}' " +
                    $"-Port {b.Port.ToString(CultureInfo.InvariantCulture)} -HostHeader '{Esc(b.Hostname)}' " +
                    $"-SslFlags {b.SslFlags.ToString(CultureInfo.InvariantCulture)} | Out-Null");

                if (!string.IsNullOrEmpty(b.CertThumbprint))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"$bindingInfo = '{Esc(b.BindingInformation)}'");
                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"$cert = Get-Item \"Cert:\\LocalMachine\\{Esc(b.CertStore)}\\{Esc(b.CertThumbprint)}\" -ErrorAction SilentlyContinue");
                    sb.AppendLine("if (-not $cert) {");
                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"    throw 'Certificate {Esc(b.CertThumbprint)} not found in store {Esc(b.CertStore)}.'");
                    sb.AppendLine("}");
                    sb.AppendLine("$webBinding = Get-WebBinding -Name $siteName -Protocol 'https' " +
                                  "-IPAddress $cert.PSParentPath; # ensure context");
                    sb.AppendLine("$wb = Get-WebBinding -Name $siteName -BindingInformation $bindingInfo " +
                                  "-Protocol 'https' -ErrorAction SilentlyContinue");
                    sb.AppendLine("if ($wb) {");
                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"    $wb.AddSslCertificate('{Esc(b.CertThumbprint)}', '{Esc(b.CertStore)}') | Out-Null");
                    sb.AppendLine("}");
                }
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"New-WebBinding -Name $siteName -Protocol '{Esc(b.Protocol)}' -IPAddress '{Esc(b.IpAddress)}' " +
                    $"-Port {b.Port.ToString(CultureInfo.InvariantCulture)} -HostHeader '{Esc(b.Hostname)}' | Out-Null");
            }
        }

        sb.AppendLine();
    }

    // ── Authentication (site-level module toggles) ────────────────────────────

    private static void WriteAuthenticationBlock(StringBuilder sb, KrakenIisAuthentication auth)
    {
        sb.AppendLine("# ── Authentication modules (site-level) ──");
        // Each module is configured under the site's IIS:\Sites path. Set-WebConfigurationProperty
        // mutates web.config; the modules themselves must be installed in IIS (the standard
        // WebServer role enables AnonymousAuthentication out of the box; Basic and Windows
        // are optional sub-features the operator may need to install separately on the host).
        SetAuthModule(sb, "anonymousAuthentication", auth.AnonymousEnabled);
        SetAuthModule(sb, "basicAuthentication",     auth.BasicEnabled);
        SetAuthModule(sb, "windowsAuthentication",   auth.WindowsEnabled);
        sb.AppendLine();
    }

    private static void SetAuthModule(StringBuilder sb, string moduleName, bool enabled)
    {
        // PSPath must point at IIS:\ (not literal path), filter selects the auth module section.
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Set-WebConfigurationProperty -Filter '/system.WebServer/security/authentication/{moduleName}' " +
            $"-Name 'enabled' -Value {PsBool(enabled)} -PSPath 'IIS:\\' -Location $siteName");
    }

    // ── Deploy (atomic-swap or in-place) ───────────────────────────────────────

    private static void WriteDeployBlock(StringBuilder sb, KrakenIisConfig cfg)
    {
        sb.AppendLine("# ── Deploy package ──");

        if (!cfg.Deploy.IsAtomicSwap)
        {
            // In-place: copy extract → webRoot
            sb.AppendLine("Write-Host '[Kraken.IIS] In-place deploy: copying package to webRoot…'");
            sb.AppendLine("if (-not (Test-Path $webRoot)) { New-Item -ItemType Directory -Path $webRoot -Force | Out-Null }");
            sb.AppendLine("Copy-Item -Path (Join-Path $extractDir '*') -Destination $webRoot -Recurse -Force");
            sb.AppendLine("Set-ItemProperty \"IIS:\\Sites\\$siteName\" -Name 'physicalPath' -Value $webRoot");
            sb.AppendLine();
            return;
        }

        // Atomic swap: copy → versioned subdir, then point physicalPath at it
        sb.AppendLine("Write-Host '[Kraken.IIS] Atomic-swap deploy…'");
        sb.AppendLine("$versionStamp = (Get-Date).ToString('yyyy.MM.dd-HHmmss')");
        sb.AppendLine("$versionDir = Join-Path $webRoot \"v-$versionStamp\"");
        sb.AppendLine("New-Item -ItemType Directory -Path $versionDir -Force | Out-Null");
        sb.AppendLine("Copy-Item -Path (Join-Path $extractDir '*') -Destination $versionDir -Recurse -Force");

        sb.AppendLine("# Capture previous physicalPath for rollback context");
        sb.AppendLine("$previousPath = (Get-ItemProperty \"IIS:\\Sites\\$siteName\" -Name 'physicalPath').Value");
        sb.AppendLine("Write-Host \"[Kraken.IIS] Switching site physicalPath to: $versionDir (was: $previousPath)\"");
        sb.AppendLine("Set-ItemProperty \"IIS:\\Sites\\$siteName\" -Name 'physicalPath' -Value $versionDir");

        if (cfg.Deploy.KeepVersions > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# Retention: keep the most recent N version directories");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"$keep = {cfg.Deploy.KeepVersions.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine("$allVersions = Get-ChildItem -Path $webRoot -Directory -Filter 'v-*' | " +
                          "Sort-Object Name -Descending");
            sb.AppendLine("if ($allVersions.Count -gt $keep) {");
            sb.AppendLine("    foreach ($old in $allVersions | Select-Object -Skip $keep) {");
            sb.AppendLine("        if ($old.FullName -ne $versionDir -and $old.FullName -ne $previousPath) {");
            sb.AppendLine("            Write-Host \"[Kraken.IIS] Pruning old version: $($old.FullName)\"");
            sb.AppendLine("            try { Remove-Item -Path $old.FullName -Recurse -Force -ErrorAction Stop }");
            sb.AppendLine("            catch { Write-Warning \"Failed to remove $($old.FullName): $_\" }");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
        }

        sb.AppendLine();
    }

    // ── Recycle (drain mode optional) ──────────────────────────────────────────

    private static void WriteRecycleBlock(StringBuilder sb, KrakenIisConfig cfg)
    {
        sb.AppendLine("# ── App pool recycle ──");
        sb.AppendLine("$state = (Get-WebAppPoolState -Name $appPoolName).Value");
        sb.AppendLine("if ($state -ne 'Started') {");
        sb.AppendLine("    Write-Host \"[Kraken.IIS] Starting app pool '$appPoolName' (was $state)…\"");
        sb.AppendLine("    Start-WebAppPool -Name $appPoolName");
        sb.AppendLine("} else {");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"    Write-Host '[Kraken.IIS] {(cfg.Deploy.DrainModeRecycle ? "Drain-mode" : "Hard")} recycling app pool…'");
        sb.AppendLine(cfg.Deploy.DrainModeRecycle
            ? "    Restart-WebAppPool -Name $appPoolName"  // overlapping is the IIS default
            : "    Stop-WebAppPool -Name $appPoolName; Start-Sleep -Seconds 1; Start-WebAppPool -Name $appPoolName");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    // ── Health Probe ───────────────────────────────────────────────────────────

    private static void WriteHealthCheckBlock(StringBuilder sb, KrakenIisHealthCheck hc)
    {
        sb.AppendLine("# ── Post-deploy health probe ──");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"$healthUrl     = '{Esc(hc.Url)}'");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"$expectedCode  = {hc.ExpectedStatus.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"$timeoutSec    = {hc.TimeoutSeconds.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"$retryAttempts = {hc.RetryAttempts.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"$retryDelay    = {hc.RetryDelaySeconds.ToString(CultureInfo.InvariantCulture)}");

        if (!string.IsNullOrEmpty(hc.ExpectedBodyContains))
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"$expectedBody  = '{Esc(hc.ExpectedBodyContains)}'");
        }
        else
        {
            sb.AppendLine("$expectedBody  = $null");
        }

        sb.AppendLine();
        sb.AppendLine("$healthOk = $false");
        sb.AppendLine("for ($i = 1; $i -le $retryAttempts; $i++) {");
        sb.AppendLine("    Write-Host \"[Kraken.IIS] Health probe attempt $i/$retryAttempts → $healthUrl\"");
        sb.AppendLine("    try {");
        sb.AppendLine("        $resp = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing " +
                      "-TimeoutSec $timeoutSec -ErrorAction Stop");
        sb.AppendLine("        if ($resp.StatusCode -eq $expectedCode) {");
        sb.AppendLine("            if ([string]::IsNullOrEmpty($expectedBody) -or " +
                      "$resp.Content.IndexOf($expectedBody, [StringComparison]::Ordinal) -ge 0) {");
        sb.AppendLine("                Write-Host \"[Kraken.IIS] Health probe OK ($($resp.StatusCode)).\"");
        sb.AppendLine("                $healthOk = $true");
        sb.AppendLine("                break");
        sb.AppendLine("            } else {");
        sb.AppendLine("                Write-Warning \"Status OK but expected body fragment not found.\"");
        sb.AppendLine("            }");
        sb.AppendLine("        } else {");
        sb.AppendLine("            Write-Warning \"Got status $($resp.StatusCode), expected $expectedCode.\"");
        sb.AppendLine("        }");
        sb.AppendLine("    } catch {");
        sb.AppendLine("        Write-Warning \"Health probe error: $($_.Exception.Message)\"");
        sb.AppendLine("    }");
        sb.AppendLine("    if ($i -lt $retryAttempts) { Start-Sleep -Seconds $retryDelay }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("if (-not $healthOk) {");
        sb.AppendLine("    throw \"[Kraken.IIS] Health probe failed after $retryAttempts attempt(s).\"");
        sb.AppendLine("}");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string Esc(string s) => s.Replace("'", "''");
    private static string PsBool(bool b) => b ? "$true" : "$false";
}

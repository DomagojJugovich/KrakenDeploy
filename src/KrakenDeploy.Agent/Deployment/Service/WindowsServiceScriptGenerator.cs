using System.Globalization;
using System.Text;

namespace KrakenDeploy.Agent.Deployment.Service;

/// <summary>
/// Generates the PowerShell script that drives an <c>Octopus.WindowsService</c>
/// step on the agent. Uses <c>sc.exe</c> for service creation / configuration
/// (works on every Windows Server back to 2003 and avoids the credential gaps
/// in older <c>New-Service</c> versions). PowerShell wraps the calls for
/// idempotency: stop the existing service if running, delete it, recreate.
/// <para>
/// The generated script is written to the step's artifacts directory before
/// execution so it appears as a downloadable artifact for troubleshooting.
/// </para>
/// </summary>
public static class WindowsServiceScriptGenerator
{
    public static string Generate(WindowsServiceConfig cfg, Guid deploymentId)
    {
        ArgumentNullException.ThrowIfNull(cfg);

        var sb = new StringBuilder();

        sb.AppendLine("# ── KrakenDeploy Octopus.WindowsService step ──");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Deployment:  {deploymentId}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Service:     {cfg.ServiceName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Account:     {cfg.ServiceAccount}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# StartMode:   {cfg.StartMode}");
        sb.AppendLine();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("Set-StrictMode -Version Latest");
        sb.AppendLine();

        // ── Resolve the executable path against the install root. ──
        // Octopus accepts both relative ("MyApp.exe", "bin\MyApp.exe") and absolute
        // paths. Relative paths combine against InstallRoot; absolute pass through.
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"$installRoot = '{Esc(cfg.InstallRoot)}'");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"$execInput   = '{Esc(cfg.ExecutablePath)}'");
        sb.AppendLine("$exePath = if ([System.IO.Path]::IsPathRooted($execInput)) { $execInput } else { Join-Path $installRoot $execInput }");
        sb.AppendLine("if (-not (Test-Path -LiteralPath $exePath)) { throw \"Service executable not found at: $exePath\" }");
        sb.AppendLine();

        // Build the binPath value sc.exe expects. Wrap the exe in quotes, append
        // the (already-substituted) args as-is.
        var argsLiteral = cfg.Arguments is null ? string.Empty : Esc(cfg.Arguments);
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"$svcArgs   = '{argsLiteral}'");
        sb.AppendLine("$binPath = if ([string]::IsNullOrWhiteSpace($svcArgs)) { '\"' + $exePath + '\"' } else { '\"' + $exePath + '\" ' + $svcArgs }");
        sb.AppendLine();

        // ── Stop + delete any existing service of the same name. ──
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"$serviceName = '{Esc(cfg.ServiceName)}'");
        sb.AppendLine("$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue");
        sb.AppendLine("if ($existing) {");
        sb.AppendLine("    if ($existing.Status -ne 'Stopped') {");
        sb.AppendLine("        Write-Host \"[Octopus.WindowsService] Stopping existing service $serviceName…\"");
        sb.AppendLine("        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("        # Wait briefly so the SCM can release the binary.");
        sb.AppendLine("        $deadline = [DateTime]::UtcNow.AddSeconds(30)");
        sb.AppendLine("        while ((Get-Service -Name $serviceName -ErrorAction SilentlyContinue).Status -ne 'Stopped' -and [DateTime]::UtcNow -lt $deadline) {");
        sb.AppendLine("            Start-Sleep -Milliseconds 250");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("    Write-Host \"[Octopus.WindowsService] Deleting existing service $serviceName…\"");
        sb.AppendLine("    & sc.exe delete $serviceName | Out-Null");
        sb.AppendLine("    Start-Sleep -Milliseconds 500");
        sb.AppendLine("}");
        sb.AppendLine();

        // ── Resolve sc.exe start= / obj= tokens for the account. ──
        var startToken = cfg.StartMode switch
        {
            "delayed-auto" => "delayed-auto",
            "demand"       => "demand",
            "disabled"     => "disabled",
            "unchanged"    => "unchanged",
            _              => "auto",
        };

        WriteScCreate(sb, cfg, startToken);

        // ── Description (a separate sc.exe verb). ──
        if (!string.IsNullOrWhiteSpace(cfg.Description))
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"& sc.exe description $serviceName '{Esc(cfg.Description)}' | Out-Null");
        }

        // ── Optional StartMode=unchanged means do not touch start mode. The sc.exe
        // create above already used 'auto' fallback for safety in that case. ──

        sb.AppendLine();
        sb.AppendLine("Write-Host \"[Octopus.WindowsService] Service '$serviceName' is configured.\"");
        sb.AppendLine();

        // ── Desired status — start or leave stopped. ──
        if (cfg.DesiredStatus == "Running")
        {
            sb.AppendLine("Write-Host \"[Octopus.WindowsService] Starting service…\"");
            sb.AppendLine("Start-Service -Name $serviceName");
        }
        else
        {
            sb.AppendLine("Write-Host \"[Octopus.WindowsService] Leaving service stopped (DesiredStatus=Stopped).\"");
        }

        sb.AppendLine();
        sb.AppendLine("Write-Host '[Octopus.WindowsService] Step complete.'");
        return sb.ToString();
    }

    private static void WriteScCreate(StringBuilder sb, WindowsServiceConfig cfg, string startToken)
    {
        // sc.exe create <name> binPath= "..." start= ... DisplayName= "..." obj= "..." [password= "..."] [depend= "a/b/c"]
        // Spaces after the `=` are required by sc.exe. We pass each argument as a
        // separate element of $scArgs so PowerShell handles quoting.
        sb.AppendLine("$scArgs = @(");
        sb.AppendLine("    'create', $serviceName,");
        sb.AppendLine("    'binPath=', $binPath,");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    'start=',   '{startToken}',");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    'DisplayName=', '{Esc(cfg.DisplayName)}'");
        sb.AppendLine(")");

        // Account.
        switch (cfg.ServiceAccount)
        {
            case "LocalSystem":
                sb.AppendLine("$scArgs += @('obj=', 'LocalSystem')");
                break;
            case "LocalService":
                sb.AppendLine("$scArgs += @('obj=', 'NT AUTHORITY\\LocalService')");
                break;
            case "NetworkService":
                sb.AppendLine("$scArgs += @('obj=', 'NT AUTHORITY\\NetworkService')");
                break;
            case "_CUSTOM":
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"$scArgs += @('obj=', '{Esc(cfg.CustomAccountName!)}')");
                if (!string.IsNullOrEmpty(cfg.CustomAccountPassword))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"$scArgs += @('password=', '{Esc(cfg.CustomAccountPassword)}')");
                }
                break;
            default:
                // Defensive — Parse() normalises to one of the four above.
                sb.AppendLine("$scArgs += @('obj=', 'LocalSystem')");
                break;
        }

        // Dependencies — sc.exe wants them slash-separated in a single token.
        if (cfg.Dependencies.Count > 0)
        {
            var joined = string.Join('/', cfg.Dependencies);
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"$scArgs += @('depend=', '{Esc(joined)}')");
        }

        sb.AppendLine();
        sb.AppendLine("& sc.exe @scArgs | Out-Null");
        sb.AppendLine("if ($LASTEXITCODE -ne 0) { throw \"sc.exe create failed with exit code $LASTEXITCODE\" }");
    }

    /// <summary>
    /// Escapes a string for embedding inside a PowerShell single-quoted literal.
    /// Single quotes are escaped by doubling. Everything else passes through.
    /// </summary>
    private static string Esc(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);
}

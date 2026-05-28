using System.Management.Automation.Language;
using KrakenDeploy.Server.Core.Domain.Ai;

namespace KrakenDeploy.Server.Data.Services.Ai.Adhoc;

/// <summary>
/// M11.E.3 / M11.E.15 — the static-analysis gate for ad-hoc scripts. Parses
/// the LLM-generated PowerShell into an AST
/// (<see cref="System.Management.Automation.Language.Parser"/>) and vets it
/// against the session's <see cref="AdhocMode"/> BEFORE the server signs it.
/// Runs on the FIRST iteration and on EVERY subsequent iteration's proposed
/// fix (M11.E.15c) — the gate is the security contract, not a one-time check.
///
/// <para><strong>Readonly mode (allowlist).</strong> Only known non-mutating
/// commands pass: any <c>Get-*</c> / <c>Test-*</c> / <c>Measure-*</c> cmdlet
/// plus a curated set of safe pipeline/utility cmdlets
/// (<see cref="ReadonlySafeUtilityCmdlets"/>). Anything else is rejected as a
/// mode-escalation attempt (M11.E.15b) — a readonly session can never run a
/// mutating command. Network-egress cmdlets (<c>Invoke-WebRequest</c>,
/// <c>Invoke-RestMethod</c>) are <c>Invoke</c>-verb and therefore excluded.</para>
///
/// <para><strong>Mutating mode (blocklist).</strong> State-changing commands
/// are allowed except the forbidden set: <c>Invoke-Expression</c>,
/// <c>Invoke-Command</c> with a remoting target (the agent already runs ON the
/// target), <c>Remove-Item -Recurse -Force</c>, service install/uninstall
/// (<c>New-Service</c>/<c>Remove-Service</c>), registry-write cmdlets, and
/// <c>Add-Type</c> (arbitrary code compilation).</para>
///
/// <para><strong>Both modes</strong> reject: scripts with parse errors
/// (fail-closed), and dynamically-invoked commands (<c>&amp; $var</c>,
/// <c>iex</c>, call operator on a non-literal) whose target can't be resolved
/// statically.</para>
///
/// <para><strong>Documented limitations (not statically enforceable).</strong>
/// AST command-allowlisting does NOT prevent (a) direct .NET API abuse via
/// type literals (e.g. <c>[System.IO.File]::Delete($p)</c>,
/// <c>[System.Net.WebClient]</c>) — these are member-expressions, not commands;
/// nor (b) file/registry writes whose path is a runtime variable rather than a
/// literal. The residual risk is covered by the defense-in-depth layers that
/// the gate sits inside: mandatory operator approval per iteration (M11.E.5),
/// server signing + agent-side signature verification (M11.E.6), the frozen
/// immutable target set (M11.E.15a), and mode immutability (M11.E.15b). The
/// gate is the first filter, not the only one.</para>
/// </summary>
public static class AdhocScriptGate
{
    /// <summary>Verbs whose cmdlets are inherently read-only.</summary>
    private static readonly HashSet<string> ReadonlyVerbs =
        new(StringComparer.OrdinalIgnoreCase) { "Get", "Test", "Measure" };

    /// <summary>
    /// Non-mutating pipeline/utility cmdlets allowed in readonly mode in
    /// addition to the <see cref="ReadonlyVerbs"/>. Curated + audited: none of
    /// these change system state. Pipeline cmdlets that take script blocks
    /// (<c>ForEach-Object</c>, <c>Where-Object</c>) are safe because the gate
    /// descends into nested script blocks and analyses the commands inside
    /// them independently. Deliberately EXCLUDES <c>Tee-Object</c> / <c>Out-File</c>
    /// (write to disk) and all <c>Invoke-*</c> (egress / code execution).
    /// </summary>
    private static readonly HashSet<string> ReadonlySafeUtilityCmdlets =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Select-Object", "Where-Object", "Sort-Object", "Group-Object",
            "ForEach-Object", "Compare-Object", "Select-String",
            "Format-Table", "Format-List", "Format-Wide", "Format-Custom",
            "Out-String", "Out-Host", "Out-Default", "Out-Null",
            "Write-Output", "Write-Host", "Write-Verbose", "Write-Information",
            "Write-Debug", "Write-Warning",
            "ConvertTo-Json", "ConvertFrom-Json", "ConvertTo-Csv",
            "ConvertFrom-Csv", "ConvertFrom-StringData", "ConvertTo-Xml",
        };

    /// <summary>
    /// Security-critical alias → canonical-cmdlet map. Covers every alias of a
    /// forbidden cmdlet (so <c>iex</c> can't slip past an
    /// <c>Invoke-Expression</c> check) plus common safe aliases (so readonly
    /// scripts using <c>?</c>/<c>%</c>/<c>select</c> stay usable). An UNmapped
    /// alias is treated by its literal text — which fails closed in readonly
    /// (no matching verb) and, for any unmapped dangerous alias, would only be
    /// a gap if it resolves to a forbidden cmdlet; all known dangerous aliases
    /// are mapped here.
    /// </summary>
    private static readonly Dictionary<string, string> AliasMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Forbidden-cmdlet aliases (must be normalised before checking).
            ["iex"]   = "Invoke-Expression",
            ["icm"]   = "Invoke-Command",
            ["ri"]    = "Remove-Item",
            ["rm"]    = "Remove-Item",
            ["rmdir"] = "Remove-Item",
            ["rd"]    = "Remove-Item",
            ["del"]   = "Remove-Item",
            ["erase"] = "Remove-Item",
            ["sp"]    = "Set-ItemProperty",
            ["clp"]   = "Clear-ItemProperty",
            ["ni"]    = "New-Item",
            ["si"]    = "Set-Item",
            // Safe aliases (usability for readonly + mutating).
            ["?"]       = "Where-Object",
            ["where"]   = "Where-Object",
            ["%"]       = "ForEach-Object",
            ["foreach"] = "ForEach-Object",
            ["select"]  = "Select-Object",
            ["sort"]    = "Sort-Object",
            ["group"]   = "Group-Object",
            ["ft"]      = "Format-Table",
            ["fl"]      = "Format-List",
            ["fw"]      = "Format-Wide",
            ["echo"]    = "Write-Output",
            ["write"]   = "Write-Output",
            ["sls"]     = "Select-String",
            ["gc"]      = "Get-Content",
            ["cat"]     = "Get-Content",
            ["type"]    = "Get-Content",
            ["gci"]     = "Get-ChildItem",
            ["ls"]      = "Get-ChildItem",
            ["dir"]     = "Get-ChildItem",
            ["gm"]      = "Get-Member",
            ["gps"]     = "Get-Process",
            ["ps"]      = "Get-Process",
            ["gsv"]     = "Get-Service",
            ["measure"] = "Measure-Object",
        };

    /// <summary>Cmdlets forbidden in mutating mode regardless of arguments.</summary>
    private static readonly HashSet<string> AlwaysForbiddenCmdlets =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Invoke-Expression",
            "Add-Type",          // compiles + loads arbitrary code → RCE-equivalent
            "New-Service",       // service install
            "Remove-Service",    // service uninstall
            "New-ItemProperty",  // registry write
            "Set-ItemProperty",  // registry / item-metadata write
            "Remove-ItemProperty",
            "Clear-ItemProperty",
        };

    /// <summary>Registry-drive prefixes used to spot registry writes via the
    /// generic <c>*-Item</c> cmdlets.</summary>
    private static readonly string[] RegistryPathPrefixes =
        ["HKLM:", "HKCU:", "HKCR:", "HKU:", "HKCC:", "Registry::"];

    /// <summary>Remoting-target parameters on <c>Invoke-Command</c>. Matched by
    /// case-insensitive abbreviation (PowerShell allows <c>-Com</c> for
    /// <c>-ComputerName</c>), so any prefix of length ≥ 2 of these counts.</summary>
    private static readonly string[] RemotingParameters =
        ["ComputerName", "Cn", "Session", "ConnectionUri", "VMName", "VMId",
         "ContainerId", "HostName", "SSHTransport"];

    /// <summary>
    /// Analyses <paramref name="script"/> for the given <paramref name="mode"/>.
    /// Never throws on script content — a script that fails to parse is
    /// reported as a (rejecting) <see cref="AdhocViolationKind.ParseError"/>.
    /// </summary>
    public static AdhocScriptGateResult Analyze(string script, AdhocMode mode)
    {
        ArgumentNullException.ThrowIfNull(script);

        var violations = new List<AdhocScriptViolation>();

        var ast = Parser.ParseInput(script, out _, out var parseErrors);
        if (parseErrors is { Length: > 0 })
        {
            foreach (var err in parseErrors)
            {
                violations.Add(new AdhocScriptViolation(
                    AdhocViolationKind.ParseError,
                    $"Script does not parse: {err.Message}",
                    CommandName: null,
                    Line: err.Extent.StartLineNumber));
            }
            // Fail closed — an unparseable script is never signed.
            return new AdhocScriptGateResult(false, violations);
        }

        // searchNestedScriptBlocks: true so commands inside ForEach-Object{…},
        // &{…}, if/while bodies, etc. are gated individually — the readonly
        // allowlist can't be bypassed by hiding a mutating command in a block.
        var commands = ast.FindAll(n => n is CommandAst, searchNestedScriptBlocks: true)
            .Cast<CommandAst>();

        foreach (var cmd in commands)
        {
            var line = cmd.Extent.StartLineNumber;
            var rawName = cmd.GetCommandName();

            // Dynamic invocation: `& $var`, `. $expr`, or a command name that's
            // not a string literal. Unverifiable statically → reject in BOTH
            // modes (fail closed). This also blocks the decode-then-execute
            // pattern when paired with the Invoke-Expression block below.
            if (string.IsNullOrEmpty(rawName))
            {
                violations.Add(new AdhocScriptViolation(
                    AdhocViolationKind.DynamicInvocation,
                    "Command is invoked dynamically (call operator on a non-literal, " +
                    "or an expression-resolved name); its target can't be verified " +
                    "statically and is not allowed.",
                    CommandName: null,
                    Line: line));
                continue;
            }

            var name = Canonicalise(rawName);

            if (mode == AdhocMode.Readonly)
            {
                if (!IsReadonlyAllowed(name))
                {
                    violations.Add(new AdhocScriptViolation(
                        AdhocViolationKind.ModeEscalation,
                        $"'{rawName}' is not on the readonly allowlist (only Get-/Test-/" +
                        "Measure-* and curated safe utility cmdlets are permitted). A " +
                        "readonly session cannot run state-changing commands.",
                        rawName,
                        line));
                }
                // In readonly, the allowlist is the whole policy — no need to
                // also run the mutating blocklist (allowlist is strictly tighter).
                continue;
            }

            // ── Mutating-mode blocklist ─────────────────────────────────────
            CheckMutatingForbidden(cmd, name, rawName, line, violations);
        }

        return new AdhocScriptGateResult(violations.Count == 0, violations);
    }

    private static bool IsReadonlyAllowed(string canonicalName)
    {
        if (ReadonlySafeUtilityCmdlets.Contains(canonicalName))
        {
            return true;
        }
        var dash = canonicalName.IndexOf('-');
        if (dash <= 0)
        {
            // Native command (ipconfig, …) or bare name — no verb to vet.
            return false;
        }
        var verb = canonicalName[..dash];
        return ReadonlyVerbs.Contains(verb);
    }

    private static void CheckMutatingForbidden(
        CommandAst cmd, string name, string rawName, int line,
        List<AdhocScriptViolation> violations)
    {
        if (AlwaysForbiddenCmdlets.Contains(name))
        {
            var kind = name switch
            {
                "New-Service" or "Remove-Service" => AdhocViolationKind.ServiceLifecycle,
                "New-ItemProperty" or "Set-ItemProperty"
                    or "Remove-ItemProperty" or "Clear-ItemProperty"
                    => AdhocViolationKind.RegistryWrite,
                _ => AdhocViolationKind.ForbiddenCmdlet,
            };
            violations.Add(new AdhocScriptViolation(
                kind, $"'{rawName}' ({name}) is forbidden.", rawName, line));
            return;
        }

        if (name.Equals("Invoke-Command", StringComparison.OrdinalIgnoreCase)
            && HasRemotingParameter(cmd))
        {
            violations.Add(new AdhocScriptViolation(
                AdhocViolationKind.ForbiddenRemoting,
                "'Invoke-Command' with a remoting target is forbidden — the agent " +
                "already runs ON the target, so remoting is never needed and would " +
                "expand the blast radius beyond the frozen target set.",
                rawName, line));
            return;
        }

        if (name.Equals("Remove-Item", StringComparison.OrdinalIgnoreCase)
            && HasParameter(cmd, "Recurse") && HasParameter(cmd, "Force"))
        {
            violations.Add(new AdhocScriptViolation(
                AdhocViolationKind.DestructiveDelete,
                "'Remove-Item -Recurse -Force' is forbidden — an unbounded recursive " +
                "force-delete is too destructive to run from an ad-hoc action.",
                rawName, line));
            return;
        }

        // Registry write via a generic *-Item cmdlet targeting a registry drive.
        if ((name.Equals("New-Item", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Set-Item", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Remove-Item", StringComparison.OrdinalIgnoreCase))
            && TargetsRegistryPath(cmd))
        {
            violations.Add(new AdhocScriptViolation(
                AdhocViolationKind.RegistryWrite,
                $"'{rawName}' targets a registry path — registry writes are forbidden.",
                rawName, line));
        }
    }

    private static string Canonicalise(string commandName)
        => AliasMap.TryGetValue(commandName, out var canonical) ? canonical : commandName;

    private static bool HasParameter(CommandAst cmd, string parameterName)
    {
        foreach (var el in cmd.CommandElements)
        {
            if (el is CommandParameterAst p
                && parameterName.StartsWith(p.ParameterName, StringComparison.OrdinalIgnoreCase)
                && p.ParameterName.Length >= 3)
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasRemotingParameter(CommandAst cmd)
    {
        foreach (var el in cmd.CommandElements)
        {
            if (el is not CommandParameterAst p)
            {
                continue;
            }
            foreach (var remoting in RemotingParameters)
            {
                if (remoting.StartsWith(p.ParameterName, StringComparison.OrdinalIgnoreCase)
                    && p.ParameterName.Length >= 2)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool TargetsRegistryPath(CommandAst cmd)
    {
        foreach (var el in cmd.CommandElements)
        {
            if (el is StringConstantExpressionAst s)
            {
                foreach (var prefix in RegistryPathPrefixes)
                {
                    if (s.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }
}

/// <summary>Outcome of <see cref="AdhocScriptGate.Analyze"/>.</summary>
public sealed record AdhocScriptGateResult(
    bool IsAllowed,
    IReadOnlyList<AdhocScriptViolation> Violations)
{
    /// <summary>True when at least one violation is a mode-escalation attempt
    /// (a mutating command in a readonly session) — surfaced distinctly so the
    /// UI + audit can call out the escalation (M11.E.15b / M11.E.17).</summary>
    public bool IsModeEscalation
        => Violations.Any(v => v.Kind == AdhocViolationKind.ModeEscalation);

    /// <summary>One-line human-readable summary for logs + audit details.</summary>
    public string Summary => IsAllowed
        ? "Script passed the static-analysis gate."
        : string.Join("; ", Violations.Select(v => $"[{v.Kind}] L{v.Line}: {v.Message}"));
}

/// <summary>One reason a script was rejected by <see cref="AdhocScriptGate"/>.</summary>
public sealed record AdhocScriptViolation(
    AdhocViolationKind Kind,
    string Message,
    string? CommandName,
    int Line);

/// <summary>Classification of an <see cref="AdhocScriptViolation"/>.</summary>
public enum AdhocViolationKind
{
    /// <summary>Script failed to parse (fail-closed).</summary>
    ParseError,

    /// <summary>Command invoked dynamically; target unverifiable.</summary>
    DynamicInvocation,

    /// <summary>A mutating/non-allowlisted command in a readonly session.</summary>
    ModeEscalation,

    /// <summary>An always-forbidden cmdlet (e.g. Invoke-Expression, Add-Type).</summary>
    ForbiddenCmdlet,

    /// <summary>Invoke-Command with a remoting target.</summary>
    ForbiddenRemoting,

    /// <summary>Remove-Item -Recurse -Force.</summary>
    DestructiveDelete,

    /// <summary>Service install / uninstall.</summary>
    ServiceLifecycle,

    /// <summary>A registry write.</summary>
    RegistryWrite,
}

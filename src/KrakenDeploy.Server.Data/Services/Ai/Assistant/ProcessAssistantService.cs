using System.Globalization;
using System.IO.Compression;
using System.Text;
using KrakenDeploy.Ai;
using KrakenDeploy.Server.Core.Domain.Packages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services.Ai.Assistant;

/// <summary>
/// M11.D — the process-builder AI assistant backend. Three operations, all
/// routed through <see cref="IKrakenAi"/> under
/// <see cref="KrakenAiFeature.Assistant"/> (so the per-Space AssistantEnabled
/// flag + budget + audit apply uniformly):
/// <list type="bullet">
///   <item><see cref="SuggestStepsAsync"/> — inspects a package's layout and
///         proposes a starter step list (one-shot, structured output).</item>
///   <item><see cref="ExplainFieldAsync"/> — explains a single step-config
///         field in context (one-shot text).</item>
///   <item><see cref="StreamScriptSuggestionAsync"/> — streams script
///         suggestions for the script-editor sidebar.</item>
/// </list>
/// <para>
/// AI-unavailable exceptions (disabled / feature-off / budget) bubble to the
/// caller — the UI surfaces them as "assistant not available" rather than a
/// hard error. The service never throws for an absent package etc.; it
/// returns a clear null/empty result.
/// </para>
/// </summary>
public sealed class ProcessAssistantService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    IPackageStore packageStore,
    IKrakenAi ai,
    ILogger<ProcessAssistantService> logger)
{
    private const int MaxLayoutEntries = 60;

    /// <summary>
    /// Suggests a starter process for the package identified by
    /// <paramref name="packageId"/> + <paramref name="version"/>. Returns
    /// null when the package isn't found. Throws the AI-unavailable
    /// exceptions for the caller to surface.
    /// </summary>
    public async Task<StepSuggestionResult?> SuggestStepsAsync(
        string packageId, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var pkg = await db.Packages.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PackageId == packageId && p.Version == version, ct)
            .ConfigureAwait(false);
        if (pkg is null)
        {
            return null;
        }

        var layout = await ReadLayoutSignalsAsync(pkg, ct).ConfigureAwait(false);

        var prompt = new StringBuilder();
        prompt.AppendLine(CultureInfo.InvariantCulture,
            $"Package: {pkg.PackageId} version {pkg.Version} (file: {pkg.FileName}).");
        prompt.AppendLine("Top-level layout + notable files:");
        foreach (var entry in layout)
        {
            prompt.AppendLine(CultureInfo.InvariantCulture, $"- {entry}");
        }
        prompt.AppendLine();
        prompt.AppendLine(
            "Propose a concise starter deployment process for this package. " +
            "Prefer the fewest steps that cover the obvious deployment shape " +
            "(e.g. an ASP.NET site → IIS step; a *.service / Windows service " +
            "exe → Windows Service step; loose scripts → a script step). If " +
            "the layout is ambiguous, return an empty step list and say so in " +
            "the rationale.");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "You are a deployment-process designer. You see a package's file layout " +
                "and propose KrakenDeploy steps. Be conservative: only suggest steps the " +
                "layout clearly supports."),
            new(ChatRole.User, prompt.ToString()),
        };

        return await ai.CompleteAsync<StepSuggestionResult>(
            messages,
            KrakenAiFeature.Assistant,
            new KrakenAiRequestOptions { Temperature = 0.2f, MaxOutputTokens = 700 },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Explains a single step-config field in plain language. One-shot;
    /// callers (the UI) cache per page session. Throws the AI-unavailable
    /// exceptions for the caller to surface.
    /// </summary>
    public async Task<string> ExplainFieldAsync(
        string stepType, string fieldKey, string? label, string? helpText,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepType);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldKey);

        var prompt =
            $"Step type: {stepType}\n" +
            $"Field key: {fieldKey}\n" +
            $"Field label: {label ?? "(none)"}\n" +
            $"Existing help text: {helpText ?? "(none)"}\n\n" +
            "Explain in 2–4 sentences what this field controls, what a typical " +
            "value looks like, and one common pitfall. Be concrete and concise; " +
            "don't repeat the field label verbatim.";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "You explain deployment-tool configuration fields to operators clearly and concisely."),
            new(ChatRole.User, prompt),
        };

        var completion = await ai.CompleteAsync(
            messages,
            KrakenAiFeature.Assistant,
            new KrakenAiRequestOptions { Temperature = 0.1f, MaxOutputTokens = 250 },
            ct).ConfigureAwait(false);
        return completion.Text;
    }

    /// <summary>
    /// Streams script suggestions for the script-editor sidebar. The caller
    /// passes the current script body + light context (target type, available
    /// variable names — already sanitised by the caller); we stream the
    /// model's response chunks for "typing" feedback. Throws the
    /// AI-unavailable exceptions before the first chunk if the feature is off.
    /// </summary>
    public IAsyncEnumerable<string> StreamScriptSuggestionAsync(
        string userRequest, string? currentScript, string? contextNote,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userRequest);

        var prompt = new StringBuilder();
        prompt.AppendLine(userRequest);
        if (!string.IsNullOrWhiteSpace(contextNote))
        {
            prompt.AppendLine();
            prompt.AppendLine(CultureInfo.InvariantCulture, $"Context: {contextNote}");
        }
        if (!string.IsNullOrWhiteSpace(currentScript))
        {
            prompt.AppendLine();
            prompt.AppendLine("Current script:");
            prompt.AppendLine("```");
            prompt.AppendLine(currentScript);
            prompt.AppendLine("```");
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "You are a deployment-script assistant. Produce correct, minimal PowerShell " +
                "(or Bash if the user asks) for KrakenDeploy script steps. Explain only briefly; " +
                "favour the code."),
            new(ChatRole.User, prompt.ToString()),
        };

        return ai.StreamChatAsync(
            messages,
            KrakenAiFeature.Assistant,
            new KrakenAiRequestOptions { Temperature = 0.2f, MaxOutputTokens = 800 },
            ct);
    }

    /// <summary>
    /// Reads up to <see cref="MaxLayoutEntries"/> layout signals from the
    /// package zip: top-level directory names + notable files (project files,
    /// web.config, service exes, appsettings, Dockerfile, wwwroot presence).
    /// Best-effort — a non-zip or unreadable package yields an empty list +
    /// a note rather than throwing.
    /// </summary>
    private async Task<List<string>> ReadLayoutSignalsAsync(Package pkg, CancellationToken ct)
    {
        try
        {
            await using var stream = await packageStore.OpenReadAsync(pkg.StoredPath, ct)
                .ConfigureAwait(false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

            var topDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var notableFiles = new List<string>();
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                var firstSeg = name.Split('/', 2)[0];
                if (name.Contains('/') && !string.IsNullOrEmpty(firstSeg))
                {
                    topDirs.Add(firstSeg + "/");
                }

                var fileName = Path.GetFileName(name);
                if (IsNotable(fileName) && notableFiles.Count < MaxLayoutEntries)
                {
                    notableFiles.Add(name);
                }
            }

            var result = new List<string>();
            result.AddRange(topDirs.OrderBy(d => d).Take(MaxLayoutEntries));
            result.AddRange(notableFiles);
            if (result.Count == 0)
            {
                result.Add("(package is empty or contains no recognisable layout signals)");
            }
            return result.Take(MaxLayoutEntries).ToList();
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or IOException)
        {
            logger.LogWarning(ex,
                "Could not read package layout for {PackageId} v{Version}; suggesting from metadata only.",
                pkg.PackageId, pkg.Version);
            return ["(package contents could not be read — suggest from the package name only)"];
        }
    }

    private static bool IsNotable(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }
        if (fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return fileName.ToLowerInvariant() switch
        {
            "web.config" or "appsettings.json" or "dockerfile" or "global.asax"
            or "package.json" or "nginx.conf" => true,
            _ => fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
        };
    }
}

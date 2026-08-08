using KrakenDeploy.Contracts.Steps;

namespace KrakenDeploy.Steps.TransferPackage;

public static class TransferPackageConfigKeys
{
    private const string Prefix = "Octopus.Action.TransferPackage.";

    public const string DestinationType = Prefix + "DestinationType";
    public const string DestinationPath = Prefix + "DestinationPath";
    public const string DestinationUrl = Prefix + "DestinationUrl";
    public const string DestinationUsername = Prefix + "DestinationUsername";
    public const string DestinationPassword = Prefix + "DestinationPassword";
    public const string FileNamePattern = Prefix + "FileNamePattern";
}

public sealed class TransferPackageStepHandler : IStepHandler
{
    public bool CanHandle(string stepType)
        => stepType.Equals("Octopus.TransferPackage", StringComparison.OrdinalIgnoreCase);

    public bool RequiresPackage => true;

    public async Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct)
    {
        var destType = Get(context, TransferPackageConfigKeys.DestinationType) ?? "file";
        var isHttp = destType.Equals("http", StringComparison.OrdinalIgnoreCase)
            || destType.Equals("feed", StringComparison.OrdinalIgnoreCase);

        if (isHttp)
        {
            return await TransferToHttpAsync(context, ct).ConfigureAwait(false);
        }

        return await TransferToFileAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<bool> TransferToFileAsync(StepHandlerContext context, CancellationToken ct)
    {
        var destPath = Get(context, TransferPackageConfigKeys.DestinationPath);
        if (string.IsNullOrWhiteSpace(destPath))
        {
            await context.LogAsync("error",
                "Octopus.Action.TransferPackage.DestinationPath is required for file transfers.")
                .ConfigureAwait(false);
            return false;
        }

        var pattern = Get(context, TransferPackageConfigKeys.FileNamePattern);
        var files = CollectFiles(context.ExtractDir, pattern);
        if (files.Count == 0)
        {
            await context.LogAsync("error",
                $"No files matched pattern '{pattern ?? "**/*"}' in the package extract directory.")
                .ConfigureAwait(false);
            return false;
        }

        Directory.CreateDirectory(destPath);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(context.ExtractDir, file);
            var target = Path.Combine(destPath, relative);
            var targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            File.Copy(file, target, overwrite: true);
            await context.LogAsync("info", $"Copied {relative} → {target}").ConfigureAwait(false);
        }

        await context.LogAsync("info",
            $"Transferred {files.Count} file(s) to {destPath}").ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> TransferToHttpAsync(StepHandlerContext context, CancellationToken ct)
    {
        var url = Get(context, TransferPackageConfigKeys.DestinationUrl);
        if (string.IsNullOrWhiteSpace(url))
        {
            await context.LogAsync("error",
                "Octopus.Action.TransferPackage.DestinationUrl is required for HTTP/feed transfers.")
                .ConfigureAwait(false);
            return false;
        }

        var username = Get(context, TransferPackageConfigKeys.DestinationUsername);
        var password = Get(context, TransferPackageConfigKeys.DestinationPassword);

        using var handler = new HttpClientHandler();
        if (!string.IsNullOrEmpty(username))
        {
            handler.Credentials = new System.Net.NetworkCredential(username, password);
            handler.PreAuthenticate = true;
        }

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };

        var pattern = Get(context, TransferPackageConfigKeys.FileNamePattern);
        var files = CollectFiles(context.ExtractDir, pattern);
        if (files.Count == 0)
        {
            await context.LogAsync("error",
                $"No files matched pattern '{pattern ?? "**/*"}' in the package extract directory.")
                .ConfigureAwait(false);
            return false;
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(file);
            var uploadUrl = url.TrimEnd('/') + "/" + Uri.EscapeDataString(fileName);

            await context.LogAsync("info", $"Uploading {fileName} → {uploadUrl}").ConfigureAwait(false);

            using var content = new StreamContent(File.OpenRead(file));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                "application/octet-stream");

            using var resp = await http.PutAsync(uploadUrl, content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                await context.LogAsync("error",
                    $"Upload of {fileName} failed: {(int)resp.StatusCode} {resp.ReasonPhrase}")
                    .ConfigureAwait(false);
                return false;
            }
        }

        await context.LogAsync("info",
            $"Transferred {files.Count} file(s) to {url}").ConfigureAwait(false);
        return true;
    }

    private static List<string> CollectFiles(string root, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "**/*")
        {
            return [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)];
        }

        var results = new List<string>();
        foreach (var p in pattern.Split(['\n', ',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            results.AddRange(Directory.EnumerateFiles(root, p, SearchOption.AllDirectories));
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? Get(StepHandlerContext context, string key)
        => context.Step.Config.GetValueOrDefault(key);
}

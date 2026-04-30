using System.CommandLine;
using System.IO.Compression;

namespace KrakenDeploy.Cli.Commands;

/// <summary>
/// <c>kraken package create &lt;dir&gt;</c>  — zips a directory.<br/>
/// <c>kraken package upload &lt;file&gt;</c> — uploads a zip to the server.
/// </summary>
public static class PackageCommands
{
    public static Command Build(Option<string> serverOption, Option<string> apiKeyOption)
    {
        var packageCommand = new Command("package", "Manage deployment packages.");

        packageCommand.AddCommand(BuildCreate());
        packageCommand.AddCommand(BuildUpload(serverOption, apiKeyOption));

        return packageCommand;
    }

    // ── package create ────────────────────────────────────────────────────────

    private static Command BuildCreate()
    {
        var dirArg     = new Argument<DirectoryInfo>("directory", "Directory to zip.");
        var outputOpt  = new Option<FileInfo?>(
            ["--output", "-o"],
            "Output zip path. Defaults to <directory-name>.zip in the current directory.");

        var cmd = new Command("create", "Zip a directory into a deployable package.");
        cmd.AddArgument(dirArg);
        cmd.AddOption(outputOpt);

        cmd.SetHandler((dir, output) =>
        {
            if (!dir.Exists)
            {
                Console.Error.WriteLine($"Directory not found: {dir.FullName}");
                return Task.FromResult(1);
            }

            var destPath = output?.FullName
                ?? Path.Combine(Directory.GetCurrentDirectory(), dir.Name + ".zip");

            if (File.Exists(destPath))
            {
                File.Delete(destPath);
            }

            ZipFile.CreateFromDirectory(dir.FullName, destPath, CompressionLevel.Optimal, false);

            var sizeKb = new FileInfo(destPath).Length / 1024.0;
            Console.WriteLine($"Created {destPath} ({sizeKb:F1} KB)");
            return Task.FromResult(0);
        }, dirArg, outputOpt);

        return cmd;
    }

    // ── package upload ────────────────────────────────────────────────────────

    private static Command BuildUpload(Option<string> serverOption, Option<string> apiKeyOption)
    {
        var fileArg      = new Argument<FileInfo>("file", "Zip file to upload.");
        var packageIdOpt = new Option<string>("--package-id", "Package identifier (e.g. MyApp.Api).")
        {
            IsRequired = true,
        };
        var versionOpt   = new Option<string>("--version", "Semantic version (e.g. 1.2.3).")
        {
            IsRequired = true,
        };

        var cmd = new Command("upload", "Upload a package zip to the server.");
        cmd.AddArgument(fileArg);
        cmd.AddOption(packageIdOpt);
        cmd.AddOption(versionOpt);

        cmd.SetHandler(async (file, packageId, version, server, apiKey) =>
        {
            if (!file.Exists)
            {
                Console.Error.WriteLine($"File not found: {file.FullName}");
                return;
            }

            using var client = new KrakenApiClient(server, apiKey);
            try
            {
                Console.WriteLine($"Uploading {file.Name} ({file.Length / 1024.0:F1} KB)…");
                var result = await client.UploadPackageAsync(packageId, version, file.FullName)
                    .ConfigureAwait(false);
                Console.WriteLine($"✓  Uploaded {packageId} v{version}");
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Upload failed: {ex.Message}");
            }
        }, fileArg, packageIdOpt, versionOpt, serverOption, apiKeyOption);

        return cmd;
    }
}

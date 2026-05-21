using System.CommandLine;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using KrakenDeploy.Contracts.StepPackages;

namespace KrakenDeploy.Cli.Commands;

/// <summary>
/// <c>kraken pack &lt;input&gt; [--key signing.key] [--output ./out.kdeploy-step] [--configuration Release]</c>
///   — builds (if input is a <c>.csproj</c>) and optionally signs a step-package
///   <c>.kdeploy-step</c> archive.
/// <para>
/// Two modes, dispatched by file extension:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <c>.csproj</c> — runs <c>dotnet build -c $(configuration)</c>,
///       finds the resulting <c>{id}-{version}.kdeploy-step</c> in the
///       project's build output, then (if <c>--key</c> was passed) re-signs
///       the manifest inside the zip with the supplied private RSA key.
///       Build output is dispatched to the console verbatim so MSBuild errors
///       remain visible.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>.kdeploy-step</c> — skips the build phase and just re-signs in
///       place (or writes to <c>--output</c>).
///     </description>
///   </item>
/// </list>
/// <para>
/// Without <c>--key</c> the command is a no-op build (or a no-op on an
/// existing archive); the dev-sentinel signature emitted by
/// <c>KrakenStepPackage.targets</c> stays untouched. This keeps the local
/// iteration loop short — only the production release path needs the
/// signing key.
/// </para>
/// </summary>
public static class PackCommands
{
    public static Command Build()
    {
        var inputArg = new Argument<FileSystemInfo>(
            "input",
            "Path to a .csproj (will be built first) or an existing .kdeploy-step archive.");

        var keyOpt = new Option<FileInfo?>(
            ["--key", "-k"],
            "PEM file with the RSA private key to sign with. Omit to skip signing.");

        var outputOpt = new Option<FileInfo?>(
            ["--output", "-o"],
            "Output .kdeploy-step path. Defaults to in-place rewrite of the built archive.");

        var configurationOpt = new Option<string>(
            ["--configuration", "-c"],
            () => "Release",
            "MSBuild configuration when input is a .csproj. Defaults to Release.");

        var cmd = new Command(
            "pack",
            "Build (if needed) and optionally sign a step-package .kdeploy-step.");
        cmd.AddArgument(inputArg);
        cmd.AddOption(keyOpt);
        cmd.AddOption(outputOpt);
        cmd.AddOption(configurationOpt);

        cmd.SetHandler(
            async (input, key, output, configuration) =>
            {
                var exitCode = await RunAsync(input, key, output, configuration)
                    .ConfigureAwait(false);
                Environment.ExitCode = exitCode;
            },
            inputArg, keyOpt, outputOpt, configurationOpt);

        return cmd;
    }

    /// <summary>
    /// Test-friendly entry point — same code path the CLI handler uses.
    /// Returns the process exit code (0 on success, 1 on user error,
    /// non-zero on MSBuild failure).
    /// </summary>
    public static async Task<int> RunAsync(
        FileSystemInfo input,
        FileInfo?      keyFile,
        FileInfo?      output,
        string         configuration)
    {
        if (!input.Exists)
        {
            Console.Error.WriteLine($"Input not found: {input.FullName}");
            return 1;
        }

        string archive;
        if (string.Equals(input.Extension, ".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var built = await BuildProjectAsync((FileInfo)input, configuration)
                .ConfigureAwait(false);
            if (built is null)
            {
                return 2;
            }
            archive = built;
        }
        else if (string.Equals(
            input.Extension, StepPackageFiles.Extension, StringComparison.OrdinalIgnoreCase))
        {
            archive = input.FullName;
        }
        else
        {
            Console.Error.WriteLine(
                $"Unrecognised input '{input.Name}'. Expected .csproj or {StepPackageFiles.Extension}.");
            return 1;
        }

        var destPath = output?.FullName ?? archive;

        if (keyFile is null)
        {
            if (!string.Equals(destPath, archive, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(archive, destPath, overwrite: true);
            }
            Console.WriteLine($"Built {destPath}");
            Console.WriteLine("(Not signed — pass --key <signing.pem> to sign for production use.)");
            return 0;
        }

        if (!keyFile.Exists)
        {
            Console.Error.WriteLine($"Signing key not found: {keyFile.FullName}");
            return 1;
        }

        try
        {
            SignArchive(archive, destPath, keyFile.FullName);
            Console.WriteLine($"Signed {destPath}");
            return 0;
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException or FileNotFoundException)
        {
            Console.Error.WriteLine($"Signing failed: {ex.Message}");
            return 3;
        }
    }

    // ── Build ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Invokes <c>dotnet build</c> on <paramref name="project"/> and locates
    /// the <c>.kdeploy-step</c> in the project's output directory. Returns
    /// <c>null</c> when the build fails or the archive can't be located.
    /// </summary>
    private static async Task<string?> BuildProjectAsync(FileInfo project, string configuration)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "dotnet",
            WorkingDirectory       = project.DirectoryName ?? Environment.CurrentDirectory,
            RedirectStandardOutput = false,
            RedirectStandardError  = false,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(project.FullName);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(configuration);
        psi.ArgumentList.Add("--nologo");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start `dotnet build`.");
        await proc.WaitForExitAsync().ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            Console.Error.WriteLine($"`dotnet build` failed with exit code {proc.ExitCode}.");
            return null;
        }

        // Look under bin/$(configuration)/*/*.kdeploy-step — TFM subdir varies
        // (`net10.0`, `net10.0-windows`, etc.). We pick the newest match so a
        // multi-target project (rare for step packages, but allowed) still
        // signs the freshly-built one.
        var binDir = Path.Combine(project.DirectoryName!, "bin", configuration);
        if (!Directory.Exists(binDir))
        {
            Console.Error.WriteLine($"Build output directory not found: {binDir}");
            return null;
        }
        var archive = new DirectoryInfo(binDir)
            .EnumerateFiles($"*{StepPackageFiles.Extension}", SearchOption.AllDirectories)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (archive is null)
        {
            Console.Error.WriteLine(
                $"No {StepPackageFiles.Extension} archive found under {binDir}. " +
                "Ensure the project imports KrakenStepPackage.targets and sets " +
                "KrakenStepPackageId / KrakenStepPackageVersion.");
            return null;
        }
        return archive.FullName;
    }

    // ── Sign ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rewrites the manifest entry of <paramref name="sourceArchive"/> with a
    /// signature produced from <paramref name="pemKeyPath"/>. Public for tests.
    /// </summary>
    /// <remarks>
    /// Recipe (matches <see cref="StepPackageSigner"/>):
    /// <list type="number">
    ///   <item><description>Read manifest.json from the zip.</description></item>
    ///   <item><description>Extract executor/<c>ExecutorAssembly</c> to a temp file (so the signer can hash it).</description></item>
    ///   <item><description>Call <c>StepPackageSigner.Sign</c>.</description></item>
    ///   <item><description>Copy source → dest (if different) and overwrite the manifest entry.</description></item>
    /// </list>
    /// In-place signing is supported (<paramref name="destArchive"/> == <paramref name="sourceArchive"/>):
    /// the zip is opened for Update directly.
    /// </remarks>
    public static void SignArchive(string sourceArchive, string destArchive, string pemKeyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceArchive);
        ArgumentException.ThrowIfNullOrWhiteSpace(destArchive);
        ArgumentException.ThrowIfNullOrWhiteSpace(pemKeyPath);

        if (!File.Exists(sourceArchive))
        {
            throw new FileNotFoundException($"Source archive not found: {sourceArchive}", sourceArchive);
        }
        if (!File.Exists(pemKeyPath))
        {
            throw new FileNotFoundException($"PEM key not found: {pemKeyPath}", pemKeyPath);
        }

        var pem = File.ReadAllText(pemKeyPath);
        using var key = StepPackageSigner.ImportPrivateKeyFromPem(pem);

        if (!string.Equals(sourceArchive, destArchive, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourceArchive, destArchive, overwrite: true);
        }

        // Stage the executor DLL on disk so the signer can hash it. The
        // canonical recipe folds the DLL's SHA into the signature input;
        // anything that produces the same bytes works, but Sign()'s API is
        // path-based.
        using var workspace = new TempWorkspace();

        StepPackageManifest manifest;
        string             executorDllPath;
        using (var read = ZipFile.OpenRead(destArchive))
        {
            var manifestEntry = read.GetEntry(StepPackageFiles.ManifestFileName)
                ?? throw new InvalidDataException(
                    $"Archive '{destArchive}' is missing {StepPackageFiles.ManifestFileName}.");
            using var rs = manifestEntry.Open();
            using var sr = new StreamReader(rs);
            manifest = StepPackageManifestJson.Deserialize(sr.ReadToEnd());

            var executorEntry = read.GetEntry(
                $"{StepPackageFiles.ExecutorDirectory}/{manifest.ExecutorAssembly}")
                ?? throw new InvalidDataException(
                    $"Archive '{destArchive}' is missing executor DLL '{manifest.ExecutorAssembly}'.");
            executorDllPath = Path.Combine(workspace.Path, manifest.ExecutorAssembly);
            using var dllFs = File.Create(executorDllPath);
            using var src   = executorEntry.Open();
            src.CopyTo(dllFs);
        }

        var signed     = StepPackageSigner.Sign(manifest, executorDllPath, key);
        signed         = signed with { SignedBy = manifest.SignedBy ?? "kraken-project" };
        var signedJson = StepPackageManifestJson.Serialize(signed);

        // Now rewrite the manifest entry. Open for Update — the zip stays
        // valid mid-flight (executor DLL never gets touched, so existing
        // signatures of other tooling against the same archive would still
        // bind to the same bytes).
        using var write       = ZipFile.Open(destArchive, ZipArchiveMode.Update);
        var       oldManifest = write.GetEntry(StepPackageFiles.ManifestFileName);
        oldManifest?.Delete();
        var newManifest = write.CreateEntry(StepPackageFiles.ManifestFileName);
        using var ws = newManifest.Open();
        using var sw = new StreamWriter(ws);
        sw.Write(signedJson);
    }

    /// <summary>
    /// Cleanup helper for the per-operation temp dir that stages the executor
    /// DLL outside the zip.
    /// </summary>
    private sealed class TempWorkspace : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"kraken-pack-{Guid.NewGuid():N}");

        public TempWorkspace()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}

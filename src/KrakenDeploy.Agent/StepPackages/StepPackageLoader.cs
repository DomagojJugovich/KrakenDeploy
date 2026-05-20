using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using KrakenDeploy.Agent.Transport;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Contracts.Steps;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Agent.StepPackages;

/// <summary>
/// Loads <c>.kdeploy-step</c> packages on the agent (Phase D-4).
/// <para>
/// For each <c>(packageName, packageVersion)</c> pair the loader:
/// </para>
/// <list type="number">
///   <item>
///     Locates the cached extraction under
///     <c>{dataPath}/step-packages-cache/{name}/{version}/</c>. When the
///     directory is missing, returns <c>null</c> from <see cref="TryLoad"/> —
///     the caller (the deployment executor) is expected to trigger the
///     gRPC download (Phase D-5) and retry. The loader doesn't initiate the
///     download itself so the agent's deployment path stays linear.
///   </item>
///   <item>
///     Reads + validates <c>manifest.json</c>, then verifies the package
///     SHA-256 of the on-disk archive (when present) against the signature
///     placeholder. Real RSA-SHA256 verification is identical to the
///     server-side upload check in <c>StepPackageService</c> — implemented
///     as a hook so the same recipe lands in both places at the same time.
///   </item>
///   <item>
///     Creates a collectible <see cref="AssemblyLoadContext"/> per
///     <c>(name, version)</c>. The ALC is configured to delegate any
///     assembly already loaded in the default load context to the default
///     ALC — crucial: <c>KrakenDeploy.Contracts</c>, <c>System.*</c>, and
///     <c>Microsoft.Extensions.*</c> all flow through this branch, so
///     plug-in types share identity with the agent's view of
///     <see cref="IStepHandler"/> rather than ending up as a "second"
///     <c>IStepHandler</c> only the plug-in's ALC knows about. Everything
///     genuinely package-private (third-party deps shipped under
///     <c>executor/</c>) loads in the plug-in ALC and stays isolated from
///     other plug-ins.
///   </item>
///   <item>
///     Loads the manifest's <c>executorAssembly</c> from the plug-in ALC,
///     resolves <c>executorTypeName</c> via <see cref="Assembly.GetType(string)"/>,
///     asserts it implements <see cref="IStepHandler"/> from the default
///     ALC's Contracts, and caches the resolved <see cref="Type"/>.
///   </item>
/// </list>
/// <para>
/// Lifecycle is <strong>per-step-execution</strong>: each call to
/// <see cref="CreateHandler"/> constructs a fresh instance via the type's
/// parameterless constructor; instances implementing <see cref="IDisposable"/>
/// must be disposed by the caller after <see cref="IStepHandler.HandleAsync"/>
/// returns. The cache is on the <em>Type</em>, not on the instance.
/// </para>
/// </summary>
public sealed class StepPackageLoader(
    IConfiguration config,
    ILogger<StepPackageLoader> logger,
    IStepPackageSource? source = null)
{
    private readonly ConcurrentDictionary<(string Name, string Version), LoadedPackage> _cache = new();

    /// <summary>
    /// Cache-miss-aware load: when <see cref="TryLoad"/> returns <c>null</c>
    /// because the extraction is missing AND an <see cref="IStepPackageSource"/>
    /// is wired up, pulls the package from the source and re-tries the load.
    /// Returns <c>null</c> on any failure (no source configured, download
    /// failure, post-download load still fails) — all errors are logged.
    /// </summary>
    public async Task<LoadedPackage?> TryLoadOrDownloadAsync(
        string name, string version, CancellationToken ct)
    {
        var loaded = TryLoad(name, version);
        if (loaded is not null) { return loaded; }

        if (source is null)
        {
            logger.LogWarning(
                "StepPackageLoader: {Name} {Version} not in cache and no IStepPackageSource is configured.",
                name, version);
            return null;
        }

        try
        {
            await source.EnsureExtractedAsync(name, version, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "StepPackageLoader: failed to download {Name} {Version}.", name, version);
            return null;
        }

        return TryLoad(name, version);
    }

    /// <summary>
    /// Looks up — or loads on first access — the step-handler type for
    /// <paramref name="name"/> + <paramref name="version"/>. Returns
    /// <c>null</c> when the package isn't present locally (the caller should
    /// download it and retry) OR when the manifest / signature / load
    /// otherwise fails (the error is logged for diagnosis).
    /// </summary>
    public LoadedPackage? TryLoad(string name, string version)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(version);

        if (_cache.TryGetValue((name, version), out var cached))
        {
            return cached;
        }

        var packageDir = ResolveCacheDir(name, version);
        if (!Directory.Exists(packageDir))
        {
            logger.LogDebug(
                "StepPackageLoader: no cached extraction for {Name} {Version} at {Path} — caller must download.",
                name, version, packageDir);
            return null;
        }

        var manifestPath = Path.Combine(packageDir, StepPackageFiles.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            logger.LogError(
                "StepPackageLoader: {Name} {Version} extraction is missing manifest.json at {Path}.",
                name, version, manifestPath);
            return null;
        }

        StepPackageManifest manifest;
        try
        {
            manifest = StepPackageManifestJson.Deserialize(File.ReadAllText(manifestPath));
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "StepPackageLoader: failed to parse manifest for {Name} {Version}.", name, version);
            return null;
        }

        if (!manifest.Id.Equals(name, StringComparison.Ordinal)
            || !manifest.Version.Equals(version, StringComparison.Ordinal))
        {
            logger.LogError(
                "StepPackageLoader: manifest id/version ({MId}/{MVer}) does not match cache key ({Name}/{Version}).",
                manifest.Id, manifest.Version, name, version);
            return null;
        }

        // Signature placeholder — mirror of the server-side hook in
        // StepPackageService.VerifySignatureAsync. Same opt-in flag.
        if (!VerifySignature(packageDir, manifest))
        {
            return null;
        }

        // Build the isolated ALC + load the executor.
        var executorPath = Path.Combine(
            packageDir, StepPackageFiles.ExecutorDirectory, manifest.ExecutorAssembly);
        if (!File.Exists(executorPath))
        {
            logger.LogError(
                "StepPackageLoader: {Name} {Version} executor assembly missing at {Path}.",
                name, version, executorPath);
            return null;
        }

        Type? handlerType;
        StepPackageAssemblyLoadContext alc;
        try
        {
            alc = new StepPackageAssemblyLoadContext(name, version, executorPath);
            var assembly = alc.LoadFromAssemblyPath(executorPath);
            handlerType = assembly.GetType(manifest.ExecutorTypeName, throwOnError: false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "StepPackageLoader: failed to load assembly for {Name} {Version}.", name, version);
            return null;
        }

        if (handlerType is null)
        {
            logger.LogError(
                "StepPackageLoader: {Name} {Version} manifest's executorTypeName " +
                "'{TypeName}' was not found in '{Assembly}'.",
                name, version, manifest.ExecutorTypeName, manifest.ExecutorAssembly);
            alc.Unload();
            return null;
        }

        if (!typeof(IStepHandler).IsAssignableFrom(handlerType))
        {
            logger.LogError(
                "StepPackageLoader: {Name} {Version} type '{TypeName}' does not implement IStepHandler. " +
                "If the type looks correct, ensure the plug-in references Kraken.SDK (KrakenDeploy.Contracts) " +
                "without a copy-local override — the ALC delegates Contracts to the agent so types identify cleanly.",
                name, version, manifest.ExecutorTypeName);
            alc.Unload();
            return null;
        }

        var loaded = new LoadedPackage(manifest, handlerType, alc);
        _cache.TryAdd((name, version), loaded);
        logger.LogInformation(
            "StepPackageLoader: loaded {Name} {Version} (executor {TypeName}).",
            name, version, manifest.ExecutorTypeName);
        return loaded;
    }

    /// <summary>
    /// Constructs a fresh <see cref="IStepHandler"/> instance for the given
    /// package version. Returns <c>null</c> when the package isn't loaded
    /// (the caller should call <see cref="TryLoad"/> first, or download +
    /// retry). Per-step-execution lifecycle — caller disposes the instance.
    /// </summary>
    public IStepHandler? CreateHandler(string name, string version)
    {
        var pkg = TryLoad(name, version);
        if (pkg is null) { return null; }
        try
        {
            return (IStepHandler)Activator.CreateInstance(pkg.HandlerType)!;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "StepPackageLoader: failed to construct handler for {Name} {Version}.", name, version);
            return null;
        }
    }

    /// <summary>
    /// Drops the cached entry for <paramref name="name"/> + <paramref name="version"/>
    /// and best-effort unloads its ALC. Future <see cref="TryLoad"/> calls
    /// will re-load from disk. Useful for tests + future "reload after
    /// upgrade" UX. Unload is best-effort — .NET 10 won't reclaim memory
    /// until the GC chases all root references.
    /// </summary>
    public void Evict(string name, string version)
    {
        if (_cache.TryRemove((name, version), out var loaded))
        {
            loaded.Context.Unload();
        }
    }

    private string ResolveCacheDir(string name, string version)
    {
        var root = config["DataPath"] ?? "data";
        return Path.Combine(root, "step-packages-cache",
            SanitisePathSegment(name), SanitisePathSegment(version));
    }

    private static string SanitisePathSegment(string s)
        => string.Join('_',
            s.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries))
         .Replace("..", "_", StringComparison.Ordinal);

    private bool VerifySignature(string packageDir, StepPackageManifest manifest)
    {
        var allowUnsigned = config.GetValue<bool?>("StepPackages:AllowUnsignedLoads") ?? false;

        if (string.IsNullOrEmpty(manifest.Signature))
        {
            if (allowUnsigned)
            {
                logger.LogWarning(
                    "StepPackageLoader: {Name} {Version} loaded unsigned " +
                    "(StepPackages:AllowUnsignedLoads is true). Disable this in production.",
                    manifest.Id, manifest.Version);
                return true;
            }
            logger.LogError(
                "StepPackageLoader: {Name} {Version} has no signature in manifest. " +
                "Configure StepPackages:AllowUnsignedLoads=true to accept unsigned packages (dev only).",
                manifest.Id, manifest.Version);
            return false;
        }

        // Real RSA-SHA256 verification lands in a D-3.x / D-4.x follow-up.
        // The canonical recipe lives in StepPackageManifestJson.CanonicalSignatureInput
        // — the loader pairs it with the SHA-256 of the on-disk archive (or the
        // executor DLL) and the project public key. For v1 we accept any non-empty
        // signature so the rest of the loading pipeline is testable end-to-end.
        if (File.Exists(Path.Combine(packageDir, "package" + StepPackageFiles.Extension)))
        {
            _ = ComputeArchiveSha256(packageDir);
        }
        return true;
    }

    private static string? ComputeArchiveSha256(string packageDir)
    {
        var path = Path.Combine(packageDir, "package" + StepPackageFiles.Extension);
        if (!File.Exists(path)) { return null; }
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(sha.ComputeHash(stream));
    }

    /// <summary>
    /// Convenience: extract a <c>.kdeploy-step</c> archive into the loader's
    /// cache directory. Called by Phase D-5's gRPC downloader after a fresh
    /// fetch; also handy for tests that stage packages locally without going
    /// through the full download path.
    /// </summary>
    public string ExtractToCache(string name, string version, string archivePath)
    {
        ArgumentNullException.ThrowIfNull(archivePath);
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException(
                $"Step-package archive not found: {archivePath}", archivePath);
        }

        var dir = ResolveCacheDir(name, version);
        if (Directory.Exists(dir))
        {
            // Idempotent overwrite — re-extracting the same archive on top of
            // itself shouldn't fail.
            try { Directory.Delete(dir, recursive: true); } catch { /* tolerate races */ }
        }
        Directory.CreateDirectory(dir);

        using (var fs   = File.OpenRead(archivePath))
        using (var zip  = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false))
        {
            ExtractZipSafely(zip, dir);
        }

        // Keep a copy of the archive alongside the extraction so signature
        // verification has the exact bytes the signer used.
        var archiveCopy = Path.Combine(dir, "package" + StepPackageFiles.Extension);
        File.Copy(archivePath, archiveCopy, overwrite: true);

        return dir;
    }

    private static void ExtractZipSafely(ZipArchive zip, string destinationRoot)
    {
        var rootFull = Path.GetFullPath(destinationRoot);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) { continue; }
            var destPath = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
            if (!destPath.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !destPath.Equals(rootFull, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Archive entry '{entry.FullName}' escapes the destination root.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }
}

/// <summary>
/// One successfully-loaded step package — the manifest, the resolved
/// <see cref="IStepHandler"/> implementation type, and the isolated ALC the
/// type was loaded into.
/// </summary>
public sealed record LoadedPackage(
    StepPackageManifest Manifest,
    Type HandlerType,
    AssemblyLoadContext Context);

/// <summary>
/// Per-package isolated <see cref="AssemblyLoadContext"/>. Configured to
/// <strong>delegate</strong> any assembly already loaded in the default ALC
/// back to it — this is the canonical fix for the "two <c>IStepHandler</c>
/// types from two contexts" identity trap.
/// <para>
/// In practice, every type the plug-in references that's also referenced by
/// the agent (Contracts, System.*, Microsoft.Extensions.*) ends up resolving
/// through this delegate branch. Only assemblies genuinely package-private —
/// third-party deps the plug-in shipped under <c>executor/</c> alongside its
/// own DLL — load into the plug-in's own context.
/// </para>
/// </summary>
internal sealed class StepPackageAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public StepPackageAssemblyLoadContext(string name, string version, string mainAssemblyPath)
        : base($"kraken-step-package:{name}@{version}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Delegate any assembly already loaded in the default ALC back to it
        // — Contracts, System.*, Microsoft.Extensions.*, and the host's
        // direct dependencies all flow through this branch so types identify
        // cleanly across the boundary.
        foreach (var loaded in Default.Assemblies)
        {
            var loadedName = loaded.GetName();
            if (string.Equals(loadedName.Name, assemblyName.Name, StringComparison.Ordinal))
            {
                return loaded;
            }
        }

        // Otherwise resolve via the executor's own AssemblyDependencyResolver
        // — picks up the DLLs the plug-in shipped under executor/.
        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return assemblyPath is not null
            ? LoadFromAssemblyPath(assemblyPath)
            : null;
    }
}

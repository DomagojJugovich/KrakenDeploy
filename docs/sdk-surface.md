# Kraken.SDK public surface

> **Status:** Draft. Versioning policy locks at Phase D-2 when `Kraken.SDK`
> NuGet ships. Until then the surface listed here may shift; once the
> NuGet is public, changes follow additive-semver rules (breaking changes
> bump the major version of the SDK).

This document is the **plugin ABI** for `.kdeploy-step` packages
([Phase D](../TASKS.md#m104--schema-driven-step-ui--step-package-plugin-system)).
Anything in `KrakenDeploy.Contracts` not listed here is internal to the
agent + server and may be refactored without notice.

The `Kraken.SDK` NuGet (D-2) is published from the same source as
`KrakenDeploy.Contracts.dll` — it _is_ the Contracts assembly, packaged for
external authors with independent versioning.

## Stable types and members

### `KrakenDeploy.Contracts.IStepHandler`
The contract every executor implements.

```csharp
public interface IStepHandler
{
    bool CanHandle(string stepType);
    bool RequiresPackage { get; }
    Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct);
}
```

- `CanHandle` should agree with `manifest.json:stepTypes`. The loader uses
  the manifest to register the package; the C# logic gates per-call.
- `RequiresPackage` tells the agent's `DeploymentExecutor` to download +
  extract the step's primary package before calling `HandleAsync`. `false`
  for steps that have no package payload (`Octopus.Manual`,
  `Octopus.DeployRelease`).
- `HandleAsync` returns `true` on success. Exceptions are caught by the
  executor and treated as failures.
- **Lifecycle:** per-step-execution. A fresh instance is constructed via
  the type's parameterless constructor for every step call; instances
  implementing `IDisposable` are disposed after `HandleAsync` returns.

### `KrakenDeploy.Contracts.StepHandlerContext`
Per-step context handed to the executor.

```csharp
public sealed class StepHandlerContext
{
    public required DeploymentPlan Plan { get; init; }
    public required DeploymentStepPlan Step { get; init; }
    public required string ExtractDir { get; init; }       // package extract root
    public required string ArtifactsDir { get; init; }     // write here; auto-uploaded
    public required Func<string, string, Task> LogAsync { get; init; } // (level, message)
    public IReadOnlyDictionary<string, string> ReferencedPackagePaths { get; init; }
}
```

`LogAsync` is the canonical way to surface log lines in the deployment view.
Use levels `"info"` / `"warning"` / `"error"`. The agent intercepts
`##octopus[...]` markers in the stream — emit them via `LogAsync` if you
want to set output variables or create artifacts from a non-script handler
(but the dedicated APIs below are clearer).

### `KrakenDeploy.Contracts.DeploymentPlan` and `DeploymentStepPlan`
Read-only context records describing the deployment + the step. Both are
already documented inline in `DeploymentContracts.cs`. Notable fields:

- `DeploymentPlan.Variables : IReadOnlyDictionary<string,string>` — fully
  resolved (Octostache-substituted) deployment variables. Use these for any
  variable lookup; don't reach into the agent's env or run Octostache yourself.
- `DeploymentPlan.EnvironmentName : string` — the environment slug.
- `DeploymentStepPlan.Config : IReadOnlyDictionary<string,string>` — your
  step's snapshotted config bag. The schema you ship in `ui/ui-schema.json`
  describes the keys here.
- `DeploymentStepPlan.PackageId : string` and `PackageVersion : string` —
  the primary package the agent extracted to `ExtractDir` (when
  `RequiresPackage = true`).
- `DeploymentStepPlan.ReferencedPackages` — list of additional packages the
  user added via the "Referenced Packages" UI. Already extracted; their
  paths are in `StepHandlerContext.ReferencedPackagePaths` keyed by package
  name.

### `KrakenDeploy.Contracts.Steps.PackageReference`
The wire record for referenced packages. Round-trip-stable so step packages
can both read and produce them.

### `KrakenDeploy.Contracts.Steps.StepUiSchema` (and supporting types)

The Phase C schema IR is part of the SDK surface so step packages can
declare their UI either as embedded `ui-schema.json` or via C# attributes
(`[StepUiSchemaRoot]` / `[StepUiGroup]` / `[StepUiField]` /
`[StepUiEnum]` / `[StepUiVisibleWhen]`). Both paths are documented inline.

Included:
- `StepUiSchema`, `StepUiGroup`, `StepUiField`, `StepUiEnumValue`,
  `StepUiVisibleWhen`, `StepUiValidation`, `StepUiFieldType`.
- `StepUiWidgets` — canonical widget id constants. New widgets may be added
  in additive releases; renderers fall back to `text` for unknown values.
- `StepUiSchemaJson` — canonical JSON serializer (use it for the embedded
  `ui/ui-schema.json` file).
- `StepUiSchemaBuilder` — `FromType<T>()` + `FromJson()` for both authoring
  paths.
- `StepUiSchemaValidator` — `Validate` + `CoerceFromConfig` +
  `CoerceToConfig`.

### `KrakenDeploy.Contracts.Steps.KrakenScriptConfigKeys`

Constants for the Octopus-compatible script step keys. Re-export them when
your handler delegates to the script runner.

### `KrakenDeploy.Contracts.Steps.KrakenIisConfig` and `KrakenIisConfigKeys`

Strongly-typed view of the Kraken IIS step config. Step packages that
extend or compose with the IIS runtime use this surface. Includes the
sub-config records (`KrakenIisAppPool`, `KrakenIisAuthentication`,
`KrakenIisRecycle`, `KrakenIisRapidFail`, `KrakenIisDeploy`,
`KrakenIisHealthCheck`, `KrakenIisBinding`) and the sub-deployment records
(`KrakenIisWebApplicationConfig`, `KrakenIisVirtualDirectoryConfig`).

### `KrakenDeploy.Contracts.StepPackages.StepPackageManifest`

The on-disk manifest record. Packaging tools serialise it via
`StepPackageManifestJson.Serialize`; the server-side upload validator
deserializes via `StepPackageManifestJson.Deserialize`. The
`CanonicalSignatureInput` helper produces the byte sequence the signing
tool feeds to RSA-SHA256 along with the executor DLL hash.

### `KrakenDeploy.Contracts.StepPackages.StepPackageFiles`

Constants for the canonical filenames inside the `.kdeploy-step` zip
(`manifest.json`, `executor/`, `ui/ui-schema.json`, …).

## Internal / unstable

Everything else in `KrakenDeploy.Contracts` — for example, internal helpers
in `KrakenIisBinding.ParseAll`, `StepTemplateCategoryMap`, the legacy
`StepTemplateParameter` shape, gRPC-generated types — is **not** part of the
SDK surface. Plugins that take dependencies on them risk breaking on agent
upgrades and don't get the additive-semver guarantee. Use the listed types
above.

## Versioning policy (post-D-2)

- **PATCH** — pure bug fixes; no surface changes; safe to drop into any
  agent that supports the same MAJOR.
- **MINOR** — additive only. New types, new optional members, new widget
  ids. Existing plugin DLLs continue to load.
- **MAJOR** — breaking changes (rename / remove / signature change). The
  agent verifies `manifest.minKrakenAgent` against its own version; loads
  fail with a clear error when a plugin requires a newer SDK than the
  agent ships.

## Plugin loading model (preview — see D-4)

The agent creates a separate `AssemblyLoadContext` per `(packageId, version)`.
The ALC is configured to **delegate** these assemblies to the default
(agent) ALC so plugin types share identity with the agent:

- `KrakenDeploy.Contracts` (the SDK)
- `System.*`
- `Microsoft.Extensions.*`

Everything else inside `executor/` loads in the plugin ALC — keep
package-private deps under `executor/` and they won't collide with the
agent's deps. This avoids the classic "two `IStepHandler` types with the
same FQN" identity bug.

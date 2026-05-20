using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KrakenDeploy.Contracts.StepPackages;

/// <summary>
/// Metadata for a single <c>.kdeploy-step</c> package — the unit of step-type
/// distribution defined by Phase D of M10.4.
/// <para>
/// On-disk layout (the package is a zip):
/// </para>
/// <code>
///   /manifest.json     ← THIS record, serialised with <see cref="StepPackageManifestJson"/>
///   /executor/         ← C# IStepHandler implementation + its direct deps
///       Kraken.Steps.X.dll
///       *.dll          ← package-private deps
///   /ui/
///       ui-schema.json ← declarative UI schema (Phase C-1 / C-2)
///   /README.md         ← optional human docs
///   /CHANGELOG.md      ← optional release notes; surfaced in the editor's
///                        "Update available" diff
///   /logo.png          ← optional icon
/// </code>
/// <para>
/// Signature scope: the signature is computed over the canonical UTF-8 bytes
/// of <see cref="StepPackageManifestJson.Serialize"/> applied to a "naked"
/// copy of this manifest with <see cref="Signature"/> blanked out, concatenated
/// with the SHA-256 of the executor DLL. This makes both the metadata and
/// the executable subject to verification while staying easy to compute
/// independently from any one tool.
/// </para>
/// </summary>
public sealed record StepPackageManifest
{
    /// <summary>
    /// Stable identifier — dotted lower-case (e.g. <c>kraken.iis</c>,
    /// <c>kraken.steps.aws-s3-upload</c>). Same as the schema root id; the
    /// renderer keys built-in / installed schemas by this value too.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Package version (semver — <c>MAJOR.MINOR.PATCH</c>, optional
    /// <c>-prerelease</c>). Multi-version coexistence on disk is keyed by
    /// (<see cref="Id"/>, <see cref="Version"/>); the per-step pin records
    /// the exact version it locks to.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>Human-readable name shown in the step picker + editor header.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// One-paragraph description rendered as the schema-root description in
    /// the editor and in the catalog browser.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>Author / publisher name shown alongside the version in the UI.</summary>
    public string? Author { get; init; }

    /// <summary>
    /// .NET TFM the executor assembly targets — almost always
    /// <c>net10.0</c> until the platform moves on.
    /// </summary>
    public required string TargetFramework { get; init; }

    /// <summary>
    /// Step-type strings the executor's <see cref="IStepHandler.CanHandle"/>
    /// claims. Multi-claim is supported so a single package can implement
    /// alias types (e.g. <c>"Kraken.Script"</c> + <c>"Octopus.Script"</c>).
    /// At runtime the executor's <c>CanHandle</c> must agree with this list —
    /// the loader uses the list to register the package against each
    /// declared type, then trusts the C# logic to gate execution.
    /// </summary>
    public required IReadOnlyList<string> StepTypes { get; init; }

    /// <summary>
    /// Minimum agent version that can load this package, semver string. The
    /// loader refuses to load packages whose <see cref="MinKrakenAgent"/> is
    /// newer than the running agent — useful when a new package depends on
    /// a Contracts surface only present on a newer agent.
    /// </summary>
    public string? MinKrakenAgent { get; init; }

    /// <summary>
    /// Filename of the executor assembly inside <c>executor/</c>. Always
    /// just the leaf filename (<c>Kraken.Steps.Iis.dll</c>), not a path.
    /// </summary>
    public required string ExecutorAssembly { get; init; }

    /// <summary>
    /// Fully-qualified type name (namespace + class) of the
    /// <c>IStepHandler</c>-implementing class. The loader resolves this via
    /// <see cref="System.Reflection.Assembly.GetType(string)"/> after loading
    /// <see cref="ExecutorAssembly"/>.
    /// </summary>
    public required string ExecutorTypeName { get; init; }

    /// <summary>
    /// Optional URL to a CHANGELOG anchor or release-notes page for this
    /// version. Surfaced in the "Update available" diff dialog when the
    /// embedded <c>CHANGELOG.md</c> is absent or terse.
    /// </summary>
    public string? Homepage { get; init; }

    /// <summary>
    /// Base64-encoded RSA-SHA256 signature over the canonical
    /// (manifest-with-signature-blanked) + SHA-256(executor.dll). Empty during
    /// authoring; populated by the signing tool (<c>kraken pack</c> /
    /// <c>dotnet pack</c>). Server-side D-3 upload verifies it against the
    /// project's compiled-in public key before persisting.
    /// </summary>
    public string? Signature { get; init; }

    /// <summary>
    /// Identifier of the key that signed the package. For v1 always
    /// <c>kraken-project</c>; in the future could carry an organisation
    /// key id when third parties publish signed packages.
    /// </summary>
    public string? SignedBy { get; init; }

    /// <summary>
    /// Returns a copy of this manifest with <see cref="Signature"/> set to
    /// <c>null</c> — the canonical form used to compute the signature input.
    /// </summary>
    public StepPackageManifest WithoutSignature() => this with { Signature = null };
}

/// <summary>
/// Canonical JSON serialiser for <see cref="StepPackageManifest"/>. Centralises
/// the serialization options so authoring tools, the upload endpoint, and the
/// agent loader all agree on casing, indentation, and null-omission. The exact
/// bytes <see cref="Serialize"/> emits are what the signature is computed over,
/// so divergence here would break verification.
/// </summary>
public static class StepPackageManifestJson
{
    /// <summary>
    /// Shared JSON options. <c>camelCase</c> property naming matches
    /// JavaScript / JSON Schema convention so step-package authors can
    /// hand-edit the file. Null fields are omitted to keep the canonical
    /// signature input stable across optional-field churn.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy         = null,
        WriteIndented               = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        AllowTrailingCommas         = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
    };

    public static string Serialize(StepPackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, Options);
    }

    public static StepPackageManifest Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<StepPackageManifest>(json, Options)
            ?? throw new InvalidOperationException("Step-package manifest JSON deserialised to null.");
    }

    /// <summary>
    /// Computes the canonical UTF-8 byte sequence that the signing tool feeds
    /// into RSA-SHA256 along with the executor DLL's SHA-256. Equivalent to
    /// <c>Encoding.UTF8.GetBytes(Serialize(manifest.WithoutSignature()))</c>
    /// but factored here so callers don't have to think about the exact
    /// recipe.
    /// </summary>
    public static byte[] CanonicalSignatureInput(StepPackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var json = Serialize(manifest.WithoutSignature());
        return System.Text.Encoding.UTF8.GetBytes(json);
    }
}

/// <summary>
/// Canonical filenames inside the <c>.kdeploy-step</c> zip. Centralised so
/// the authoring tools, the server-side upload validator, and the agent
/// loader agree on the on-disk layout.
/// </summary>
[SuppressMessage("Design", "CA1052:Static holder types should be Static",
    Justification = "Public constant-bag class; making it static would prevent " +
                    "test fixtures from declaring a [Friend] field, and the C# 12 " +
                    "constructor-less public class is intentional.")]
public static class StepPackageFiles
{
    /// <summary>Canonical extension for a step package archive.</summary>
    public const string Extension = ".kdeploy-step";

    /// <summary>The manifest filename at the zip root.</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>Directory containing the executor DLL + its direct deps.</summary>
    public const string ExecutorDirectory = "executor";

    /// <summary>Directory containing the declarative UI schema.</summary>
    public const string UiDirectory = "ui";

    /// <summary>The UI schema filename inside <see cref="UiDirectory"/>.</summary>
    public const string UiSchemaFileName = "ui-schema.json";

    /// <summary>Optional README at the zip root.</summary>
    public const string ReadmeFileName = "README.md";

    /// <summary>Optional changelog at the zip root.</summary>
    public const string ChangelogFileName = "CHANGELOG.md";

    /// <summary>Optional logo at the zip root.</summary>
    public const string LogoFileName = "logo.png";
}

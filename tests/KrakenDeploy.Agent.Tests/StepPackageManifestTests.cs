using System.Text;
using FluentAssertions;
using KrakenDeploy.Contracts.StepPackages;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// Unit tests for <see cref="StepPackageManifest"/> + <see cref="StepPackageManifestJson"/>
/// (Phase D-1). Covers the required-field surface, JSON round-trip, the
/// canonical signature-input recipe, and the <c>WithoutSignature</c> helper.
/// </summary>
public sealed class StepPackageManifestTests
{
    // ── Construction ──────────────────────────────────────────────────────

    [Fact]
    public void Required_fields_only_yields_a_valid_manifest_with_nulls_for_optionals()
    {
        var m = NewMinimalManifest();

        m.Id.Should().Be("kraken.iis");
        m.Version.Should().Be("1.0.0");
        m.DisplayName.Should().Be("Deploy to IIS");
        m.TargetFramework.Should().Be("net10.0");
        m.StepTypes.Should().ContainSingle().Which.Id.Should().Be("Kraken.IIS");
        m.ExecutorAssembly.Should().Be("Kraken.Steps.Iis.dll");
        m.ExecutorTypeName.Should().Be("Kraken.Steps.Iis.KrakenIisStepHandler");

        m.Description.Should().BeNull();
        m.Author.Should().BeNull();
        m.MinKrakenAgent.Should().BeNull();
        m.Homepage.Should().BeNull();
        m.Signature.Should().BeNull();
        m.SignedBy.Should().BeNull();
    }

    // ── JSON round-trip ───────────────────────────────────────────────────

    [Fact]
    public void Round_trip_a_minimal_manifest_preserves_required_fields()
    {
        var original = NewMinimalManifest();
        var json     = StepPackageManifestJson.Serialize(original);
        var parsed   = StepPackageManifestJson.Deserialize(json);

        parsed.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Round_trip_a_realistic_signed_manifest_preserves_every_field()
    {
        var original = new StepPackageManifest
        {
            Id               = "kraken.steps.aws-s3-upload",
            Version          = "2.3.1-preview.4",
            DisplayName      = "Upload to AWS S3",
            Description      = "Uploads files from the package payload to an S3 bucket.",
            Author           = "Kraken Project",
            TargetFramework  = "net10.0",
            StepTypes        = ["Kraken.Steps.AwsS3Upload"],
            MinKrakenAgent   = "1.5.0",
            ExecutorAssembly = "Kraken.Steps.AwsS3Upload.dll",
            ExecutorTypeName = "Kraken.Steps.AwsS3Upload.S3UploadStepHandler",
            Homepage         = "https://github.com/KrakenDeploy/StepPackages/tree/main/aws-s3-upload",
            Signature        = "Aa+/Bb==base64...sig==",
            SignedBy         = "kraken-project",
        };

        var json   = StepPackageManifestJson.Serialize(original);
        var parsed = StepPackageManifestJson.Deserialize(json);

        parsed.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Serialization_uses_camelCase_property_names()
    {
        var json = StepPackageManifestJson.Serialize(NewMinimalManifest());

        json.Should().Contain("\"id\":");
        json.Should().Contain("\"version\":");
        json.Should().Contain("\"displayName\":");
        json.Should().Contain("\"stepTypes\":");
        json.Should().Contain("\"targetFramework\":");
        json.Should().Contain("\"executorAssembly\":");
        json.Should().Contain("\"executorTypeName\":");
        json.Should().NotContain("\"Id\":");
        json.Should().NotContain("\"DisplayName\":");
    }

    [Fact]
    public void Null_optional_fields_are_omitted_from_serialised_output()
    {
        // The canonical signature input is computed off this output, so a
        // stable null-omission story is necessary for verification to work
        // across optional-field churn.
        var json = StepPackageManifestJson.Serialize(NewMinimalManifest());

        json.Should().NotContain("\"description\"");
        json.Should().NotContain("\"author\"");
        json.Should().NotContain("\"minKrakenAgent\"");
        json.Should().NotContain("\"signature\"");
        json.Should().NotContain("\"signedBy\"");
        json.Should().NotContain("\"homepage\"");
    }

    [Fact]
    public void Deserialize_tolerates_comments_and_trailing_commas()
    {
        var json = """
            // sample manifest for kraken.iis
            {
                "id": "kraken.iis",
                "version": "1.0.0",
                "displayName": "IIS",
                "targetFramework": "net10.0",
                "stepTypes": ["Kraken.IIS"],
                "executorAssembly": "Kraken.Steps.Iis.dll",
                "executorTypeName": "Kraken.Steps.Iis.KrakenIisStepHandler",
            }
            """;

        var m = StepPackageManifestJson.Deserialize(json);
        m.Id.Should().Be("kraken.iis");
        m.StepTypes.Should().ContainSingle().Which.Id.Should().Be("Kraken.IIS");
    }

    // ── SC1: stepTypes entries as string | object ─────────────────────────

    [Fact]
    public void StepTypes_object_entries_parse_with_per_type_metadata()
    {
        var json = """
            {
                "id": "octopus.kubernetes",
                "version": "1.1.0",
                "displayName": "Kubernetes",
                "targetFramework": "net10.0",
                "stepTypes": [
                    "Octopus.KubernetesRunScript",
                    {
                        "id": "Octopus.HelmChartUpgrade",
                        "displayName": "Deploy a Helm Chart",
                        "category": "kubernetes",
                        "description": "Install or upgrade a Helm release.",
                        "featured": true,
                        "someFutureField": { "ignored": true }
                    }
                ],
                "executorAssembly": "K.dll",
                "executorTypeName": "K.Handler"
            }
            """;

        var m = StepPackageManifestJson.Deserialize(json);

        m.StepTypes.Should().HaveCount(2);
        m.StepTypes[0].Should().BeEquivalentTo(new StepTypeDeclaration
        {
            Id = "Octopus.KubernetesRunScript",
        });
        m.StepTypes[0].IsIdOnly.Should().BeTrue();
        m.StepTypes[1].Should().BeEquivalentTo(new StepTypeDeclaration
        {
            Id          = "Octopus.HelmChartUpgrade",
            DisplayName = "Deploy a Helm Chart",
            Category    = "kubernetes",
            Description = "Install or upgrade a Helm release.",
            Featured    = true,
        }, "unknown fields are skipped for forward-compat");
        m.StepTypeIds.Should().Equal("Octopus.KubernetesRunScript", "Octopus.HelmChartUpgrade");
    }

    [Fact]
    public void Id_only_stepTypes_entries_serialise_as_plain_strings()
    {
        // Canonical-bytes contract: a pre-SC1 manifest (string entries) must
        // re-serialise to the exact same shape, or re-verifying an old signed
        // archive would fail on a signature computed over different bytes.
        var json = StepPackageManifestJson.Serialize(NewMinimalManifest());

        json.Should().Contain("\"Kraken.IIS\"");
        json.Should().NotContain("\"id\": \"Kraken.IIS\"",
            "an id-only entry must stay a bare string, not become an object");
    }

    [Fact]
    public void Metadata_carrying_stepTypes_entries_round_trip_as_objects()
    {
        var original = NewMinimalManifest() with
        {
            StepTypes =
            [
                new StepTypeDeclaration
                {
                    Id          = "Kraken.IIS",
                    DisplayName = "Deploy Web Site",
                    Category    = "iis",
                    Featured    = true,
                },
                "Octopus.IIS",
            ],
        };

        var json   = StepPackageManifestJson.Serialize(original);
        var parsed = StepPackageManifestJson.Deserialize(json);

        parsed.Should().BeEquivalentTo(original);
        json.Should().Contain("\"displayName\": \"Deploy Web Site\"");
        json.Should().Contain("\"Octopus.IIS\"");
    }

    [Fact]
    public void Empty_string_stepTypes_entry_is_rejected()
    {
        var json = """
            {
                "id": "x", "version": "1.0.0", "displayName": "X",
                "targetFramework": "net10.0",
                "stepTypes": [""],
                "executorAssembly": "X.dll", "executorTypeName": "X.H"
            }
            """;

        var act = () => StepPackageManifestJson.Deserialize(json);
        act.Should().Throw<System.Text.Json.JsonException>()
            .WithMessage("*must not be an empty string*");
    }

    [Fact]
    public void StepTypes_object_entry_without_id_is_rejected()
    {
        var json = """
            {
                "id": "x", "version": "1.0.0", "displayName": "X",
                "targetFramework": "net10.0",
                "stepTypes": [{ "displayName": "No id here" }],
                "executorAssembly": "X.dll", "executorTypeName": "X.H"
            }
            """;

        var act = () => StepPackageManifestJson.Deserialize(json);
        act.Should().Throw<System.Text.Json.JsonException>()
            .WithMessage("*missing a non-empty 'id'*");
    }

    [Fact]
    public void Deserialize_throws_InvalidOperation_on_null_json_body()
    {
        var act = () => StepPackageManifestJson.Deserialize("null");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*deserialised to null*");
    }

    // ── WithoutSignature + canonical signing recipe ───────────────────────

    [Fact]
    public void WithoutSignature_returns_a_copy_with_signature_blank_and_everything_else_intact()
    {
        var signed = NewMinimalManifest() with
        {
            Signature = "real-sig-bytes-base64==",
            SignedBy  = "kraken-project",
        };

        var bare = signed.WithoutSignature();

        bare.Signature.Should().BeNull();
        bare.SignedBy.Should().Be("kraken-project",
            "WithoutSignature only blanks the signature, not the key identifier");
        bare.Id.Should().Be(signed.Id);
        bare.Version.Should().Be(signed.Version);
        bare.ExecutorAssembly.Should().Be(signed.ExecutorAssembly);
        bare.ExecutorTypeName.Should().Be(signed.ExecutorTypeName);
    }

    [Fact]
    public void CanonicalSignatureInput_is_stable_under_signature_field_changes()
    {
        // Two manifests differing only in Signature must produce the same
        // canonical signature input — that's the whole point of signing
        // the "naked" form. The 32-byte SHA appended after the JSON is the
        // same in both cases here so equality holds.
        var a   = NewMinimalManifest() with { Signature = "sig-A" };
        var b   = NewMinimalManifest() with { Signature = "sig-B" };
        var sha = new byte[32]; // arbitrary but identical across both calls

        var canonicalA = StepPackageManifestJson.CanonicalSignatureInput(a, sha);
        var canonicalB = StepPackageManifestJson.CanonicalSignatureInput(b, sha);

        canonicalA.Should().BeEquivalentTo(canonicalB);
    }

    [Fact]
    public void CanonicalSignatureInput_emits_naked_manifest_UTF8_then_executor_sha()
    {
        var m   = NewMinimalManifest();
        var sha = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var bytes = StepPackageManifestJson.CanonicalSignatureInput(m, sha);

        var nakedJson      = StepPackageManifestJson.Serialize(m.WithoutSignature());
        var nakedJsonBytes = Encoding.UTF8.GetBytes(nakedJson);

        bytes.Length.Should().Be(nakedJsonBytes.Length + 32);
        bytes[..nakedJsonBytes.Length].Should().Equal(nakedJsonBytes,
            "the canonical input starts with the exact UTF-8 representation of the naked manifest's JSON");
        bytes[nakedJsonBytes.Length..].Should().Equal(sha,
            "and ends with the executor DLL's 32-byte SHA-256, so swapping the DLL invalidates the signature");
    }

    [Fact]
    public void CanonicalSignatureInput_rejects_executor_sha_of_wrong_length()
    {
        var m = NewMinimalManifest();
        var bad = new byte[16]; // SHA-128? Wrong.

        var act = () => StepPackageManifestJson.CanonicalSignatureInput(m, bad);
        act.Should().Throw<ArgumentException>().WithMessage("*32 bytes*");
    }

    // ── StepPackageFiles constants ────────────────────────────────────────

    [Fact]
    public void StepPackageFiles_exposes_the_canonical_zip_layout_filenames()
    {
        StepPackageFiles.Extension.Should().Be(".kdeploy-step");
        StepPackageFiles.ManifestFileName.Should().Be("manifest.json");
        StepPackageFiles.ExecutorDirectory.Should().Be("executor");
        StepPackageFiles.UiDirectory.Should().Be("ui");
        StepPackageFiles.UiSchemaFileName.Should().Be("ui-schema.json");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static StepPackageManifest NewMinimalManifest() => new()
    {
        Id               = "kraken.iis",
        Version          = "1.0.0",
        DisplayName      = "Deploy to IIS",
        TargetFramework  = "net10.0",
        StepTypes        = ["Kraken.IIS"],
        ExecutorAssembly = "Kraken.Steps.Iis.dll",
        ExecutorTypeName = "Kraken.Steps.Iis.KrakenIisStepHandler",
    };
}

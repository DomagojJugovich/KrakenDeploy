using FluentAssertions;
using KrakenDeploy.Contracts.Steps;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// Structural tests for <see cref="BuiltInStepSchemas"/> (Phase C-5). These
/// assert that every shipped step type has a registered schema, that the
/// schema is buildable (no reflection / attribute errors), and that the
/// fundamentals — config-bag keys, required fields, conditional visibility,
/// enum options — line up with what the runtime handlers expect.
/// </summary>
public sealed class BuiltInStepSchemasTests
{
    [Theory]
    [InlineData("Kraken.IIS")]
    [InlineData("Octopus.IIS")]
    [InlineData("Octopus.TentaclePackage")]
    [InlineData("Kraken.Script")]
    [InlineData("Octopus.Script")]
    [InlineData("Octopus.SubstituteVariables")]
    [InlineData("Octopus.FileTransform")]
    [InlineData("Octopus.Manual")]
    public void GetForStepType_returns_a_schema_for_every_built_in_step_type(string stepType)
    {
        var schema = BuiltInStepSchemas.GetForStepType(stepType);

        schema.Should().NotBeNull();
        schema!.Id.Should().NotBeNullOrEmpty();
        schema.Title.Should().NotBeNullOrEmpty();
        schema.Version.Should().NotBeNullOrEmpty();
        schema.Properties.Should().NotBeEmpty($"step '{stepType}' has no fields");
    }

    [Fact]
    public void GetForStepType_is_case_insensitive()
    {
        BuiltInStepSchemas.GetForStepType("KRAKEN.IIS").Should().NotBeNull();
        BuiltInStepSchemas.GetForStepType("octopus.manual").Should().NotBeNull();
    }

    [Fact]
    public void GetForStepType_returns_null_for_unknown_step_type()
    {
        BuiltInStepSchemas.GetForStepType("something.unknown").Should().BeNull();
    }

    [Fact]
    public void Kraken_Script_and_Octopus_Script_share_the_same_schema_id()
    {
        var k = BuiltInStepSchemas.GetForStepType("Kraken.Script")!;
        var o = BuiltInStepSchemas.GetForStepType("Octopus.Script")!;
        k.Id.Should().Be(o.Id, "both step types use the same schema definition");
    }

    // ── Kraken.IIS structural ────────────────────────────────────────────

    [Fact]
    public void Kraken_IIS_schema_uses_canonical_dotted_config_keys()
    {
        var schema = BuiltInStepSchemas.GetForStepType("Kraken.IIS")!;
        schema.Properties.Should().ContainKey(KrakenIisConfigKeys.SiteName);
        schema.Properties.Should().ContainKey(KrakenIisConfigKeys.WebRoot);
        schema.Properties.Should().ContainKey(KrakenIisConfigKeys.AppPoolName);
        schema.Properties.Should().ContainKey(KrakenIisConfigKeys.AppPoolIdentityType);
        schema.Properties.Should().ContainKey(KrakenIisConfigKeys.AppPoolUsername);
        schema.Properties.Should().ContainKey(KrakenIisConfigKeys.AppPoolPassword);
        schema.Properties.Should().ContainKey(KrakenIisConfigKeys.Bindings);
        schema.Properties.Should().ContainKey(KrakenIisConfigKeys.HealthCheckUrl);
    }

    [Fact]
    public void Kraken_IIS_appPool_username_password_are_visible_only_for_SpecificUser_identity()
    {
        var schema = BuiltInStepSchemas.GetForStepType("Kraken.IIS")!;
        var user = schema.Properties[KrakenIisConfigKeys.AppPoolUsername];
        user.VisibleWhen.Should().NotBeNull();
        user.VisibleWhen!.Field.Should().Be(KrakenIisConfigKeys.AppPoolIdentityType);
        user.VisibleWhen.Operator.Should().Be("equals");
        user.VisibleWhen.Value.Should().Be("SpecificUser");
    }

    [Fact]
    public void Kraken_IIS_SiteName_and_WebRoot_are_required()
    {
        var schema = BuiltInStepSchemas.GetForStepType("Kraken.IIS")!;
        schema.Properties[KrakenIisConfigKeys.SiteName].Validation.Should().NotBeNull();
        schema.Properties[KrakenIisConfigKeys.SiteName].Validation!.Required.Should().BeTrue();
        schema.Properties[KrakenIisConfigKeys.WebRoot].Validation!.Required.Should().BeTrue();
    }

    [Fact]
    public void Kraken_IIS_identityType_enumerates_all_five_IIS_pool_identities()
    {
        var schema = BuiltInStepSchemas.GetForStepType("Kraken.IIS")!;
        var f = schema.Properties[KrakenIisConfigKeys.AppPoolIdentityType];
        f.EnumValues.Select(e => e.Value).Should().BeEquivalentTo(
            "ApplicationPoolIdentity", "LocalSystem", "LocalService",
            "NetworkService", "SpecificUser");
    }

    // ── Octopus.IIS structural ───────────────────────────────────────────

    [Fact]
    public void Octopus_IIS_DeploymentType_drives_branch_visibility()
    {
        var schema = BuiltInStepSchemas.GetForStepType("Octopus.IIS")!;
        var dtKey = "Octopus.Action.IISWebSite.DeploymentType";
        schema.Properties.Should().ContainKey(dtKey);

        // The webApplication sub-fields are gated on DeploymentType=webApplication.
        var parentSite = schema.Properties["Octopus.Action.IISWebSite.WebApplication.WebSiteName"];
        parentSite.VisibleWhen.Should().NotBeNull();
        parentSite.VisibleWhen!.Field.Should().Be(dtKey);
        parentSite.VisibleWhen.Value.Should().Be("webApplication");

        // The virtualDirectory sub-fields are gated on DeploymentType=virtualDirectory.
        var vdir = schema.Properties["Octopus.Action.IISWebSite.VirtualDirectory.VirtualPath"];
        vdir.VisibleWhen!.Value.Should().Be("virtualDirectory");

        // The site-only fields are gated on DeploymentType=webSite.
        var siteName = schema.Properties["Octopus.Action.IISWebSite.WebSiteName"];
        siteName.VisibleWhen!.Value.Should().Be("webSite");
    }

    [Fact]
    public void Octopus_IIS_Bindings_widget_is_json_editor()
    {
        var schema = BuiltInStepSchemas.GetForStepType("Octopus.IIS")!;
        var b = schema.Properties["Octopus.Action.IISWebSite.Bindings"];
        b.Widget.Should().Be(StepUiWidgets.JsonEditor);
    }

    // ── Octopus.TentaclePackage structural ───────────────────────────────

    [Fact]
    public void Octopus_TentaclePackage_schema_lists_the_required_PackageId_field()
    {
        var schema = BuiltInStepSchemas.GetForStepType("Octopus.TentaclePackage")!;
        schema.Properties["Octopus.Action.Package.PackageId"].Validation!.Required.Should().BeTrue();
    }

    [Fact]
    public void Octopus_TentaclePackage_exposes_purge_and_transform_flags()
    {
        var schema = BuiltInStepSchemas.GetForStepType("Octopus.TentaclePackage")!;
        schema.Properties.Should().ContainKey(
            "Octopus.Action.Package.CustomInstallationDirectoryShouldBePurgedBeforeDeployment");
        schema.Properties.Should().ContainKey(
            "Octopus.Action.Package.AutomaticallyUpdateAppSettingsAndConnectionStrings");
        schema.Properties.Should().ContainKey(
            "Octopus.Action.Package.AutomaticallyRunConfigurationTransformationFiles");
    }

    // ── Script structural ────────────────────────────────────────────────

    [Fact]
    public void Script_schema_uses_Octopus_compatible_keys()
    {
        var schema = BuiltInStepSchemas.GetForStepType("Kraken.Script")!;
        schema.Properties.Should().ContainKey(KrakenScriptConfigKeys.Syntax);
        schema.Properties.Should().ContainKey(KrakenScriptConfigKeys.ScriptBody);
        schema.Properties.Should().ContainKey(KrakenScriptConfigKeys.PowerShellEdition);
        schema.Properties.Should().ContainKey(KrakenScriptConfigKeys.RunOnServer);
    }

    [Fact]
    public void Script_PowerShell_edition_is_only_visible_for_PowerShell_syntax()
    {
        var schema = BuiltInStepSchemas.GetForStepType("Kraken.Script")!;
        var edition = schema.Properties[KrakenScriptConfigKeys.PowerShellEdition];
        edition.VisibleWhen.Should().NotBeNull();
        edition.VisibleWhen!.Field.Should().Be(KrakenScriptConfigKeys.Syntax);
        edition.VisibleWhen.Value.Should().Be("PowerShell");
    }

    // ── Manual + utility steps structural ────────────────────────────────

    [Fact]
    public void Manual_schema_marks_Instructions_required()
    {
        var schema = BuiltInStepSchemas.GetForStepType("Octopus.Manual")!;
        schema.Properties["Octopus.Action.Manual.Instructions"].Validation!.Required.Should().BeTrue();
    }

    [Fact]
    public void SubstituteVariables_schema_has_single_required_TargetFiles_field()
    {
        var schema = BuiltInStepSchemas.GetForStepType("Octopus.SubstituteVariables")!;
        schema.Properties.Should().ContainSingle();
        schema.Properties["Octopus.Action.SubstituteInFiles.TargetFiles"]
            .Validation!.Required.Should().BeTrue();
    }

    [Fact]
    public void FileTransform_schema_has_single_required_Targets_field()
    {
        var schema = BuiltInStepSchemas.GetForStepType("Octopus.FileTransform")!;
        schema.Properties.Should().ContainSingle();
        schema.Properties["Octopus.Action.Package.JsonConfigurationVariablesTargets"]
            .Validation!.Required.Should().BeTrue();
    }

    // ── Round-trip ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("Kraken.IIS")]
    [InlineData("Octopus.IIS")]
    [InlineData("Octopus.TentaclePackage")]
    [InlineData("Kraken.Script")]
    [InlineData("Octopus.SubstituteVariables")]
    [InlineData("Octopus.FileTransform")]
    [InlineData("Octopus.Manual")]
    public void Every_schema_round_trips_through_JSON_serialization(string stepType)
    {
        var original = BuiltInStepSchemas.GetForStepType(stepType)!;
        var json     = StepUiSchemaJson.Serialize(original);
        var parsed   = StepUiSchemaJson.Deserialize(json);
        parsed.Should().BeEquivalentTo(original);
    }
}

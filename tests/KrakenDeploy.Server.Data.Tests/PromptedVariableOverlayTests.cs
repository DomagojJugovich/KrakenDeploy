using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Encryption;

namespace KrakenDeploy.Server.Data.Tests;

public sealed class PromptedVariableOverlayTests
{
    [Fact]
    public void Codec_rejects_the_pre_versioned_payload_shape_instead_of_dropping_values()
    {
        var crypto = TestCrypto.Service(Convert.ToBase64String(new byte[32]));
        var act = () => PromptedVariableFormValuesCodec.Deserialize("{\"Legacy\":\"value\"}", crypto);

        act.Should().Throw<System.Text.Json.JsonException>();
    }

    [Fact]
    public void Apply_gives_prompted_values_highest_precedence_and_preserves_step_scope()
    {
        var stepId = Guid.NewGuid();
        var context = new PromptedVariableContext(
            Guid.NewGuid(), Guid.NewGuid(), [], null, null, []);
        var snapshot = new List<VariableSnapshot>
        {
            new()
            {
                Name = "Greeting",
                Value = "stored",
                IsPrompted = true,
                Layer = VariableSnapshot.ProjectLayer,
            },
            new()
            {
                Name = "StepSecret",
                Value = "ciphertext",
                Type = VariableType.Sensitive,
                IsPrompted = true,
                Scope = new VariableScope { ProcessStepId = stepId },
                Layer = VariableSnapshot.ProjectLayer,
            },
        };
        var resolution = new StepScopedResolution(
            new Dictionary<string, string> { ["Greeting"] = "stored" },
            [],
            []);

        var sensitive = PromptedVariableOverlay.Apply(
            resolution,
            snapshot,
            context,
            [stepId],
            new Dictionary<string, string>
            {
                ["Greeting"] = "operator-value",
                ["StepSecret"] = "operator-secret",
            });

        resolution.DeploymentWide["Greeting"].Should().Be("operator-value");
        resolution.DeploymentWide.Should().NotContainKey("StepSecret");
        resolution.PerStepDelta[stepId]["StepSecret"].Should().Be("operator-secret");
        sensitive.Should().Contain("StepSecret");
    }
}

using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Variables;

namespace KrakenDeploy.Server.Core.Tests;

public sealed class PromptedVariableResolverTests
{
    [Fact]
    public void GetApplicable_uses_the_winning_scoped_definition()
    {
        var environmentId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var snapshot = new List<VariableSnapshot>
        {
            Prompt("Greeting", "Fallback", required: false, new VariableScope()),
            Prompt("Greeting", "Production greeting", required: true,
                new VariableScope { EnvironmentId = environmentId }),
            Prompt("Other", "Other environment", required: true,
                new VariableScope { EnvironmentId = Guid.NewGuid() }),
        };
        var context = new PromptedVariableContext(
            environmentId, targetId, ["web"], null, null, []);

        var result = PromptedVariableResolver.GetApplicable(snapshot, [context], []);

        result.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new PromptedVariableDefinition(
                "Greeting", "Production greeting", null, true,
                PromptControlType.Text, [], false));
    }

    [Fact]
    public void FindWinner_does_not_apply_a_prompt_when_a_more_specific_non_prompted_value_wins()
    {
        var environmentId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var snapshot = new List<VariableSnapshot>
        {
            Prompt("Key", "Enter key", required: true, new VariableScope()),
            new()
            {
                Name = "Key",
                Value = "fixed",
                Scope = new VariableScope { TargetId = targetId },
                Layer = VariableSnapshot.ProjectLayer,
            },
        };
        var context = new PromptedVariableContext(
            environmentId, targetId, [], null, null, []);

        PromptedVariableResolver.FindWinner(snapshot, "Key", context)!.IsPrompted.Should().BeFalse();
        PromptedVariableResolver.GetApplicable(snapshot, [context], []).Should().BeEmpty();
    }

    [Fact]
    public void FilterApplicableValues_keeps_only_answers_for_the_current_plan()
    {
        var definitions = new[]
        {
            new PromptedVariableDefinition(
                "TenantAOnly", "Tenant A only", null, false,
                PromptControlType.Text, [], false),
        };
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TenantAOnly"] = "applicable",
            ["TenantBOnly"] = "must-not-be-sent",
            ["EmptyOptional"] = "",
        };

        var result = PromptedVariableResolver.FilterApplicableValues(definitions, values);

        result.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["TenantAOnly"] = "applicable",
        });
    }

    private static VariableSnapshot Prompt(
        string name, string label, bool required, VariableScope scope) => new()
    {
        Name = name,
        Value = "default",
        IsPrompted = true,
        PromptLabel = label,
        PromptRequired = required,
        Scope = scope,
        Layer = VariableSnapshot.ProjectLayer,
    };
}

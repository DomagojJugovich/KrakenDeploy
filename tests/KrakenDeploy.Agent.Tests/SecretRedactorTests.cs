using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Logging;
using Xunit;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// Unit tests for the T0-6 value-based log redactor. Covers the tricky cases:
/// overlapping/substring secrets (must mask longest-first so a longer secret
/// isn't chopped by a shorter one), empty/whitespace values (must never blank a
/// whole line), and the plan-seeding factory including per-step overrides.
/// </summary>
public sealed class SecretRedactorTests
{
    [Fact]
    public void No_secrets_returns_line_unchanged()
    {
        var redactor = new SecretRedactor();
        redactor.HasSecrets.Should().BeFalse();
        redactor.Redact("nothing to hide here").Should().Be("nothing to hide here");
    }

    [Fact]
    public void Single_value_is_masked_everywhere_it_appears()
    {
        var redactor = new SecretRedactor(["hunter2"]);
        redactor.Redact("pwd=hunter2 and again hunter2")
            .Should().Be("pwd=*** and again ***");
    }

    [Fact]
    public void Overlapping_values_mask_longest_first_so_the_longer_secret_is_not_chopped()
    {
        // "abc" is a substring of "abcdef". Shortest-first would turn "abcdef"
        // into "***def"; longest-first masks the whole "abcdef".
        var redactor = new SecretRedactor(["abc", "abcdef"]);
        redactor.Redact("x abcdef y abc z").Should().Be("x *** y *** z");
    }

    [Fact]
    public void Empty_and_whitespace_values_are_ignored_and_never_blank_the_line()
    {
        var redactor = new SecretRedactor(["", "   ", "\t"]);
        redactor.HasSecrets.Should().BeFalse();
        redactor.Redact("this stays exactly as-is").Should().Be("this stays exactly as-is");
    }

    [Fact]
    public void Add_folds_in_new_values_at_runtime()
    {
        var redactor = new SecretRedactor();
        redactor.Redact("token=zzz").Should().Be("token=zzz");
        redactor.Add(["zzz"]);
        redactor.Redact("token=zzz").Should().Be("token=***");
    }

    [Fact]
    public void Redact_handles_null_and_empty_input()
    {
        var redactor = new SecretRedactor(["secret"]);
        redactor.Redact("").Should().Be("");
        redactor.Redact(null!).Should().BeNull();
    }

    [Fact]
    public void ForPlan_seeds_from_deployment_wide_and_per_step_sensitive_values()
    {
        var steps = new[]
        {
            new DeploymentStepPlan(
                0, "Deploy", "Kraken.Script", "", "",
                Config: new Dictionary<string, string>(),
                StepVariables: new Dictionary<string, string>
                {
                    ["Api.Key"] = "step-scoped-secret",
                }),
        };

        var plan = new DeploymentPlan(
            DeploymentId: Guid.NewGuid(),
            EnvironmentName: "Prod",
            Steps: steps,
            Variables: new Dictionary<string, string>
            {
                ["Db.Password"] = "deployment-wide-secret",
                ["Public.Url"]  = "https://example.test",
            },
            ArrayVariables: new Dictionary<string, string[]>(),
            SensitiveVariableNames: ["Db.Password", "Api.Key"]);

        var redactor = SecretRedactor.ForPlan(plan);

        redactor.Redact("db=deployment-wide-secret key=step-scoped-secret url=https://example.test")
            .Should().Be("db=*** key=*** url=https://example.test");
    }

    [Fact]
    public void ForPlan_with_no_sensitive_names_is_a_noop_redactor()
    {
        var plan = new DeploymentPlan(
            DeploymentId: Guid.NewGuid(),
            EnvironmentName: "Prod",
            Steps: [],
            Variables: new Dictionary<string, string> { ["X"] = "y" },
            ArrayVariables: new Dictionary<string, string[]>());

        SecretRedactor.ForPlan(plan).HasSecrets.Should().BeFalse();
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using KrakenDeploy.Agent.Offline;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Crypto;
using KrakenDeploy.Contracts.Offline;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// The offline runner decrypts <c>plan.enc</c> with the per-target key, composes
/// the SAME <see cref="KrakenDeploy.Agent.Deployment.DeploymentExecutor"/> the
/// online agent uses against bundle-backed ports, runs it, and writes
/// <c>deployment-result.json</c> with the right exit code. (Wave + cross-step
/// output-variable behaviour is covered by the executor/accumulator tests; this
/// pins the runner wiring, decryption, result, and exit codes.)
/// </summary>
public sealed class OfflineRunnerTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _bundle =
        Path.Combine(Path.GetTempPath(), $"kraken-offline-{Guid.NewGuid():N}");

    public OfflineRunnerTests() => Directory.CreateDirectory(_bundle);

    [Fact]
    public async Task Runs_empty_plan_and_writes_success_result()
    {
        var key = RandomNumberGenerator.GetBytes(AesGcmCipher.KeyBytes);
        var deploymentId = Guid.NewGuid();
        WritePlan(key, new DeploymentPlan(
            deploymentId, "Production", [],
            new Dictionary<string, string>(), new Dictionary<string, string[]>()));

        var exit = await new OfflineRunner(NullLoggerFactory.Instance)
            .RunAsync(_bundle, key);

        exit.Should().Be(0);

        var resultJson = await File.ReadAllTextAsync(
            Path.Combine(_bundle, OfflineBundleLayout.ResultFile));
        var result = JsonSerializer.Deserialize<OfflineDropResult>(resultJson, Web)!;
        result.DeploymentId.Should().Be(deploymentId);
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Wrong_key_returns_setup_error_exit_code()
    {
        var key = RandomNumberGenerator.GetBytes(AesGcmCipher.KeyBytes);
        WritePlan(key, new DeploymentPlan(
            Guid.NewGuid(), "Production", [],
            new Dictionary<string, string>(), new Dictionary<string, string[]>()));

        var wrongKey = RandomNumberGenerator.GetBytes(AesGcmCipher.KeyBytes);
        var exit = await new OfflineRunner(NullLoggerFactory.Instance)
            .RunAsync(_bundle, wrongKey);

        exit.Should().Be(2); // setup error — couldn't decrypt the plan
    }

    [Fact]
    public async Task Missing_plan_returns_setup_error_exit_code()
    {
        var key = RandomNumberGenerator.GetBytes(AesGcmCipher.KeyBytes);
        // No plan.enc written.
        var exit = await new OfflineRunner(NullLoggerFactory.Instance)
            .RunAsync(_bundle, key);

        exit.Should().Be(2);
    }

    [Fact]
    public async Task Unresolved_variable_condition_skip_is_logged_at_warning_offline()
    {
        var key = RandomNumberGenerator.GetBytes(AesGcmCipher.KeyBytes);

        // Two steps that both skip without ever executing (so no handler /
        // step-package is needed):
        //   • index 0: Condition=Failure with no prior failure → ordinary skip.
        //   • index 1: Condition=Variable referencing a missing variable →
        //     Unresolved (an author error, distinct from an intentional skip).
        // Offline there is no audit log, so the deployment-log level is the only
        // signal that distinguishes the two — the unresolved case must surface
        // at warning while the ordinary skip stays at info.
        var config = new Dictionary<string, string>();
        DeploymentStepPlan[] steps =
        [
            new(0, "FailureSkip", "Kraken.Script", "", "", config, Condition: 1),
            new(1, "UnresolvedVar", "Kraken.Script", "", "", config,
                Condition: 3, ConditionVariableExpression: "#{Missing}"),
        ];
        WritePlan(key, new DeploymentPlan(
            Guid.NewGuid(), "Production", steps,
            new Dictionary<string, string>(), new Dictionary<string, string[]>()));

        var exit = await new OfflineRunner(NullLoggerFactory.Instance)
            .RunAsync(_bundle, key);

        exit.Should().Be(0); // both steps skipped → deployment succeeds

        var lines = (await File.ReadAllTextAsync(
                Path.Combine(_bundle, OfflineBundleLayout.LogFile)))
            .Split('\n');

        lines.Should().Contain(
            l => l.Contains("| info |", StringComparison.Ordinal)
                 && l.Contains("FailureSkip skipped:", StringComparison.Ordinal),
            "an ordinary Run Condition skip stays at info");
        lines.Should().Contain(
            l => l.Contains("| warning |", StringComparison.Ordinal)
                 && l.Contains("UnresolvedVar skipped:", StringComparison.Ordinal),
            "an unresolved Variable condition is an author error and must stand out");
    }

    [Fact]
    public async Task Truthy_array_indexed_variable_condition_runs_offline()
    {
        var key = RandomNumberGenerator.GetBytes(AesGcmCipher.KeyBytes);

        // A single Variable-condition step whose expression references an
        // INDEXED array element. Online the server expands StringArrays into
        // name[i] keys in its condition varDict, so #{Arr[0]} resolves to
        // "true" and the step RUNS. This pins the same decision offline:
        // before the array-index parity fix the offline condition bag carried
        // arrays only in comma-joined scalar form, so #{Arr[0]} was unresolved
        // → the step was (wrongly) skipped with a warning.
        //
        // Required=false so the missing step-package (no handler is registered
        // in this lightweight test) yields a non-required failure rather than a
        // hard abort — the assertion is about the Run/Skip *decision*, proven
        // by the execution-start marker + the absence of a "skipped" line, not
        // about the handler succeeding.
        var config = new Dictionary<string, string>();
        DeploymentStepPlan[] steps =
        [
            new(0, "ArrTrue", "Kraken.Script", "", "", config,
                Condition: 3, ConditionVariableExpression: "#{Arr[0]}", Required: false),
        ];
        WritePlan(key, new DeploymentPlan(
            Guid.NewGuid(), "Production", steps,
            new Dictionary<string, string>(),
            new Dictionary<string, string[]> { ["Arr"] = ["true"] }));

        var exit = await new OfflineRunner(NullLoggerFactory.Instance)
            .RunAsync(_bundle, key);

        exit.Should().Be(0); // non-required handler-resolution failure → success

        var lines = (await File.ReadAllTextAsync(
                Path.Combine(_bundle, OfflineBundleLayout.LogFile)))
            .Split('\n');

        lines.Should().NotContain(
            l => l.Contains("ArrTrue skipped:", StringComparison.Ordinal),
            "a truthy #{Arr[0]} Variable condition must RUN offline, matching online");
        lines.Should().Contain(
            l => l.Contains("Step 1: ArrTrue ---", StringComparison.Ordinal)
                 && !l.Contains("skipped", StringComparison.Ordinal),
            "the step's execution-start marker proves the Run decision was reached");
    }

    private void WritePlan(byte[] key, DeploymentPlan plan)
    {
        var enc = AesGcmCipher.Encrypt(key, JsonSerializer.Serialize(plan, Web));
        File.WriteAllText(Path.Combine(_bundle, OfflineBundleLayout.EncryptedPlanFile), enc);
    }

    public void Dispose()
    {
        try { Directory.Delete(_bundle, recursive: true); }
        catch { /* best effort */ }
    }
}

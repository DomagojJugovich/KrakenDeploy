namespace KrakenDeploy.Contracts.Logging;

/// <summary>
/// Redacts known secret VALUES from free-text log lines before they are persisted
/// or streamed (T0-6). Matching is by value substring, not by variable name, so a
/// secret leaks nothing even when a script echoes it, bakes it into a path, or
/// concatenates it — the resolved sensitive value is replaced with <c>***</c>.
///
/// <para>
/// Lives in Contracts so both the out-of-process agent
/// (<c>DeploymentExecutor</c>) and the server-side step runner
/// (<c>ServerScriptStepRunner</c>) share one implementation. Mirrors the LLM
/// <c>PromptSanitizer</c>: values are matched longest-first so a longer secret
/// isn't chopped by a shorter overlapping one, and empty/whitespace values are
/// ignored (redacting "" would blank the whole line).
/// </para>
/// <para>
/// <see cref="Redact"/> is lock-free (reads an immutable snapshot); <see cref="Add"/>
/// is synchronized so captured sensitive output-variable values can be folded in
/// as steps run without racing concurrent step log output.
/// </para>
/// </summary>
public sealed class SecretRedactor
{
    /// <summary>The replacement token. Matches the "***" convention used elsewhere.</summary>
    public const string Mask = "***";

    private readonly object _gate = new();
    // Immutable snapshot, ordered longest-value-first. Swapped atomically by Add.
    private volatile string[] _values = [];

    public SecretRedactor()
    {
    }

    public SecretRedactor(IEnumerable<string> secretValues) => Add(secretValues);

    /// <summary>
    /// Builds a redactor seeded with every sensitive value in a plan — the
    /// deployment-wide value for each <see cref="DeploymentPlan.SensitiveVariableNames"/>
    /// entry plus any per-step override of it (a variable can be sensitive and
    /// step-scoped, so its value lives in a step's <c>StepVariables</c> delta, not
    /// the deployment-wide map). Used identically by the agent and the server-side
    /// step runner.
    /// </summary>
    public static SecretRedactor ForPlan(DeploymentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var redactor = new SecretRedactor();
        if (plan.SensitiveVariableNames is not { Count: > 0 } names)
        {
            return redactor;
        }

        var values = new List<string>();
        foreach (var name in names)
        {
            if (plan.Variables.TryGetValue(name, out var deploymentWide))
            {
                values.Add(deploymentWide);
            }

            foreach (var step in plan.Steps)
            {
                if (step.StepVariables is { } stepVars && stepVars.TryGetValue(name, out var stepValue))
                {
                    values.Add(stepValue);
                }
            }
        }

        redactor.Add(values);
        return redactor;
    }

    /// <summary>True when at least one secret value is registered.</summary>
    public bool HasSecrets => _values.Length > 0;

    /// <summary>
    /// Registers additional secret values to redact (deduplicated, empty/whitespace
    /// skipped). Safe to call while <see cref="Redact"/> runs concurrently.
    /// </summary>
    public void Add(IEnumerable<string> secretValues)
    {
        ArgumentNullException.ThrowIfNull(secretValues);
        lock (_gate)
        {
            var merged = new HashSet<string>(_values, StringComparer.Ordinal);
            foreach (var value in secretValues)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    merged.Add(value);
                }
            }

            if (merged.Count != _values.Length)
            {
                _values = merged.OrderByDescending(v => v.Length).ToArray();
            }
        }
    }

    /// <summary>
    /// Returns <paramref name="line"/> with every registered secret value replaced
    /// by <see cref="Mask"/>. Returns the input unchanged when there are no secrets.
    /// </summary>
    public string Redact(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return line;
        }

        var values = _values; // single volatile read → stable snapshot for this call
        if (values.Length == 0)
        {
            return line;
        }

        foreach (var value in values)
        {
            if (line.Contains(value, StringComparison.Ordinal))
            {
                line = line.Replace(value, Mask, StringComparison.Ordinal);
            }
        }

        return line;
    }
}

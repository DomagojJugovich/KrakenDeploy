using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Agent.Services;

/// <summary>
/// F5 — startup validation for <see cref="AgentUpdateConfig"/>'s durations, wired with
/// <c>ValidateOnStart</c> so a typo fails the boot loudly instead of degrading the
/// agent silently.
/// <para>
/// The agent had no options validator at all, and <see cref="AgentUpdateConfig.SwapGateTimeout"/>
/// is the knob that most needs one: it bounds how long the self-upgrade may hold a
/// QUEUED exclusive waiter on the machine execution gate, and because that gate is
/// writer-fair a queued writer blocks every new deployment and ad-hoc script on the box
/// while it waits. Every malformed value defeats exactly the guarantee the knob exists
/// to provide:
/// </para>
/// <list type="bullet">
///   <item><c>"5"</c> binds as FIVE DAYS (the same footgun
///     <c>EngineOptionsValidator</c> and <c>Adhoc:MaxTotalDuration</c> already guard):
///     five days of whole-machine blockage, and small enough to slip under the gate's
///     own ~24.8-day <c>CancelAfter</c> clamp.</item>
///   <item><c>"-00:00:00.001"</c> is <see cref="Timeout.InfiniteTimeSpan"/>, so the
///     wait is never bounded at all and the writer parks FOREVER — the agent heartbeats
///     Online and silently accepts no work until it is restarted.</item>
///   <item><c>"00:00:00"</c> degrades the acquisition to a non-blocking probe, so the
///     "block new work while swapping" guarantee never engages.</item>
///   <item>Any other negative value throws out of the gate on every tick, which the
///     update loop swallows as a generic retry warning — self-upgrade is then dead
///     with no mention of the gate or the key.</item>
/// </list>
/// <para>
/// Mirrors <c>EngineOptionsValidator</c>: the bare-number form is rejected from the RAW
/// configured string, because once the binder has produced a <see cref="TimeSpan"/>
/// <c>"5"</c> and <c>"5.00:00:00"</c> are indistinguishable and only the second can
/// have been written on purpose.
/// </para>
/// </summary>
public sealed class AgentUpdateConfigValidator(IConfiguration configuration)
    : IValidateOptions<AgentUpdateConfig>
{
    /// <summary>Configuration section <see cref="AgentUpdateConfig"/> binds to.</summary>
    public const string SectionName = "Agent:Update";

    /// <summary>
    /// Largest accepted value for the SWAP WINDOW specifically. That wait exists so a
    /// wedged holder cannot stop the agent from accepting work, and because the gate is
    /// writer-fair the wait itself blocks the machine — so anything beyond an hour has
    /// already failed its own purpose.
    /// <para>
    /// Deliberately NOT applied to <see cref="AgentUpdateConfig.CheckInterval"/> or
    /// <see cref="AgentUpdateConfig.HealthCheckTimeout"/>: neither blocks the machine,
    /// and a long poll interval is a legitimate choice on a metered or segmented link
    /// (it is also the SAFE direction for the cross-field rule below). An earlier cut
    /// applied one hour to all three, which turned a sensible twice-daily poll into a
    /// fleet-wide boot failure.
    /// </para>
    /// </summary>
    public static readonly TimeSpan MaxAcceptedSwapWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// Ceiling for the remaining durations — generous, and only there to catch a value
    /// so large it cannot be deliberate.
    /// </summary>
    public static readonly TimeSpan MaxAcceptedDuration = TimeSpan.FromDays(7);

    public ValidateOptionsResult Validate(string? name, AgentUpdateConfig options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Nothing here governs a disabled updater, and `ValidateOnStart` makes every
        // failure a boot failure — so a legacy value must not brick an agent whose
        // auto-update is switched off and which therefore never reads these knobs.
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var section = configuration.GetSection(SectionName);
        var failures = new List<string>();

        Check(nameof(AgentUpdateConfig.SwapGateTimeout), options.SwapGateTimeout,
            MaxAcceptedSwapWindow);
        Check(nameof(AgentUpdateConfig.CheckInterval), options.CheckInterval,
            MaxAcceptedDuration);
        Check(nameof(AgentUpdateConfig.HealthCheckTimeout), options.HealthCheckTimeout,
            MaxAcceptedDuration);

        if (options.MaxHealthAttempts <= 0)
        {
            failures.Add(
                $"{SectionName}:{nameof(AgentUpdateConfig.MaxHealthAttempts)} must be " +
                $"positive, got {options.MaxHealthAttempts}.");
        }

        // The swap wait must be shorter than the interval that re-arms it. Equal values
        // (the originally shipped 5/5 pair) let the updater re-queue a blocking writer
        // back-to-back: PeriodicTimer coalesces the tick that elapsed during the wait, so
        // the next iteration starts immediately and the machine is blocked for essentially
        // the whole maintenance window while never completing a swap.
        // Reported against BOTH keys, because the operator may have set only the other
        // one: with SwapGateTimeout defaulting to 2 min, tightening CheckInterval to
        // 2 min trips this, and an error naming only SwapGateTimeout would send them to
        // a key they never touched.
        if (options.SwapGateTimeout >= options.CheckInterval)
        {
            failures.Add(
                $"{SectionName}:{nameof(AgentUpdateConfig.SwapGateTimeout)} " +
                $"({options.SwapGateTimeout}) must be shorter than " +
                $"{SectionName}:{nameof(AgentUpdateConfig.CheckInterval)} " +
                $"({options.CheckInterval}), or the updater re-queues a machine-blocking " +
                "writer on every tick. Raise CheckInterval or lower SwapGateTimeout — " +
                "either resolves it, and only one of them may be a value you set.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);

        void Check(string key, TimeSpan value, TimeSpan max)
        {
            var raw = section[key];
            if (raw is not null && IsBareNumber(raw))
            {
                failures.Add(
                    $"{SectionName}:{key} is '{raw}', which TimeSpan binding reads as " +
                    $"{raw.Trim()} DAYS, not minutes or hours. Write the unit out as " +
                    "[d.]hh:mm:ss — '00:05:00' for five minutes.");
                return;
            }

            if (value <= TimeSpan.Zero)
            {
                failures.Add(
                    $"{SectionName}:{key} must be a positive duration, got {value}. " +
                    "Note -00:00:00.001 is Timeout.InfiniteTimeSpan, which would make " +
                    "the wait unbounded.");
            }
            else if (value > max)
            {
                failures.Add($"{SectionName}:{key} is {value}, above the {max} ceiling.");
            }
        }
    }

    /// <summary>Digits only (optionally signed) — the form
    /// <see cref="TimeSpan.Parse(string)"/> silently interprets as whole days.</summary>
    private static bool IsBareNumber(string raw)
    {
        var trimmed = raw.AsSpan().Trim();
        if (trimmed.Length > 0 && (trimmed[0] is '-' or '+'))
        {
            trimmed = trimmed[1..];
        }
        if (trimmed.IsEmpty)
        {
            return false;
        }
        foreach (var c in trimmed)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }
        return true;
    }
}

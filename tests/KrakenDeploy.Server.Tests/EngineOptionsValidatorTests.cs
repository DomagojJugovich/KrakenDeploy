using FluentAssertions;
using KrakenDeploy.Server.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// F2-followup 5 — the <c>Engine</c> durations are the one config surface where a
/// plausible typo ("4" meaning four minutes) binds as four DAYS and then breaks
/// EVERY deployment at dispatch, because the F2 backstop feeds
/// <c>MaxTargetWaveDuration + MaxTargetQueueWait</c> straight into
/// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>. These tests pin the
/// startup refusal so that failure can never reach production as a per-deployment
/// "Parameter 'delay'" crash.
/// </summary>
public class EngineOptionsValidatorTests
{
    /// <summary>Binds an in-memory <c>Engine</c> section exactly the way
    /// <c>Program</c> does (Bind + ValidateOnStart + the validator) and resolves the
    /// options, so a regression in the registration is caught too, not only one in
    /// the validator.</summary>
    private static EngineOptions Resolve(params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(
                s => $"{EngineOptions.SectionName}:{s.Key}", s => (string?)s.Value))
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddOptions<EngineOptions>()
            .Bind(config.GetSection(EngineOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<EngineOptions>, EngineOptionsValidator>();

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<EngineOptions>>().Value;
    }

    [Fact]
    public void No_Engine_section_leaves_the_shipped_defaults_valid()
    {
        var options = Resolve();

        options.MaxTargetWaveDuration.Should().Be(TimeSpan.FromHours(1));
        options.MaxTargetQueueWait.Should().Be(EngineOptions.DefaultMaxTargetQueueWait);
        options.MaxConcurrentTasks.Should().Be(5);
    }

    [Fact]
    public void Ordinary_durations_bind_and_validate()
    {
        var options = Resolve(
            ("MaxTargetWaveDuration", "00:30:00"),
            ("MaxTargetQueueWait", "04:00:00"),
            ("MaxDeployReleaseWaitDuration", "02:00:00"),
            ("AgentDisconnectWaveGrace", "00:05:00"),
            ("MaxConcurrentTasks", "12"));

        options.MaxTargetQueueWait.Should().Be(TimeSpan.FromHours(4));
        options.MaxConcurrentTasks.Should().Be(12);
    }

    /// <summary>
    /// THE motivating case, and the reason a magnitude ceiling alone is not enough:
    /// "4" is what an operator writes meaning four minutes or four hours, binding
    /// reads it as four DAYS, and four days is INSIDE any ceiling generous enough to
    /// be useful. Nothing crashes — the queue-wait backstop silently becomes four
    /// days and a wedged wave hangs for most of a week.
    /// </summary>
    [Theory]
    [InlineData("MaxTargetQueueWait", "4")]
    [InlineData("MaxTargetWaveDuration", "1")]
    [InlineData("MaxDeployReleaseWaitDuration", " 2 ")]
    [InlineData("AgentDisconnectWaveGrace", "5")]
    public void Bare_number_is_refused_because_binding_reads_it_as_days(
        string key, string value)
    {
        var act = () => Resolve((key, value));

        act.Should().Throw<OptionsValidationException>()
            .WithMessage($"*{key}*")
            .WithMessage("*DAYS*");
    }

    /// <summary>The counterpart: spelling the unit out is accepted at the same
    /// magnitude, because writing <c>4.00:00:00</c> is something you can only do on
    /// purpose. Without this, the check would just be a lower ceiling in disguise.</summary>
    [Fact]
    public void The_same_duration_written_explicitly_as_days_is_accepted()
    {
        var options = Resolve(("MaxTargetQueueWait", "4.00:00:00"));

        options.MaxTargetQueueWait.Should().Be(TimeSpan.FromDays(4));
    }

    /// <summary>
    /// The hard failure the ceiling exists for: a value large enough that
    /// <c>wave + queue</c> overflows <c>CancelAfter</c>'s <c>uint.MaxValue - 1</c> ms
    /// limit. Pre-followup this bound fine and then threw
    /// <see cref="ArgumentOutOfRangeException"/> on every single wave dispatch.
    /// </summary>
    [Fact]
    public void Duration_past_the_CancelAfter_limit_is_refused_at_startup()
    {
        var act = () => Resolve(("MaxTargetWaveDuration", "60.00:00:00"));

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*MaxTargetWaveDuration*")
            .WithMessage("*ceiling*");
    }

    [Fact]
    public void The_refused_ceiling_would_actually_have_broken_CancelAfter()
    {
        // Pins the premise of the test above rather than trusting the arithmetic:
        // 60 days of wave budget plus the default queue wait really is out of range.
        using var cts = new CancellationTokenSource();
        var backstop = TimeSpan.FromDays(60) + EngineOptions.DefaultMaxTargetQueueWait;

        var act = () => cts.CancelAfter(backstop);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("MaxTargetWaveDuration", "00:00:00")]
    [InlineData("MaxTargetWaveDuration", "-00:05:00")]
    [InlineData("MaxTargetQueueWait", "00:00:00")]
    [InlineData("MaxDeployReleaseWaitDuration", "00:00:00")]
    public void Non_positive_durations_are_refused(string key, string value)
    {
        var act = () => Resolve((key, value));

        act.Should().Throw<OptionsValidationException>().WithMessage($"*{key}*");
    }

    /// <summary>Zero is the documented "disable the disconnect monitor" switch for
    /// this one knob, so it must survive validation — a negative value is still a
    /// typo, but B3 documents zero-or-negative as disabling, so only reject values
    /// past the ceiling here.</summary>
    [Fact]
    public void Zero_disconnect_grace_stays_legal_because_it_disables_the_monitor()
    {
        var options = Resolve(("AgentDisconnectWaveGrace", "00:00:00"));

        options.AgentDisconnectWaveGrace.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Non_positive_task_cap_is_refused()
    {
        var act = () => Resolve(("MaxConcurrentTasks", "0"));

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*MaxConcurrentTasks*");
    }

    [Fact]
    public void All_offending_keys_are_reported_together()
    {
        var act = () => Resolve(
            ("MaxTargetWaveDuration", "00:00:00"),
            ("MaxTargetQueueWait", "9999.00:00:00"),
            ("MaxConcurrentTasks", "-1"));

        var message = act.Should().Throw<OptionsValidationException>().Which.Message;

        message.Should().Contain("MaxTargetWaveDuration")
            .And.Contain("MaxTargetQueueWait")
            .And.Contain("MaxConcurrentTasks");
    }
}

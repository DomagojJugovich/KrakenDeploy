using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Transport;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// Unit tests for the M14.3.1 <see cref="LogSequencer"/> carrier. Pin
/// the contract that two concurrent <c>Next()</c> calls produce two
/// distinct sequences. This is the structural prerequisite for M14.4
/// wave-parallel step execution — without the lock, two parallel waves
/// writing log entries would assign the same sequence twice and the
/// live-log UI would render entries in an undefined order.
/// </summary>
public sealed class LogSequencerTests
{
    [Fact]
    public void Sequential_calls_yield_consecutive_sequences()
    {
        var deployment = NewDeployment(startAt: 0);
        var seq = new LogSequencer(deployment);

        seq.Next().Should().Be(0);
        seq.Next().Should().Be(1);
        seq.Next().Should().Be(2);

        deployment.NextLogSequence.Should().Be(3,
            "carrier advances the deployment entity's counter as a side-effect — " +
            "downstream SaveChanges persists the new value");
    }

    [Fact]
    public void Honours_existing_starting_value()
    {
        // After a deployment was previously dispatched and persisted some
        // log entries, NextLogSequence has a non-zero starting value.
        // New LogSequencer picks up where the previous run left off.
        var deployment = NewDeployment(startAt: 17);
        var seq = new LogSequencer(deployment);

        seq.Next().Should().Be(17);
        seq.Next().Should().Be(18);
    }

    [Fact]
    public async Task Concurrent_increments_produce_unique_sequences()
    {
        // M14.4 prerequisite test. Two parallel "waves" of step execution
        // share the same LogSequencer. Without the lock, the
        // read-modify-write race assigns the same sequence to multiple
        // log entries. With the lock, every Next() returns a unique value.
        const int parallelism = 16;
        const int callsPerThread = 1000;
        var deployment = NewDeployment(startAt: 0);
        var seq = new LogSequencer(deployment);

        var observed = new System.Collections.Concurrent.ConcurrentBag<int>();
        var barrier = new Barrier(parallelism);

        var tasks = new Task[parallelism];
        for (var t = 0; t < parallelism; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (var i = 0; i < callsPerThread; i++)
                {
                    observed.Add(seq.Next());
                }
            });
        }

        await Task.WhenAll(tasks);

        observed.Should().HaveCount(parallelism * callsPerThread,
            "every call returns; none are dropped");
        observed.Distinct().Should().HaveCount(parallelism * callsPerThread,
            "every returned sequence is unique — the lock serialises the " +
            "read-modify-write under contention");
        deployment.NextLogSequence.Should().Be(parallelism * callsPerThread,
            "the carrier mutates the entity's counter atomically");
    }

    private static Deployment NewDeployment(int startAt)
    {
        // Construct just enough of a Deployment for the test. The entity's
        // navigation properties stay null — LogSequencer only touches the
        // NextLogSequence scalar property.
        return new Deployment
        {
            NextLogSequence = startAt,
            Release         = null!,
            Environment     = null!,
        };
    }
}

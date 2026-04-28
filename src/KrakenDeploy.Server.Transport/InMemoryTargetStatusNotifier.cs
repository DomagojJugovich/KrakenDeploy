using KrakenDeploy.Server.Core.Domain.Targets;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Thread-safe in-process implementation of <see cref="ITargetStatusNotifier"/>.
/// The null-conditional delegate invocation captures a snapshot of the delegate
/// chain before calling it, making concurrent subscribe/unsubscribe safe.
/// </summary>
public sealed class InMemoryTargetStatusNotifier : ITargetStatusNotifier
{
    public event Action<Guid, TargetStatus, DateTimeOffset?>? TargetStatusChanged;

    public void Publish(Guid targetId, TargetStatus status, DateTimeOffset? lastSeenUtc)
        => TargetStatusChanged?.Invoke(targetId, status, lastSeenUtc);
}

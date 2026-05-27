using System.Threading.Channels;

namespace KrakenDeploy.Server.Data;

/// <summary>
/// M11.C — thin wrapper around an unbounded <see cref="Channel{T}"/> of
/// failed-deployment IDs awaiting AI diagnosis. Registered as a singleton so
/// the orchestrator (<c>DeploymentWorker</c>, writer) and the
/// <c>DeploymentDiagnosisWorker</c> (reader) share one instance without DI
/// ambiguity against the deployment-dispatch <c>Channel&lt;Guid&gt;</c> or
/// <see cref="RunbookRunChannel"/> (same typed-wrapper pattern as the latter).
/// <para>
/// Decoupling diagnosis onto its own channel keeps it strictly best-effort:
/// the orchestrator's <c>FailAsync</c> drops the id and moves on; a slow or
/// disabled diagnosis never holds up deployment finalisation, and the
/// transport layer needs no Hangfire dependency.
/// </para>
/// </summary>
public sealed class DeploymentDiagnosisChannel
{
    private readonly Channel<Guid> _inner =
        Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelWriter<Guid> Writer => _inner.Writer;
    public ChannelReader<Guid> Reader => _inner.Reader;
}

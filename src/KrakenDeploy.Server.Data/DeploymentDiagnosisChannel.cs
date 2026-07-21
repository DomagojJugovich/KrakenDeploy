using System.Threading.Channels;

namespace KrakenDeploy.Server.Data;

/// <summary>
/// M11.C — thin wrapper around an unbounded <see cref="Channel{T}"/> of
/// failed-deployment IDs awaiting AI diagnosis. Registered as a singleton so
/// the orchestrator (<c>DeploymentWorker</c>, writer) and the
/// <c>DeploymentDiagnosisWorker</c> (reader) share one instance without DI
/// ambiguity against the shared task-dispatch <c>Channel&lt;TenantWorkItem&gt;</c>.
/// <para>
/// Decoupling diagnosis onto its own channel keeps it strictly best-effort:
/// the orchestrator's <c>FailAsync</c> drops the id and moves on; a slow or
/// disabled diagnosis never holds up deployment finalisation, and the
/// transport layer needs no Hangfire dependency.
/// </para>
/// </summary>
public sealed class DeploymentDiagnosisChannel
{
    private readonly Channel<TenantWorkItem> _inner =
        Channel.CreateUnbounded<TenantWorkItem>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelWriter<TenantWorkItem> Writer => _inner.Writer;
    public ChannelReader<TenantWorkItem> Reader => _inner.Reader;
}

using System.Threading.Channels;

namespace KrakenDeploy.Server.Data;

/// <summary>
/// Thin wrapper around an unbounded <see cref="Channel{T}"/> of runbook-run IDs.
/// Registered as a singleton so <see cref="Services.RunbookService"/> (writer) and
/// <c>RunbookRunWorker</c> (reader) share the same channel instance without DI
/// ambiguity with the deployment <c>Channel&lt;Guid&gt;</c>.
/// </summary>
public sealed class RunbookRunChannel
{
    private readonly Channel<TenantWorkItem> _inner =
        Channel.CreateUnbounded<TenantWorkItem>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelWriter<TenantWorkItem> Writer => _inner.Writer;
    public ChannelReader<TenantWorkItem> Reader => _inner.Reader;
}

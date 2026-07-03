using Yarp.ReverseProxy.Forwarder;

namespace KrakenDeploy.Router;

/// <summary>
/// Default forwarding transform + one critical override: the outgoing request
/// keeps the <b>original</b> <c>Host</c> header instead of the slot's localhost
/// authority. Multi-account resolves the business account from
/// <c>Request.Host</c> (subdomain → catalog), so rewriting Host to
/// <c>localhost:5081</c> would break account resolution for every request that
/// crosses the router.
/// </summary>
internal sealed class HostPreservingTransformer : HttpTransformer
{
    public static HostPreservingTransformer Instance { get; } = new();

    private HostPreservingTransformer()
    {
    }

    public override async ValueTask TransformRequestAsync(
        HttpContext httpContext,
        HttpRequestMessage proxyRequest,
        string destinationPrefix,
        CancellationToken cancellationToken)
    {
        await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken)
            .ConfigureAwait(false);

        if (httpContext.Request.Host.HasValue)
        {
            proxyRequest.Headers.Host = httpContext.Request.Host.Value;
        }
    }
}

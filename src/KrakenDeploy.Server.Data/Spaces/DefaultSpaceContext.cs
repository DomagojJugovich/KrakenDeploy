using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Spaces;

namespace KrakenDeploy.Server.Data.Spaces;

/// <summary>
/// Fallback <see cref="ISpaceContext"/> that always reports the
/// <see cref="WellKnown.DefaultSpaceId"/>. Registered by
/// <c>AddKrakenDeployData</c> so unit tests, the migration host, and the
/// <c>users create-admin</c> CLI all have a working <c>ISpaceContext</c>
/// without needing the HTTP pipeline. The Server project replaces this with
/// <c>HttpSpaceContext</c> for normal request handling.
/// </summary>
public sealed class DefaultSpaceContext : ISpaceContext
{
    private readonly Stack<Guid> _overrides = new();

    public Guid CurrentSpaceId => _overrides.Count > 0 ? _overrides.Peek() : WellKnown.DefaultSpaceId;

    public bool IsSystemAdmin => true; // tests + CLI act with full privileges

    public IDisposable WithSpace(Guid spaceId)
    {
        _overrides.Push(spaceId);
        return new PopOnDispose(_overrides);
    }

    private sealed class PopOnDispose(Stack<Guid> stack) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (stack.Count > 0)
            {
                stack.Pop();
            }
        }
    }
}

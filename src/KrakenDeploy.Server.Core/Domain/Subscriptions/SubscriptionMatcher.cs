using KrakenDeploy.Server.Core.Domain.Audit;

namespace KrakenDeploy.Server.Core.Domain.Subscriptions;

/// <summary>
/// Pure matching function — given a subscription and an event row,
/// returns whether the event satisfies all of the subscription's filters.
/// Tested directly without spinning up the EF / Hangfire pipeline.
///
/// <para>
/// Matching is AND-of-dimensions (event-type AND project AND environment
/// must all pass) and OR-within-dimension (project A OR project B OR ...).
/// Empty list on any dimension = "match anything for that dimension" —
/// the common "subscribe to all events in my Space" subscription has
/// every list empty.
/// </para>
/// </summary>
public static class SubscriptionMatcher
{
    /// <summary>
    /// True when <paramref name="evt"/> satisfies every filter on
    /// <paramref name="subscription"/>. <paramref name="resolveProjectId"/>
    /// is called lazily when (and only when) the subscription has a
    /// non-empty project filter — most subscriptions don't, so the lookup
    /// stays cheap on the common path.
    /// </summary>
    public static bool Matches(
        EventSubscription subscription,
        AuditEntry evt,
        Func<AuditEntry, Guid?>? resolveProjectId = null,
        Func<AuditEntry, Guid?>? resolveEnvironmentId = null)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(evt);

        if (subscription.Disabled) { return false; }

        // Space scope: a Space-scoped subscription only matches events
        // from its own Space. A system-wide subscription (SpaceId=null)
        // matches across every Space. System-level audit rows (no Space
        // — e.g. License.Uploaded) only match system-wide subscriptions.
        if (subscription.SpaceId is { } subSpace)
        {
            if (evt.SpaceId != subSpace) { return false; }
        }

        // Event-type filter. Patterns can be exact ("Deployment.Failed")
        // or category wildcard ("Deployment.*"). Empty list = any.
        if (subscription.EventTypePatterns.Count > 0)
        {
            var matched = false;
            foreach (var pattern in subscription.EventTypePatterns)
            {
                if (EventTypeMatches(pattern, evt.EventType))
                {
                    matched = true;
                    break;
                }
            }
            if (!matched) { return false; }
        }

        // Project filter — lazy resolve.
        if (subscription.ProjectIds.Count > 0)
        {
            if (resolveProjectId is null) { return false; }
            var projectId = resolveProjectId(evt);
            if (projectId is null || !subscription.ProjectIds.Contains(projectId.Value))
            {
                return false;
            }
        }

        // Environment filter — lazy resolve.
        if (subscription.EnvironmentIds.Count > 0)
        {
            if (resolveEnvironmentId is null) { return false; }
            var envId = resolveEnvironmentId(evt);
            if (envId is null || !subscription.EnvironmentIds.Contains(envId.Value))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Matches an event-type string against a pattern. Pattern is either
    /// an exact string (case-insensitive equality) or
    /// <c>"Category.*"</c> for prefix matching (everything before the
    /// final dot must match the event-type's category segment).
    /// </summary>
    public static bool EventTypeMatches(string pattern, string eventType)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(eventType);

        if (pattern.EndsWith(".*", StringComparison.Ordinal))
        {
            var prefix = pattern[..^1]; // keeps the trailing "."
            return eventType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        // Plain "*" — match anything. Useful for "alert me on everything"
        // subscriptions; the system-wide audit-tail use case.
        if (pattern == "*") { return true; }

        return string.Equals(pattern, eventType, StringComparison.OrdinalIgnoreCase);
    }
}

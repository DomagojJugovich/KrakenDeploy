using Hangfire.AspNetCore;
using Hangfire.Dashboard;
using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Platform;
using KrakenDeploy.Server.Core.Domain.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Server.Hangfire;

/// <summary>
/// Restricts the Hangfire dashboard to users who hold the
/// <see cref="Permission.AdministerSystem"/> permission through a SYSTEM-scope
/// assignment (a Space-pinned grant does not open this instance-wide surface — WP3-c).
/// Unauthenticated requests and insufficient-permission requests are denied.
/// <para>
/// In multi-account mode the Hangfire job store is a SINGLE shared control-plane store,
/// so the dashboard shows platform-wide job state spanning every account. The
/// <see cref="Permission.AdministerSystem"/> permission is resolved from the active
/// account's tenant database, so a per-account System Administrator must NOT be able to
/// open it — that would disclose other accounts' scheduler state. The dashboard is
/// therefore denied on any tenant subdomain and reachable only on the control-plane host
/// (which, until a control-plane admin surface exists, leaves it effectively closed in
/// multi-account — fail safe). Single-instance mode is unchanged: one store, one admin.
/// </para>
/// </summary>
public sealed class HangfireDashboardAuthFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        // GetHttpContext() is the Hangfire.AspNetCore extension on DashboardContext.
        var http = context.GetHttpContext();

        if (http is null)
        {
            return false;
        }

        // Saas: the dashboard reflects the shared control-plane store, so deny it
        // on tenant subdomains regardless of the caller's (per-account) permissions — a
        // tenant admin must not see platform-wide / cross-account job state. Only the
        // control-plane host may reach it. No-op under the on-prem topologies.
        var deployment = http.RequestServices.GetService<DeploymentOptions>();
        if (deployment?.Topology == DeploymentTopology.Saas)
        {
            var options = http.RequestServices.GetService<IOptions<MultiAccountOptions>>();
            if (options is not null
                && HostParser.ExtractSubdomain(http.Request.Host.Host, options.Value.BaseDomain) is not null)
            {
                return false;
            }
        }

        if (http.User.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var evaluator = http.RequestServices
            .GetService<IPermissionEvaluator>();

        if (evaluator is null)
        {
            return false;
        }

        // IDashboardAuthorizationFilter.Authorize is synchronous; block the
        // thread for the permission check.  The Hangfire dashboard is accessed
        // rarely and only by admins, so this is acceptable.
        return evaluator
            .HasPermissionAsync(http.User, Permission.AdministerSystem)
            .GetAwaiter()
            .GetResult();
    }
}

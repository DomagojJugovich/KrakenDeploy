using Hangfire.AspNetCore;
using Hangfire.Dashboard;
using KrakenDeploy.Server.Core.Domain.Security;
using Microsoft.Extensions.DependencyInjection;

namespace KrakenDeploy.Server.Hangfire;

/// <summary>
/// Restricts the Hangfire dashboard to users who hold the
/// <see cref="Permission.AdministerSystem"/> permission (i.e. System Administrators).
/// Unauthenticated requests and insufficient-permission requests are denied.
/// </summary>
public sealed class HangfireDashboardAuthFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        // GetHttpContext() is the Hangfire.AspNetCore extension on DashboardContext.
        var http = context.GetHttpContext();

        if (http is null || http.User.Identity?.IsAuthenticated != true)
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

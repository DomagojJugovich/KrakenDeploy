using KrakenDeploy.Server.Core.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Server.Auth;

/// <summary>
/// Dynamic <see cref="IAuthorizationPolicyProvider"/> that builds a policy
/// on demand for any policy name beginning with <see cref="PolicyPrefix"/>
/// (e.g. <c>"perm:ProjectView"</c>). The remainder is parsed as a
/// <see cref="Permission"/> enum value and turned into a single
/// <see cref="PermissionRequirement"/>.
/// <para>
/// Lets endpoints and Blazor components reference permissions by name
/// (<c>RequireAuthorization("perm:ProjectView")</c>) without registering
/// a policy per Permission in <c>Startup</c> — there are 100+ of them.
/// </para>
/// <para>
/// Falls back to the default provider for non-permission policy names
/// (e.g. policies registered explicitly via <c>AddPolicy</c>).
/// </para>
/// </summary>
public sealed class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    /// <summary>Prefix marking a permission-derived policy name.</summary>
    public const string PolicyPrefix = "perm:";

    private readonly DefaultAuthorizationPolicyProvider _fallback;

    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    {
        _fallback = new DefaultAuthorizationPolicyProvider(options);
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync()
        => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync()
        => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(PolicyPrefix, StringComparison.Ordinal))
        {
            var permName = policyName[PolicyPrefix.Length..];
            if (Enum.TryParse<Permission>(permName, ignoreCase: false, out var permission))
            {
                var policy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .AddRequirements(new PermissionRequirement(permission))
                    .Build();
                return Task.FromResult<AuthorizationPolicy?>(policy);
            }
        }

        return _fallback.GetPolicyAsync(policyName);
    }

    /// <summary>Convenience helper for building a policy name from a Permission.</summary>
    public static string PolicyNameFor(Permission permission)
        => PolicyPrefix + permission.ToString();
}

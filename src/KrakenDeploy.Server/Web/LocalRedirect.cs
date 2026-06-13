namespace KrakenDeploy.Server.Web;

/// <summary>
/// Sanitises a caller-supplied return URL to a safe LOCAL path, defeating
/// open-redirect attacks. A safe value starts with a single <c>/</c> that is NOT
/// followed by another <c>/</c> or a <c>\</c> — browsers treat <c>//host</c> and
/// <c>/\host</c> as protocol-relative and navigate off-site, and
/// <c>Uri.IsWellFormedUriString(.., Relative)</c> wrongly accepts <c>//host</c>.
/// Anything else (absolute URLs, schemes, no leading slash) collapses to <c>/</c>.
/// </summary>
public static class LocalRedirect
{
    /// <summary>The return url when it is a safe same-site local path, else "/".</summary>
    public static string MakeSafe(string? returnUrl)
        => IsSafe(returnUrl) ? returnUrl! : "/";

    /// <summary>True when <paramref name="returnUrl"/> is a safe same-site local path.</summary>
    public static bool IsSafe(string? returnUrl)
        => !string.IsNullOrEmpty(returnUrl)
           && returnUrl[0] == '/'
           && (returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\'));
}

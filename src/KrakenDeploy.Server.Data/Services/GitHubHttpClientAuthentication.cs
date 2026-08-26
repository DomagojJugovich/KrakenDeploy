using System.Net.Http.Headers;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>Applies the current account's effective GitHub token to a fresh client.</summary>
public static class GitHubHttpClientAuthentication
{
    public static async Task ApplyAsync(
        HttpClient client,
        EffectiveSettingsService settings,
        Uri destination,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!IsTrustedGitHubHost(destination.Host))
        {
            client.DefaultRequestHeaders.Authorization = null;
            return;
        }

        var token = await settings.GetGitHubTokenAsync(ct).ConfigureAwait(false);
        client.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(token)
            ? null
            : new AuthenticationHeaderValue("Bearer", token);
    }

    private static bool IsTrustedGitHubHost(string host) =>
        host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
}

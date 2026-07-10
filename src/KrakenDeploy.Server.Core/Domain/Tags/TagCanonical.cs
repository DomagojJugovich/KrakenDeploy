namespace KrakenDeploy.Server.Core.Domain.Tags;

/// <summary>
/// The Octopus-parity canonical tag string: <c>"TagSetName/TagName"</c>.
/// Display / interop format ONLY — persisted references use tag Guids so
/// renames never break stored state. Consumed by e.g. the
/// <c>Octopus.Deployment.Tenant.Tags</c> system variable.
/// </summary>
public static class TagCanonical
{
    public const char Separator = '/';

    public static string Format(string tagSetName, string tagName)
        => $"{tagSetName}{Separator}{tagName}";
}

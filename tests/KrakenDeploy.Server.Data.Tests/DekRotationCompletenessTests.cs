using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Ai;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Structural guard for the DEK-rotation walk (M13.D.2). The
/// <c>DekRotationWalk.ReEncryptAllAsync</c> re-encrypts every secret; if a new
/// encrypted domain property is added without walking it, that data silently
/// becomes undecryptable on the next DEK rotation. This test reflects over the
/// domain assembly for every <c>*Encrypted</c> string property and fails CI if
/// one appears that the walk (mirrored in <see cref="WalkedEncryptedProperties"/>)
/// doesn't cover — forcing the author to update both.
/// </summary>
public sealed class DekRotationCompletenessTests
{
    /// <summary>Every <c>*Encrypted</c> domain property re-encrypted by
    /// <c>DekRotationWalk.ReEncryptAllAsync</c>. Keep in lockstep with the walk.</summary>
    private static readonly HashSet<string> WalkedEncryptedProperties =
    [
        "SpaceAiSettings.ApiKeyEncrypted",
        "SmtpSettings.PasswordEncrypted",
        "IdentityProvider.ClientSecretEncrypted",
        "OfflineDropConfig.HmacKeyEncrypted",
        "OfflineDropConfig.BundleKeyEncrypted",
        "OfflineDropConfig.SmtpPasswordEncrypted",
        "OfflineDropConfig.WebhookSecretEncrypted",
        "OfflineDropConfig.FileSharePasswordEncrypted",
    ];

    [Fact]
    public void Every_encrypted_domain_property_is_covered_by_the_DEK_rotation_walk()
    {
        // Any Core type (SpaceAiSettings) anchors the domain assembly.
        var domainAssembly = typeof(SpaceAiSettings).Assembly;

        var discovered = domainAssembly.GetTypes()
            .SelectMany(t => t.GetProperties())
            .Where(p => p.PropertyType == typeof(string)
                     && p.Name.EndsWith("Encrypted", StringComparison.Ordinal))
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}")
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        discovered.Should().BeSubsetOf(WalkedEncryptedProperties,
            "every *Encrypted domain property must be re-encrypted by " +
            "DekRotationWalk.ReEncryptAllAsync. If you added one, walk it there AND add it to " +
            "WalkedEncryptedProperties here. (The two conditionally-encrypted 'Value' columns — " +
            "Variable.Value / VariableSnapshot.Value — are walked separately via Type==Sensitive " +
            "and are not *Encrypted-named, so they're intentionally not in this reflection set. " +
            "DataEncryptionKey.WrappedDek is the DEK itself — it must NOT be re-encrypted by the walk.)");
    }
}

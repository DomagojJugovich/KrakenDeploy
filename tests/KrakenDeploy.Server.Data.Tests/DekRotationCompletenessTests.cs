using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Core.Domain.Settings;
using KrakenDeploy.Server.Data.Settings;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Structural guard for the DEK-rotation walk (M13.D.2). If an encrypted domain
/// property is added without walking it, that data silently becomes undecryptable
/// on the next DEK rotation. Two invariants are enforced:
/// <list type="number">
///   <item>Every <c>*Encrypted</c> string property on a NON-settings domain type
///     is covered by an explicit step in <c>DekRotationWalk.ReEncryptAllAsync</c>
///     (mirrored in <see cref="WalkedEncryptedProperties"/>).</item>
///   <item>Every <see cref="ISettingsDocument"/> that carries a <c>*Encrypted</c>
///     member is registered in <see cref="SettingsDocumentCatalog"/>, so the
///     walk's generic settings step re-encrypts it automatically.</item>
/// </list>
/// </summary>
public sealed class DekRotationCompletenessTests
{
    /// <summary>Non-settings <c>*Encrypted</c> properties walked by their own
    /// explicit step. Keep in lockstep with <c>DekRotationWalk</c>. Settings
    /// documents are intentionally excluded — they are covered generically and
    /// asserted by the second test below.</summary>
    private static readonly HashSet<string> WalkedEncryptedProperties =
    [
        "IdentityProvider.ClientSecretEncrypted",
        "OfflineDropConfig.HmacKeyEncrypted",
        "OfflineDropConfig.BundleKeyEncrypted",
        "OfflineDropConfig.SmtpPasswordEncrypted",
        "OfflineDropConfig.WebhookSecretEncrypted",
        "OfflineDropConfig.FileSharePasswordEncrypted",
        // WP3 — a paused task's resume checkpoint. Non-null only while a task waits at
        // a manual-intervention gate, but it embeds captured SENSITIVE output values,
        // so a rotation that skipped it would leave every outstanding approval
        // un-resumable (the decrypt under the new DEK would throw on approve).
        "ServerTask.PauseCheckpointEncrypted",
        "PromptedVariableFormValues.SensitiveValuesEncrypted",
    ];

    [Fact]
    public void Every_non_settings_encrypted_property_is_walked_explicitly()
    {
        // Any Core type (SpaceAiSettings) anchors the domain assembly.
        var domainAssembly = typeof(SpaceAiSettings).Assembly;

        var discovered = domainAssembly.GetTypes()
            .Where(t => !typeof(ISettingsDocument).IsAssignableFrom(t))
            .SelectMany(t => t.GetProperties())
            .Where(p => p.PropertyType == typeof(string)
                     && p.Name.EndsWith("Encrypted", StringComparison.Ordinal))
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}")
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        discovered.Should().BeSubsetOf(WalkedEncryptedProperties,
            "every non-settings *Encrypted domain property must be re-encrypted by " +
            "DekRotationWalk.ReEncryptAllAsync. If you added one, walk it there AND add " +
            "it to WalkedEncryptedProperties here. (Variable.Value / VariableSnapshot.Value " +
            "are walked via Type==Sensitive and are not *Encrypted-named; " +
            "DataEncryptionKey.WrappedDek is the DEK itself and must NOT be walked.)");
    }

    [Fact]
    public void Every_secret_bearing_settings_document_is_registered_for_generic_rotation()
    {
        var domainAssembly = typeof(SpaceAiSettings).Assembly;

        var secretDocuments = domainAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                     && typeof(ISettingsDocument).IsAssignableFrom(t))
            .Where(t => t.GetProperties().Any(p =>
                p.PropertyType == typeof(string)
                && p.Name.EndsWith("Encrypted", StringComparison.Ordinal)))
            .ToList();

        // Sanity anchor: the known secret-bearing documents are present, so a
        // refactor that drops one from the domain doesn't quietly pass this test.
        secretDocuments.Select(t => t.Name).Should()
            .Contain([nameof(SpaceAiSettings), "SmtpSettings", nameof(CatalogSettings)]);

        var catalogTypes = SettingsDocumentCatalog.All.Select(d => d.ClrType).ToHashSet();
        foreach (var document in secretDocuments)
        {
            catalogTypes.Should().Contain(document,
                $"{document.Name} carries a *Encrypted member, so it MUST be registered in " +
                "SettingsDocumentCatalog for DekRotationWalk's generic settings step to " +
                "re-encrypt it — otherwise a DEK rotation silently bricks its secret.");

            var descriptor = SettingsDocumentCatalog.All.Single(d => d.ClrType == document);
            descriptor.EncryptedMembers.Should().NotBeEmpty(
                $"the catalog must expose {document.Name}'s *Encrypted members for re-encryption.");
        }
    }

    [Fact]
    public void Every_settings_document_Encrypted_member_is_a_string()
    {
        // The rotation walk + catalog only handle STRING *Encrypted members. A
        // non-string one (e.g. byte[] TokenEncrypted) would be silently skipped by
        // BOTH the walk and the completeness guard above (which detects secrets via
        // the string filter). Codify the convention so such a member fails CI and
        // forces an explicit decision rather than a silent brick on rotation.
        var domainAssembly = typeof(SpaceAiSettings).Assembly;

        var nonStringEncrypted = domainAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                     && typeof(ISettingsDocument).IsAssignableFrom(t))
            .SelectMany(t => t.GetProperties())
            .Where(p => p.Name.EndsWith("Encrypted", StringComparison.Ordinal)
                     && p.PropertyType != typeof(string))
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name} : {p.PropertyType.Name}")
            .ToList();

        nonStringEncrypted.Should().BeEmpty(
            "every *Encrypted member on an ISettingsDocument must be a string — the DEK " +
            "rotation walk and SettingsDocumentCatalog only re-encrypt string members.");
    }
}

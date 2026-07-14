using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using KrakenDeploy.Server.Core.Domain.Settings;

namespace KrakenDeploy.Server.Data.Settings;

/// <summary>
/// Reflection registry over every <see cref="ISettingsDocument"/> implementer in
/// the <c>Server.Core</c> domain assembly. Discovering documents (not a hand-kept
/// list) is load-bearing: the DEK-rotation walk re-encrypts the <c>*Encrypted</c>
/// members of every registered document generically, so a new secret-bearing
/// settings document can never be silently missed by a key rotation.
/// </summary>
public static class SettingsDocumentCatalog
{
    /// <summary>
    /// Serialization options for settings payloads. Web defaults (camelCase,
    /// case-insensitive read) plus a string enum converter so enum members
    /// round-trip as stable names, not ordinals.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>One discovered settings-document type and its metadata.</summary>
    public sealed record DocumentDescriptor(
        Type ClrType,
        string Key,
        SettingsScope Scope,
        IReadOnlyList<PropertyInfo> EncryptedMembers);

    private static readonly IReadOnlyList<DocumentDescriptor> DescriptorsList = Discover();

    private static readonly IReadOnlyDictionary<string, DocumentDescriptor> ByKey =
        DescriptorsList.ToDictionary(d => d.Key, StringComparer.Ordinal);

    /// <summary>All discovered settings documents.</summary>
    public static IReadOnlyList<DocumentDescriptor> All => DescriptorsList;

    /// <summary>The document registered for <paramref name="key"/>, or null.</summary>
    public static DocumentDescriptor? Find(string key) => ByKey.GetValueOrDefault(key);

    private static List<DocumentDescriptor> Discover()
    {
        var result = new List<DocumentDescriptor>();
        foreach (var type in typeof(ISettingsDocument).Assembly.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract || !typeof(ISettingsDocument).IsAssignableFrom(type))
            {
                continue;
            }

            var key = (string)type
                .GetProperty(nameof(ISettingsDocument.Key), BindingFlags.Public | BindingFlags.Static)!
                .GetValue(null)!;
            var scope = (SettingsScope)type
                .GetProperty(nameof(ISettingsDocument.Scope), BindingFlags.Public | BindingFlags.Static)!
                .GetValue(null)!;

            var encrypted = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(string)
                         && p.Name.EndsWith("Encrypted", StringComparison.Ordinal)
                         && p is { CanRead: true, CanWrite: true })
                .ToList();

            result.Add(new DocumentDescriptor(type, key, scope, encrypted));
        }

        var duplicates = result
            .GroupBy(d => (d.Scope, d.Key))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Scope}/{g.Key.Key}")
            .ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                "Duplicate ISettingsDocument key(s) within a scope: " + string.Join(", ", duplicates) +
                ". Each (scope, key) pair must map to exactly one document type.");
        }

        return result;
    }
}

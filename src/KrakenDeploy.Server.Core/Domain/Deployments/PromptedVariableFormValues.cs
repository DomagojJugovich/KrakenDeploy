namespace KrakenDeploy.Server.Core.Domain.Deployments;

/// <summary>Deployment-time variable overrides stored in server_tasks.form_values.</summary>
public sealed class PromptedVariableFormValues
{
    public required int Version { get; init; }
    public Dictionary<string, string> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// AES-GCM ciphertext containing a serialized string dictionary. The suffix is
    /// load-bearing for the DEK rotation completeness convention.
    /// </summary>
    public string? SensitiveValuesEncrypted { get; set; }
}

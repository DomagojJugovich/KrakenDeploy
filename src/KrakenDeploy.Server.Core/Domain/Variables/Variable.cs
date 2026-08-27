using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Variables;

/// <summary>
/// A single named variable within a <see cref="VariableSet"/>.
/// <para>
/// For <see cref="VariableType.Sensitive"/> variables, <see cref="Value"/> holds
/// AES-256-GCM ciphertext (base64-encoded nonce + auth-tag + ciphertext).
/// Decryption happens server-side in <c>VariableService</c> during deployment planning.
/// </para>
/// <para>
/// For <see cref="VariableType.StringArray"/> variables, <see cref="Value"/> holds
/// a JSON array of strings, e.g. <c>["a","b","c"]</c>.
/// </para>
/// </summary>
public class Variable : AuditableEntity, ISpaceScoped
{
    /// <summary>Inherited from the owning VariableSet; stamped on insert and
    /// backfilled for existing rows so by-id reads/mutations are Space-safe.</summary>
    public Guid SpaceId { get; set; }

    public Guid SetId { get; set; }
    public VariableSet Set { get; set; } = null!;

    /// <summary>Variable name (case-insensitive key for scope resolution).</summary>
    public required string Name { get; set; }

    /// <summary>
    /// Raw stored value. Interpret according to <see cref="Type"/>:
    /// plain string, base64-encrypted ciphertext, or JSON array.
    /// </summary>
    public required string Value { get; set; }

    public VariableType Type { get; set; }

    public bool IsPrompted { get; set; }
    public string? PromptLabel { get; set; }
    public string? PromptDescription { get; set; }
    public bool PromptRequired { get; set; }
    public PromptControlType PromptControl { get; set; }
    public List<string> PromptOptions { get; set; } = [];

    /// <summary>Scope constraints stored as <c>jsonb</c>.</summary>
    public VariableScope Scope { get; set; } = new();
}

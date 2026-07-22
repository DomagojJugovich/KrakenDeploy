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

    /// <summary>
    /// When non-null, the operator is prompted for this variable's value at
    /// deployment time. The string is the prompt label/description shown in
    /// the deploy dialog. <c>null</c> = not prompted (the stored <see cref="Value"/>
    /// is used as-is). Octopus import maps <c>Prompt.Label</c> / <c>Prompt.Description</c>
    /// into this field.
    /// </summary>
    public string? PromptText { get; set; }

    /// <summary>
    /// When <c>true</c> and <see cref="PromptText"/> is set, the deployment
    /// cannot proceed until the operator supplies a non-empty value.
    /// </summary>
    public bool PromptRequired { get; set; }

    /// <summary>Scope constraints stored as <c>jsonb</c>.</summary>
    public VariableScope Scope { get; set; } = new();
}

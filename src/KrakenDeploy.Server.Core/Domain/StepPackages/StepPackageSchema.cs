using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.StepPackages;

/// <summary>
/// One step type's UI schema as shipped by one installed package VERSION
/// (SC2 / SD-5): the extracted contents of the archive's
/// <c>ui/schemas/{typeId}.json</c>. Keyed (package row, type) so the editor
/// can render exactly the schema of the version a step is pinned to, and the
/// D-7.2 version-switch diff can compare versions without re-reading zips.
/// Rows die with their package row (cascade).
/// </summary>
public class StepPackageSchema : AuditableEntity
{
    /// <summary>FK to the owning (name, version) install.</summary>
    public required Guid StepPackageId { get; set; }

    /// <summary>Lower-cased step-type id this schema renders.</summary>
    public required string StepType { get; set; }

    /// <summary>The <c>StepUiSchema</c> JSON, verbatim from the archive.</summary>
    public required string SchemaJson { get; set; }
}

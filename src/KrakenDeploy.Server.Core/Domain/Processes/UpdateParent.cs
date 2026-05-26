namespace KrakenDeploy.Server.Core.Domain.Processes;

/// <summary>
/// M15 — wrapper for the optional parent-reassignment parameter on the
/// step-update services (<c>ProcessService.UpdateStepAsync</c> + the
/// runbook equivalent). A bare <c>Guid?</c> couldn't distinguish "leave
/// the parent alone" from "make this step a top-level step (null
/// parent)". Callers pass <see cref="To(Guid?)"/> when they want to
/// change the parent and <c>null</c> (the parameter's default) when
/// they want to leave it alone.
///
/// <para>
/// Lives in <c>Server.Core</c> so both <c>ProcessService</c> and the
/// runbook step-update path can share the type without one service
/// depending on the other.
/// </para>
/// </summary>
public sealed record UpdateParent(Guid? NewParentStepId)
{
    /// <summary>Create an <see cref="UpdateParent"/> pointing at a
    /// specific parent (or <c>null</c> for top-level).</summary>
    public static UpdateParent To(Guid? parentStepId) => new(parentStepId);
}

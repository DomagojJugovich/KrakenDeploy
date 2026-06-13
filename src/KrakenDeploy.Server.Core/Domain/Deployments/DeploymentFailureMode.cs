namespace KrakenDeploy.Server.Core.Domain.Deployments;

/// <summary>
/// How a multi-target ("rolling") deployment reacts when a target fails a
/// Required step. Chosen per deployment (defaulted at create time).
/// </summary>
public enum DeploymentFailureMode
{
    /// <summary>
    /// Default. The failing target is dropped and the SURVIVING targets run
    /// their remaining steps to completion — a step's <c>Condition=Success</c>
    /// is evaluated per target (the prior step's outcome on that same target),
    /// not as a global cross-target barrier. A non-required failure taints only
    /// that target. Use when partial progress is valuable (e.g. keep deploying
    /// the rest of a web/RDS farm when one node is out). Terminal status is
    /// <c>SucceededWithWarnings</c> when some targets dropped but others
    /// completed; <c>Failed</c> only when every target dropped.
    /// </summary>
    BestEffort = 0,

    /// <summary>
    /// A failure on ANY target puts the whole deployment into a failing state:
    /// the surviving targets' remaining <c>Condition=Success</c> steps are
    /// skipped and their <c>Condition=Failure</c>/<c>Always</c> steps run, so a
    /// half-applied change can be cleaned up / rolled back across the farm to
    /// keep every target on the same version. Terminal status is <c>Failed</c>.
    /// </summary>
    Atomic = 1,
}

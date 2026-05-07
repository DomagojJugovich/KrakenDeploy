using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Tenants;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Tenant × Environment deployment matrix for the project dashboard.
/// <see cref="Cells"/> is keyed by <c>(TenantId, EnvironmentId)</c> — a missing
/// key means there has been no deployment yet for that combination.
/// </summary>
public sealed record ProjectDashboardMatrix(
    IReadOnlyList<Tenant> Tenants,
    IReadOnlyList<DeploymentEnvironment> Environments,
    IReadOnlyDictionary<(Guid TenantId, Guid EnvironmentId), DashboardCell> Cells);

/// <summary>
/// One filled cell in the dashboard matrix. Holds just enough metadata to render
/// the version, channel, status and timestamp without further DB queries.
/// </summary>
public sealed record DashboardCell(
    Guid DeploymentId,
    DeploymentStatus Status,
    string ReleaseVersion,
    string? ChannelName,
    DateTimeOffset CreatedUtc);

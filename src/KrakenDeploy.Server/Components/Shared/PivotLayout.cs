using KrakenDeploy.Server.Data.Services;
using Radzen;

namespace KrakenDeploy.Server.Components;

/// <summary>
/// Serializable pivot layout for the dashboard analytics table — which fact
/// fields sit in rows / columns / values and with which aggregate functions.
/// Owned by the UI (saved to PivotView.Definition as JSON); kept deliberately
/// dumb so the storage schema doesn't churn as the analytics UI grows.
/// </summary>
public sealed record PivotLayout(
    List<string> Rows,
    List<string> Columns,
    List<PivotMeasure> Values);

public sealed record PivotMeasure(string Property, AggregateFunction Function);

/// <summary>Catalog of pivotable fields on <see cref="DeploymentFact"/> with
/// display titles, plus the default layout and per-measure formatting.</summary>
public static class PivotFields
{
    public static readonly (string Property, string Title)[] Catalog =
    [
        (nameof(DeploymentFact.Project), "Project"),
        (nameof(DeploymentFact.Tenant), "Tenant"),
        (nameof(DeploymentFact.Environment), "Environment"),
        (nameof(DeploymentFact.Release), "Release"),
        (nameof(DeploymentFact.Channel), "Channel"),
        (nameof(DeploymentFact.Target), "Target"),
        (nameof(DeploymentFact.Status), "Status"),
        (nameof(DeploymentFact.Day), "Day"),
        (nameof(DeploymentFact.Week), "Week"),
        (nameof(DeploymentFact.Month), "Month"),
        (nameof(DeploymentFact.DeploymentId), "Deployments"),
        (nameof(DeploymentFact.IsFailure), "Failures"),
        (nameof(DeploymentFact.IsSuccess), "Successes"),
        (nameof(DeploymentFact.DurationSeconds), "Duration (s)"),
    ];

    /// <summary>The Count-of-deployments measure — rendered with the drill-through link.</summary>
    public const string CountProperty = nameof(DeploymentFact.DeploymentId);

    public static PivotLayout Default() => new(
        Rows: [nameof(DeploymentFact.Project), nameof(DeploymentFact.Tenant)],
        Columns: [nameof(DeploymentFact.Environment)],
        Values:
        [
            new PivotMeasure(nameof(DeploymentFact.DeploymentId), AggregateFunction.Count),
            new PivotMeasure(nameof(DeploymentFact.IsFailure), AggregateFunction.Sum),
            new PivotMeasure(nameof(DeploymentFact.DurationSeconds), AggregateFunction.Average),
        ]);

    public static string Title(string property)
        => Catalog.FirstOrDefault(f => f.Property == property).Title ?? property;

    public static string MeasureTitle(PivotMeasure v) => (v.Property, v.Function) switch
    {
        (nameof(DeploymentFact.IsFailure), AggregateFunction.Sum) => "Failed",
        (nameof(DeploymentFact.IsSuccess), AggregateFunction.Sum) => "Succeeded",
        (nameof(DeploymentFact.DurationSeconds), AggregateFunction.Average) => "Avg duration (s)",
        _ => $"{v.Function} {Title(v.Property)}",
    };

    public static string? MeasureFormat(PivotMeasure v)
        => v.Property == nameof(DeploymentFact.DurationSeconds) ? "{0:N0}" : null;

    /// <summary>Catalog properties not already used as a value (declared as
    /// unselected aggregates so the picker pool is complete).</summary>
    public static IEnumerable<string> Spare(PivotLayout layout)
        => Catalog.Select(f => f.Property).Where(p => !layout.Values.Any(v => v.Property == p));
}

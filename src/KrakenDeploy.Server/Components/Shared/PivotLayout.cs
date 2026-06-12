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

/// <summary>A selectable field for the layout controls. A real type (not a
/// value tuple) so Radzen dropdowns can reflect Property/Title at runtime —
/// tuple element names are compile-time only.</summary>
public sealed record PivotFieldOption(string Property, string Title);

/// <summary>Catalog of pivotable fields on <see cref="DeploymentFact"/> with
/// display titles, plus the default layout and per-measure formatting.</summary>
public static class PivotFields
{
    public static readonly IReadOnlyList<PivotFieldOption> Catalog =
    [
        new(nameof(DeploymentFact.Project), "Project"),
        new(nameof(DeploymentFact.Tenant), "Tenant"),
        new(nameof(DeploymentFact.Environment), "Environment"),
        new(nameof(DeploymentFact.Release), "Release"),
        new(nameof(DeploymentFact.Channel), "Channel"),
        new(nameof(DeploymentFact.Target), "Target"),
        new(nameof(DeploymentFact.Status), "Status"),
        new(nameof(DeploymentFact.Day), "Day"),
        new(nameof(DeploymentFact.Week), "Week"),
        new(nameof(DeploymentFact.Month), "Month"),
        new(nameof(DeploymentFact.DeploymentId), "Deployments"),
        new(nameof(DeploymentFact.IsFailure), "Failures"),
        new(nameof(DeploymentFact.IsSuccess), "Successes"),
        new(nameof(DeploymentFact.DurationSeconds), "Duration (s)"),
    ];

    /// <summary>The Count-of-deployments measure — rendered with the drill-through link.</summary>
    public const string CountProperty = nameof(DeploymentFact.DeploymentId);

    /// <summary>Fields offered as Rows / Columns (categorical, not the numeric measures).</summary>
    public static readonly IReadOnlyList<PivotFieldOption> Dimensions =
    [
        new(nameof(DeploymentFact.Project), "Project"),
        new(nameof(DeploymentFact.Tenant), "Tenant"),
        new(nameof(DeploymentFact.Environment), "Environment"),
        new(nameof(DeploymentFact.Release), "Release"),
        new(nameof(DeploymentFact.Channel), "Channel"),
        new(nameof(DeploymentFact.Target), "Target"),
        new(nameof(DeploymentFact.Status), "Status"),
        new(nameof(DeploymentFact.Day), "Day"),
        new(nameof(DeploymentFact.Week), "Week"),
        new(nameof(DeploymentFact.Month), "Month"),
    ];

    private static readonly string[] NumericFields =
    [
        nameof(DeploymentFact.IsFailure),
        nameof(DeploymentFact.IsSuccess),
        nameof(DeploymentFact.DurationSeconds),
    ];

    /// <summary>Aggregate functions valid for a field as a Value: count-of-rows
    /// for the deployment id, numeric stats for the numeric measures, and
    /// Count/First/Last for categorical fields (so "Release · Last" works).</summary>
    public static IReadOnlyList<AggregateFunction> FunctionsFor(string property)
    {
        if (property == CountProperty)
        {
            return [AggregateFunction.Count];
        }
        if (NumericFields.Contains(property))
        {
            return [AggregateFunction.Sum, AggregateFunction.Average, AggregateFunction.Min, AggregateFunction.Max, AggregateFunction.Count];
        }
        return [AggregateFunction.Count, AggregateFunction.First, AggregateFunction.Last];
    }

    public static AggregateFunction DefaultFunction(string property) => FunctionsFor(property)[0];

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
        => Catalog.FirstOrDefault(f => f.Property == property)?.Title ?? property;

    public static string MeasureTitle(PivotMeasure v) => (v.Property, v.Function) switch
    {
        (nameof(DeploymentFact.IsFailure), AggregateFunction.Sum) => "Failed",
        (nameof(DeploymentFact.IsSuccess), AggregateFunction.Sum) => "Succeeded",
        (nameof(DeploymentFact.DurationSeconds), AggregateFunction.Average) => "Avg duration (s)",
        _ => $"{v.Function} {Title(v.Property)}",
    };

    public static string? MeasureFormat(PivotMeasure v)
        => v.Property == nameof(DeploymentFact.DurationSeconds) ? "{0:N0}" : null;
}

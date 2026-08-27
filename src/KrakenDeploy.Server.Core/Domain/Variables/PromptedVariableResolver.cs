using KrakenDeploy.Server.Core.Domain.Releases;

namespace KrakenDeploy.Server.Core.Domain.Variables;

public sealed record PromptedVariableContext(
    Guid EnvironmentId,
    Guid TargetId,
    IReadOnlyList<string> TargetRoles,
    Guid? TenantId,
    Guid? ChannelId,
    IReadOnlyList<Guid>? TenantTagIds);

public sealed record PromptedVariableDefinition(
    string Name,
    string Label,
    string? Description,
    bool Required,
    PromptControlType Control,
    IReadOnlyList<string> Options,
    bool Sensitive);

/// <summary>Applies the same scope and layer ordering as variable resolution.</summary>
public static class PromptedVariableResolver
{
    public static VariableSnapshot? FindWinner(
        IReadOnlyList<VariableSnapshot> snapshot,
        string name,
        PromptedVariableContext context,
        Guid? stepId = null)
        => snapshot
            .Where(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
            .Where(v => v.Scope.Matches(
                context.EnvironmentId,
                context.TargetId,
                context.TargetRoles,
                context.TenantId,
                context.ChannelId,
                stepId,
                context.TenantTagIds))
            .OrderByDescending(v => v.Scope.SpecificityScore())
            .ThenByDescending(v => v.Layer)
            .FirstOrDefault();

    public static IReadOnlyList<PromptedVariableDefinition> GetApplicable(
        IReadOnlyList<VariableSnapshot> snapshot,
        IReadOnlyList<PromptedVariableContext> contexts,
        IReadOnlyList<Guid> stepIds)
    {
        var winners = new List<VariableSnapshot>();
        foreach (var context in contexts)
        {
            AddWinners(snapshot, context, stepId: null, winners);
            foreach (var stepId in stepIds)
            {
                AddWinners(snapshot, context, stepId, winners);
            }
        }

        return winners
            .Where(v => v.IsPrompted)
            .GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var display = g
                    .OrderByDescending(v => v.Scope.SpecificityScore())
                    .ThenByDescending(v => v.Layer)
                    .First();
                return new PromptedVariableDefinition(
                    display.Name,
                    string.IsNullOrWhiteSpace(display.PromptLabel) ? display.Name : display.PromptLabel,
                    display.PromptDescription,
                    g.Any(v => v.PromptRequired),
                    display.PromptControl,
                    display.PromptOptions ?? [],
                    g.Any(v => v.Type == VariableType.Sensitive));
            })
            .OrderBy(v => v.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddWinners(
        IReadOnlyList<VariableSnapshot> snapshot,
        PromptedVariableContext context,
        Guid? stepId,
        List<VariableSnapshot> winners)
    {
        foreach (var group in snapshot.GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
        {
            var winner = group
                .Where(v => v.Scope.Matches(
                    context.EnvironmentId,
                    context.TargetId,
                    context.TargetRoles,
                    context.TenantId,
                    context.ChannelId,
                    stepId,
                    context.TenantTagIds))
                .OrderByDescending(v => v.Scope.SpecificityScore())
                .ThenByDescending(v => v.Layer)
                .FirstOrDefault();
            if (winner is not null)
            {
                winners.Add(winner);
            }
        }
    }
}

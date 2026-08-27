namespace KrakenDeploy.Server.Core.Domain.Variables;

/// <summary>Operator-input definition frozen into a release with its variable.</summary>
public sealed record VariablePromptSettings(
    bool IsPrompted = false,
    string? Label = null,
    string? Description = null,
    bool Required = false,
    PromptControlType Control = PromptControlType.Text,
    IReadOnlyList<string>? Options = null);

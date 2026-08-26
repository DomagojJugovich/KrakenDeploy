using KrakenDeploy.Server.Core.Domain.Settings;
using Serilog.Core;
using Serilog.Events;

namespace KrakenDeploy.Server.Observability;

/// <summary>Process-wide Serilog switches updated by the operational settings UI.</summary>
public sealed class RuntimeLoggingSettings
{
    private readonly Dictionary<string, LoggingLevelSwitch> _overrides;

    public RuntimeLoggingSettings(OperationalSettings startup)
    {
        MinimumLevel = new LoggingLevelSwitch(Parse(startup.SerilogMinimumLevel));
        _overrides = startup.SerilogCategoryOverrides.ToDictionary(
            pair => pair.Key,
            pair => new LoggingLevelSwitch(Parse(pair.Value)),
            StringComparer.Ordinal);
    }

    public LoggingLevelSwitch MinimumLevel { get; }

    public IReadOnlyDictionary<string, LoggingLevelSwitch> Overrides => _overrides;

    public void Apply(OperationalSettings settings)
    {
        MinimumLevel.MinimumLevel = Parse(settings.SerilogMinimumLevel);
        foreach (var (category, level) in settings.SerilogCategoryOverrides)
        {
            if (_overrides.TryGetValue(category, out var levelSwitch))
            {
                levelSwitch.MinimumLevel = Parse(level);
            }
        }
    }

    public static LogEventLevel Parse(string value) =>
        Enum.TryParse<LogEventLevel>(value, ignoreCase: true, out var level)
            ? level
            : throw new ArgumentException($"'{value}' is not a valid Serilog level.", nameof(value));
}

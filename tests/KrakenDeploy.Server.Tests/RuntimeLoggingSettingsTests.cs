using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Settings;
using KrakenDeploy.Server.Observability;
using Serilog.Events;

namespace KrakenDeploy.Server.Tests;

public sealed class RuntimeLoggingSettingsTests
{
    [Fact]
    public void Apply_changes_global_and_category_levels_without_rebuilding_logger()
    {
        var runtime = new RuntimeLoggingSettings(new OperationalSettings());
        var updated = new OperationalSettings
        {
            SerilogMinimumLevel = "Debug",
            SerilogCategoryOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Microsoft"] = "Error",
                ["Microsoft.AspNetCore"] = "Warning",
                ["Microsoft.EntityFrameworkCore"] = "Warning",
                ["System.Net.Http.HttpClient"] = "Warning",
            },
        };

        runtime.Apply(updated);

        runtime.MinimumLevel.MinimumLevel.Should().Be(LogEventLevel.Debug);
        runtime.Overrides["Microsoft"].MinimumLevel.Should().Be(LogEventLevel.Error);
    }
}

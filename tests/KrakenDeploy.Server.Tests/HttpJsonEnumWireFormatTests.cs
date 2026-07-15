using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Deployments;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// Pins the minimal-API JSON wire contract produced by
/// <see cref="Program.ConfigureHttpJson"/>. The bug this guards against: the
/// server serialized enums as INTEGERS while the CLI (and the intended REST
/// contract) expect enum NAMES, so <c>kraken release deploy</c> /
/// <c>kraken target list</c> threw <c>JsonException</c> converting a JSON
/// number into their string status fields. These tests exercise the real
/// configuration method (not a hand-copied options object) so the contract
/// cannot silently drift again.
/// </summary>
public sealed class HttpJsonEnumWireFormatTests
{
    private static JsonSerializerOptions ServerOptions()
    {
        // Mirror how ASP.NET seeds Http.Json options, then apply OUR contract.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Program.ConfigureHttpJson(options);
        return options;
    }

    [Fact]
    public void Enum_is_serialized_as_its_name_not_a_number()
    {
        var options = ServerOptions();

        var json = JsonSerializer.Serialize(
            new Deployment { Status = DeploymentStatus.SucceededWithWarnings }, options);

        // The exact status the CLI --wait loop mishandled before commit 9f3b94b:
        // it must arrive as the enum name so the string-typed DTO binds it.
        json.Should().Contain("\"SucceededWithWarnings\"");
        json.Should().NotContain("\"status\":6",
            because: "enums must go over the wire as names, not integers");
    }

    [Theory]
    [InlineData(DeploymentStatus.Queued)]
    [InlineData(DeploymentStatus.Succeeded)]
    [InlineData(DeploymentStatus.SucceededWithWarnings)]
    [InlineData(DeploymentStatus.Failed)]
    [InlineData(DeploymentStatus.Cancelled)]
    [InlineData(DeploymentStatus.PendingOfflineResult)]
    public void Every_deployment_status_round_trips_by_name(DeploymentStatus status)
    {
        var options = ServerOptions();

        var json = JsonSerializer.Serialize(new Deployment { Status = status }, options);
        json.Should().Contain($"\"{status}\"",
            because: "the wire value must equal DeploymentStatus.ToString() — the CLI compares names");

        var round = JsonSerializer.Deserialize<Deployment>(json, options);
        round!.Status.Should().Be(status);
    }

    [Fact]
    public void Numeric_enum_token_is_still_accepted_on_input()
    {
        var options = ServerOptions();

        // Request-body back-compat: a caller that still POSTs a numeric enum
        // must keep binding (JsonStringEnumConverter reads both token kinds).
        var fromNumber = JsonSerializer.Deserialize<Deployment>(
            "{\"status\":6}", options);
        fromNumber!.Status.Should().Be(DeploymentStatus.SucceededWithWarnings);

        // And the new string form binds too.
        var fromName = JsonSerializer.Deserialize<Deployment>(
            "{\"status\":\"SucceededWithWarnings\"}", options);
        fromName!.Status.Should().Be(DeploymentStatus.SucceededWithWarnings);
    }

    [Fact]
    public void Cycle_handling_is_preserved()
    {
        var options = ServerOptions();

        // Regression guard: the enum-converter addition must not drop the
        // pre-existing IgnoreCycles behavior that keeps EF navigation graphs
        // from throwing "possible object cycle detected" → 500.
        options.ReferenceHandler.Should().Be(ReferenceHandler.IgnoreCycles);
    }
}

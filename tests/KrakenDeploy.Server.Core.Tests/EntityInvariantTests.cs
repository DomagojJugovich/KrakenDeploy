using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Targets;

namespace KrakenDeploy.Server.Core.Tests;

public class EntityInvariantTests
{
    [Fact]
    public void DeploymentTarget_defaults_are_correct()
    {
        var target = new DeploymentTarget { Name = "test" };

        target.Id.Should().NotBe(Guid.Empty, "Entity base class generates a V7 Guid on construction");
        target.Status.Should().Be(TargetStatus.Unknown);
        target.TransportMode.Should().Be(TransportMode.Reverse);
        target.Roles.Should().NotBeNull("Roles is initialized to an empty list, never null");
        target.Roles.Should().BeEmpty();
        target.LastSeenUtc.Should().BeNull();
        target.MachineName.Should().BeNull();
        target.OperatingSystem.Should().BeNull();
        target.AgentVersion.Should().BeNull();
        target.RegistrationKeyHash.Should().BeNull();
        target.RegistrationTokenExpiresUtc.Should().BeNull();
    }

    [Fact]
    public void Entity_generates_unique_ids_for_every_instance()
    {
        var ids = Enumerable.Range(0, 20)
            .Select(i => new DeploymentTarget { Name = $"t{i}" }.Id)
            .ToList();

        ids.Should().OnlyHaveUniqueItems(because: "Guid.CreateVersion7() must be unique per call");
    }

    [Fact]
    public void Entity_id_is_not_empty()
    {
        var target = new DeploymentTarget { Name = "x" };
        target.Id.Should().NotBe(Guid.Empty);
    }
}
